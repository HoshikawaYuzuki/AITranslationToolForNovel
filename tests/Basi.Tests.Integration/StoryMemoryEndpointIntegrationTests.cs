using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basi.Api.Data;
using Basi.Api.Workers;
using Basi.Core.Interfaces;
using Basi.Core.Models;
using Basi.Module.Translation.Providers;
using Basi.Module.Translation.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Basi.Tests.Integration;

public sealed class StoryMemoryTestFactory : AuthTestFactory
{
    public Func<Guid, string, IAiProvider>? ProviderSelector { get; set; }
    public bool DisableBackgroundWorker { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            if (DisableBackgroundWorker)
            {
                var hostedWorker = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(TranslationQueueWorker));
                if (hostedWorker != null) services.Remove(hostedWorker);
            }

            var factoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAiProviderFactory));
            if (factoryDescriptor != null) services.Remove(factoryDescriptor);

            services.AddScoped<IAiProviderFactory>(sp =>
                new StoryMemoryTestProviderFactory(sp.GetRequiredService<IApiKeyVault>(), this));
        });
    }

    private sealed class StoryMemoryTestProviderFactory : IAiProviderFactory
    {
        private readonly IApiKeyVault _vault;
        private readonly StoryMemoryTestFactory _owner;
        private readonly MockAiProvider _fallbackMock = new();

        public StoryMemoryTestProviderFactory(IApiKeyVault vault, StoryMemoryTestFactory owner)
        {
            _vault = vault;
            _owner = owner;
        }

        public IAiProvider CreateProvider(string providerName, string apiKey) =>
            _owner.ProviderSelector != null ? _owner.ProviderSelector(Guid.Empty, providerName) : _fallbackMock;

        public IAiProvider GetProviderForUser(Guid userId, string providerName)
        {
            var norm = providerName.Trim().ToLowerInvariant();
            if (norm != "mock" && string.IsNullOrWhiteSpace(_vault.GetKey(userId, norm)))
            {
                throw new AiProviderError(AiErrorCode.InvalidApiKey, $"Missing key for {providerName}");
            }
            return _owner.ProviderSelector != null ? _owner.ProviderSelector(Guid.Empty, norm) : _fallbackMock;
        }

        public IAiProvider GetProviderForJob(Guid userId, string providerName, Guid jobId)
        {
            var norm = providerName.Trim().ToLowerInvariant();
            if (norm != "mock" && string.IsNullOrWhiteSpace(_vault.GetJobKey(userId, jobId) ?? _vault.GetKey(userId, norm)))
            {
                throw new AiProviderError(AiErrorCode.InvalidApiKey, $"Missing key for {providerName}");
            }
            return _owner.ProviderSelector != null ? _owner.ProviderSelector(jobId, norm) : _fallbackMock;
        }
    }
}

internal sealed class StoryMemoryDelegateProvider : IAiProvider
{
    private readonly Func<AiRequest, CancellationToken, Task<AiResponse>> _handler;
    public StoryMemoryDelegateProvider(Func<AiRequest, CancellationToken, Task<AiResponse>> handler) => _handler = handler;
    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default) => _handler(request, ct);
}

public class StoryMemoryEndpointIntegrationTests
{
    private static async Task<(Guid NovelId, List<Segment> Segments, Guid OwnerId, string GoogleId, string Email)> SeedNovelAsync(
        StoryMemoryTestFactory factory,
        string[] segmentContents)
    {
        var googleId = $"google-sm-{Guid.NewGuid():N}";
        var email = $"sm-{Guid.NewGuid():N}@example.com";
        var user = await factory.SeedUserAsync(googleId, email, "Story Owner");

        var novelId = Guid.NewGuid();
        var seededSegments = new List<Segment>();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();

        var novel = new Novel
        {
            Id = novelId,
            OwnerId = user.Id,
            Title = "Story Memory Novel",
            SourceLanguage = "ja",
            RawContent = string.Join("\n", segmentContents),
            UploadedAt = DateTime.UtcNow
        };

        for (var i = 0; i < segmentContents.Length; i++)
        {
            var seg = new Segment
            {
                Id = Guid.NewGuid(),
                NovelId = novelId,
                StableId = $"{novelId:N}:{i:D6}",
                Order = i,
                Content = segmentContents[i]
            };
            novel.Segments.Add(seg);
            seededSegments.Add(seg);
        }

        db.Novels.Add(novel);
        await db.SaveChangesAsync();
        return (novelId, seededSegments, user.Id, googleId, email);
    }

    private static HttpClient CreateAuthedClient(StoryMemoryTestFactory factory, string googleId, string email)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        return client;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(
        StoryMemoryTestFactory factory,
        HttpClient client,
        HttpMethod method,
        string url,
        object? body = null)
    {
        var token = await factory.FetchAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-XSRF-TOKEN", token);
        if (body != null) req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task StoryMemoryEndpoints_RequireAuthCsrf_AndIsolateAcrossOwners()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, segments, _, ownerGoogleId, ownerEmail) = await SeedNovelAsync(factory, new[] { "「アインズ」は立ち上がった。" });

        var anonClient = factory.CreateClient();
        anonClient.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync($"/api/novels/{novelId}/story-memory")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync($"/api/novels/{novelId}/scene-facts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync($"/api/novels/{novelId}/speech-observations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync($"/api/translations/{Guid.NewGuid()}/consistency")).StatusCode);

        var ownerClient = CreateAuthedClient(factory, ownerGoogleId, ownerEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync($"/api/novels/{novelId}/story-memory", new { targetLanguage = "vi", sourceTerm = "A", translatedTerm = "B" })).StatusCode);

        var createRes = await SendWithCsrfAsync(factory, ownerClient, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "アインズ",
            translatedTerm = "Ainz",
            category = "Character",
            evidenceSegmentId = segments[0].Id,
            evidenceQuote = "アインズ"
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var otherGoogleId = $"google-other-{Guid.NewGuid():N}";
        var otherEmail = $"other-{Guid.NewGuid():N}@example.com";
        await factory.SeedUserAsync(otherGoogleId, otherEmail, "Other");
        var otherClient = CreateAuthedClient(factory, otherGoogleId, otherEmail);

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/novels/{novelId}/story-memory")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/novels/{novelId}/scene-facts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/novels/{novelId}/speech-observations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(factory, otherClient, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new { targetLanguage = "vi", provider = "mock" })).StatusCode);
    }

    [Fact]
    public async Task UnicodeNfkcAndSurrogateValidation_Returns400_AndValidBoundarySucceeds()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, segments, _, googleId, email) = await SeedNovelAsync(factory, new[] { "Sample line" });
        var client = CreateAuthedClient(factory, googleId, email);

        // B004-04: U+337F x 100 has display length 100, expands to NFKC length 400 (>256) -> must reject 400
        var expanded100 = new string('\u337F', 100);
        var tooLongRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = expanded100,
            translatedTerm = "Corp"
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooLongRes.StatusCode);

        // Unpaired surrogate -> 400 (raw JSON literal \uD800; JsonContent sanitizes invalid surrogates client-side)
        var surrogateCsrf = await factory.FetchAntiforgeryTokenAsync(client);
        using var surrogateReq = new HttpRequestMessage(HttpMethod.Post, $"/api/novels/{novelId}/story-memory")
        {
            Content = new StringContent(
                """{"targetLanguage":"vi","sourceTerm":"Bad\uD800Term","translatedTerm":"Bad"}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        surrogateReq.Headers.Add("X-XSRF-TOKEN", surrogateCsrf);
        var badSurrogateRes = await client.SendAsync(surrogateReq);
        Assert.Equal(HttpStatusCode.BadRequest, badSurrogateRes.StatusCode);

        // Quote mismatch against segment -> 400
        var badQuoteRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "ValidTerm",
            translatedTerm = "ValidTerm",
            evidenceSegmentId = segments[0].Id,
            evidenceQuote = "Not present in segment"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badQuoteRes.StatusCode);

        // Exact NFKC boundary: U+337F x 64 -> display 64, NFKC 256 -> succeeds cleanly
        var boundary64 = new string('\u337F', 64);
        var okBoundaryRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = boundary64,
            translatedTerm = "Công ty Cổ phần"
        });
        Assert.Equal(HttpStatusCode.Created, okBoundaryRes.StatusCode);
    }

    [Fact]
    public async Task Extraction_StrictValidationAndProvisionalStatusAndConflict_EnforcedEndToEnd()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, segments, _, googleId, email) = await SeedNovelAsync(factory, new[]
        {
            "「アインズ」はナザリックに立っていた。"
        });
        var client = CreateAuthedClient(factory, googleId, email);

        // 1. Invalid schema / non-existent segment order / wrong array element types -> 502 with ZERO writes
        string? replyJson = """{"entities":[42,{"sourceTerm":123,"translatedTerm":"x"}]}""";
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
            Task.FromResult(new AiResponse(replyJson!, 20, 10, 30, req.Model, FinishReason.Stop)));

        var badSchemaRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        });
        Assert.Equal(HttpStatusCode.BadGateway, badSchemaRes.StatusCode);

        // Fake non-existent order 999999 -> 502 with ZERO writes
        replyJson = """{"entities":[{"sourceTerm":"アインズ","translatedTerm":"Ainz","category":"Character","firstSeenOrder":999999}]}""";
        var badOrderRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        });
        Assert.Equal(HttpStatusCode.BadGateway, badOrderRes.StatusCode);

        var afterErrorsDoc = JsonDocument.Parse(await (await client.GetAsync($"/api/novels/{novelId}/story-memory")).Content.ReadAsStringAsync());
        Assert.Equal(0, afterErrorsDoc.RootElement.GetArrayLength());

        // 2. Valid empty extraction -> 200 OK with 0 mutations
        replyJson = """{"entities":[],"sceneFacts":[],"speechObservations":[]}""";
        var emptyRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        });
        Assert.Equal(HttpStatusCode.OK, emptyRes.StatusCode);
        using var emptyDoc = JsonDocument.Parse(await emptyRes.Content.ReadAsStringAsync());
        Assert.Equal(0, emptyDoc.RootElement.GetProperty("extractedCount").GetInt32());

        // 3. Valid extraction even claiming status=Confirmed MUST persist Status == Provisional with real SegmentId
        replyJson = $$"""
            {
              "entities":[
                {
                  "sourceTerm":"アインズ",
                  "translatedTerm":"Ainz-Provisional",
                  "category":"Character",
                  "status":"Confirmed",
                  "confidence":0.99,
                  "evidenceSegmentOrder":0,
                  "evidenceQuote":"「アインズ」はナザリックに立っていた"
                }
              ],
              "sceneFacts":[
                {
                  "factKey":"LocationNazarick",
                  "factType":"Setting",
                  "summary":"Standing inside Nazarick",
                  "evidenceSegmentOrder":0,
                  "evidenceQuote":"ナザリックに立っていた"
                }
              ],
              "speechObservations":[
                {
                  "speakerName":"アインズ",
                  "speechRegister":"Formal",
                  "observation":"Calm sovereign presence",
                  "evidenceSegmentOrder":0
                }
              ]
            }
            """;
        var validRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        });
        Assert.Equal(HttpStatusCode.OK, validRes.StatusCode);
        using var validDoc = JsonDocument.Parse(await validRes.Content.ReadAsStringAsync());
        var ainzItem = validDoc.RootElement.GetProperty("glossary")[0];
        Assert.Equal("Provisional", ainzItem.GetProperty("status").GetString());
        Assert.Equal(segments[0].Id, ainzItem.GetProperty("evidenceSegmentId").GetGuid());
        Assert.Equal(segments[0].StableId, ainzItem.GetProperty("evidenceSegmentStableId").GetString());
        Assert.Equal(FactStatus.Provisional.ToString(), validDoc.RootElement.GetProperty("sceneFacts")[0].GetProperty("status").GetString());
        Assert.Equal(FactStatus.Provisional.ToString(), validDoc.RootElement.GetProperty("speechObservations")[0].GetProperty("status").GetString());

        // 4. Conflicting unlocked extraction transitions entry to Conflicted instead of silently overwriting
        replyJson = """
            {
              "entities":[
                {
                  "sourceTerm":"アインズ",
                  "translatedTerm":"Chúa Quỷ Ainz",
                  "category":"Character",
                  "evidenceSegmentOrder":0,
                  "evidenceQuote":"アインズ"
                }
              ]
            }
            """;
        var conflictRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        });
        Assert.Equal(HttpStatusCode.OK, conflictRes.StatusCode);
        using var conflictDoc = JsonDocument.Parse(await conflictRes.Content.ReadAsStringAsync());
        Assert.Equal(1, conflictDoc.RootElement.GetProperty("conflicted").GetInt32());
        var conflictedAinz = conflictDoc.RootElement.GetProperty("glossary")[0];
        Assert.Equal("Conflicted", conflictedAinz.GetProperty("status").GetString());
        Assert.Equal("Ainz-Provisional", conflictedAinz.GetProperty("translatedTerm").GetString());
        Assert.Contains("Chúa Quỷ Ainz", conflictedAinz.GetProperty("conflictDetails").GetString());
    }

    [Fact]
    public async Task Extract_RaceBarrier_PreservesConcurrentPutLock_AndSerializesConcurrentExtracts()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, segments, ownerId, googleId, email) = await SeedNovelAsync(factory, new[]
        {
            "「アインズ」は立ち上がった。"
        });
        var client = CreateAuthedClient(factory, googleId, email);

        // Seed unlocked AiExtracted entry BEFORE starting extraction
        Guid entityId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var existing = new StoryEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "vi",
                SourceTerm = "アインズ",
                NormalizedSourceTerm = "アインズ",
                TranslatedTerm = "Ainz-InitialAi",
                Category = StoryEntityCategory.Character,
                Origin = StoryEntityOrigin.AiExtracted,
                Status = FactStatus.Provisional,
                IsLocked = false,
                EvidenceSegmentId = segments[0].Id,
                EvidenceSegmentStableId = segments[0].StableId,
                EvidenceSegmentOrder = 0,
                Version = 1
            };
            entityId = existing.Id;
            db.StoryEntities.Add(existing);
            await db.SaveChangesAsync();
        }

        var aiStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAi = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider(async (req, ct) =>
        {
            aiStarted.TrySetResult(true);
            await releaseAi.Task.WaitAsync(ct);
            const string json = """
                {
                  "entities":[
                    {
                      "sourceTerm":"アインズ",
                      "translatedTerm":"Ainz-StaleOverwriteAttempt",
                      "category":"Character",
                      "evidenceSegmentOrder":0,
                      "evidenceQuote":"アインズ"
                    }
                  ]
                }
                """;
            return new AiResponse(json, 30, 20, 50, req.Model, FinishReason.Stop);
        });

        // 1. Start extraction and pause inside provider barrier
        var extractTask = SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        });
        await aiStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 2. Concurrent PUT modifies TranslatedTerm, sets IsLocked=true & Status=Confirmed while extraction is in-flight
        var putRes = await SendWithCsrfAsync(factory, client, HttpMethod.Put, $"/api/novels/{novelId}/story-memory/{entityId}", new
        {
            targetLanguage = "vi",
            sourceTerm = "アインズ",
            translatedTerm = "Chúa Tể Ainz (PUT Locked)",
            category = "Character",
            isLocked = true,
            status = "Confirmed",
            expectedVersion = 1
        });
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        // Stale ExpectedVersion check returns 409 Conflict
        var stalePutRes = await SendWithCsrfAsync(factory, client, HttpMethod.Put, $"/api/novels/{novelId}/story-memory/{entityId}", new
        {
            targetLanguage = "vi",
            sourceTerm = "アインズ",
            translatedTerm = "Stale Client",
            category = "Character",
            expectedVersion = 1
        });
        Assert.Equal(HttpStatusCode.Conflict, stalePutRes.StatusCode);

        // 3. Release extraction barrier and assert PUT lock/translation survived intact
        releaseAi.TrySetResult(true);
        var extractRes = await extractTask;
        Assert.Equal(HttpStatusCode.OK, extractRes.StatusCode);
        using var extractDoc = JsonDocument.Parse(await extractRes.Content.ReadAsStringAsync());
        Assert.Equal(1, extractDoc.RootElement.GetProperty("skippedLocked").GetInt32());
        var persisted = extractDoc.RootElement.GetProperty("glossary")[0];
        Assert.Equal("Chúa Tể Ainz (PUT Locked)", persisted.GetProperty("translatedTerm").GetString());
        Assert.True(persisted.GetProperty("isLocked").GetBoolean());
        Assert.Equal("Confirmed", persisted.GetProperty("status").GetString());

        // 4. Race 2: two concurrent extractions inserting same brand-new term both succeed with 1 DB row
        // Use separate clients so concurrent CSRF cookie fetches do not overwrite one shared CookieContainer.
        var (novel2Id, _, _, g2, e2) = await SeedNovelAsync(factory, new[] { "Tanaka arrived at noon." });
        var client2A = CreateAuthedClient(factory, g2, e2);
        var client2B = CreateAuthedClient(factory, g2, e2);

        var bothEntered = 0;
        var bothReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBoth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider(async (req, ct) =>
        {
            if (Interlocked.Increment(ref bothEntered) == 2) bothReady.TrySetResult(true);
            await releaseBoth.Task.WaitAsync(ct);
            const string json = """
                {
                  "entities":[
                    {
                      "sourceTerm":"Tanaka",
                      "translatedTerm":"Tanaka",
                      "category":"Character",
                      "evidenceSegmentOrder":0,
                      "evidenceQuote":"Tanaka arrived"
                    }
                  ]
                }
                """;
            return new AiResponse(json, 20, 20, 40, req.Model, FinishReason.Stop);
        });

        var tA = SendWithCsrfAsync(factory, client2A, HttpMethod.Post, $"/api/novels/{novel2Id}/story-memory/extract", new { targetLanguage = "vi", provider = "mock" });
        var tB = SendWithCsrfAsync(factory, client2B, HttpMethod.Post, $"/api/novels/{novel2Id}/story-memory/extract", new { targetLanguage = "vi", provider = "mock" });
        await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseBoth.TrySetResult(true);

        var resA = await tA;
        var resB = await tB;
        Assert.Equal(HttpStatusCode.OK, resA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resB.StatusCode);

        var listRes = await client2A.GetAsync($"/api/novels/{novel2Id}/story-memory");
        using var listDoc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync());
        Assert.Equal(1, listDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task TranslationWorker_InjectsStoryMemoryLitePrompt_AndConsistencyCheckWorks()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, segments, _, googleId, email) = await SeedNovelAsync(factory, new[]
        {
            "Momonga walked across the hall.",
            "Momonga checked his magic scroll."
        });
        var client = CreateAuthedClient(factory, googleId, email);

        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "Ainz",
            translatedTerm = "Chúa Tể Ainz",
            category = "Character",
            aliases = new[] { "Momonga" },
            status = "Confirmed",
            evidenceSegmentId = segments[0].Id,
            evidenceQuote = "Momonga walked"
        })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/scene-facts", new
        {
            targetLanguage = "vi",
            factKey = "GreatHall",
            factType = "Setting",
            summary = "Grand marble hall of Nazarick",
            status = "Confirmed"
        })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/speech-observations", new
        {
            targetLanguage = "vi",
            speakerName = "Momonga",
            speechRegister = "Formal Sovereign",
            pronounStyle = "Ta / Ngươi",
            observation = "Dignified inner monologue",
            status = "Provisional"
        })).StatusCode);

        string? capturedPrompt = null;
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
        {
            capturedPrompt = req.UserPrompt;
            const string reply =
                "[SEG:0] Chúa Tể Ainz bước qua đại sảnh.\n" +
                "[SEG:1] Momonga kiểm tra cuộn ma thuật.";
            return Task.FromResult(new AiResponse(reply, 60, 40, 100, req.Model, FinishReason.Stop));
        });

        var jobRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, "/api/translations", new
        {
            novelId,
            targetLanguage = "vi",
            provider = "mock",
            model = "mock-v1"
        });
        Assert.Equal(HttpStatusCode.Created, jobRes.StatusCode);
        using var jobDoc = JsonDocument.Parse(await jobRes.Content.ReadAsStringAsync());
        var jobId = jobDoc.RootElement.GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var worker = new TranslationQueueWorker(
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TranslationQueueWorker>>(),
                scope.ServiceProvider.GetRequiredService<IJobCancellationRegistry>());
            Assert.True(await worker.ProcessNextJobAsync(CancellationToken.None));
        }

        Assert.NotNull(capturedPrompt);
        Assert.Contains("--- STORY MEMORY & GLOSSARY", capturedPrompt);
        Assert.Contains("--- SCENE FACTS", capturedPrompt);
        Assert.Contains("--- CHARACTER SPEECH", capturedPrompt);
        Assert.Contains("Ainz -> Chúa Tể Ainz", capturedPrompt);

        var consistencyRes = await client.GetAsync($"/api/translations/{jobId}/consistency");
        Assert.Equal(HttpStatusCode.OK, consistencyRes.StatusCode);
        using var cDoc = JsonDocument.Parse(await consistencyRes.Content.ReadAsStringAsync());
        Assert.Equal(1, cDoc.RootElement.GetProperty("issueCount").GetInt32());
        Assert.Equal(1, cDoc.RootElement.GetProperty("issues")[0].GetProperty("segmentOrder").GetInt32());
    }

    [Fact]
    public async Task ExtractStoryMemory_UpstreamTruncatedOrMalformed_Returns502SanitizedWithoutSecretLeak_AndDoesNotSaveEphemeralKey()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, _, _, googleId, email) = await SeedNovelAsync(factory, new[] { "「アインズ」は立ち上がった。" });
        var client = CreateAuthedClient(factory, googleId, email);

        // 1. Missing stored Gemini key and omitted request key -> 400 Bad Request
        var missingKeyRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "gemini"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingKeyRes.StatusCode);

        // 2. Valid extraction with ephemeral apiKey -> 200 OK without persisting to RAM vault
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
            Task.FromResult(new AiResponse("""{"entities":[]}""", 10, 5, 15, req.Model, FinishReason.Stop)));
        var ephemeralRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "gemini",
            apiKey = "ephemeral-secret-key-xyz"
        });
        Assert.Equal(HttpStatusCode.OK, ephemeralRes.StatusCode);

        var keyStatusRes = await client.GetAsync("/api/auth/api-keys");
        using var keyStatusDoc = JsonDocument.Parse(await keyStatusRes.Content.ReadAsStringAsync());
        Assert.False(keyStatusDoc.RootElement.GetProperty("gemini").GetBoolean());

        // 3. Upstream MaxTokens containing canary secret -> 502 BadGateway with sanitized message & zero secret leak
        const string secretCanary = "AIzaSyCanarySecret9999";
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
            Task.FromResult(new AiResponse($"invalid json raw body {secretCanary}", 50, 20, 70, req.Model, FinishReason.MaxTokens)));

        var truncatedRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "openai",
            apiKey = secretCanary
        });
        Assert.Equal(HttpStatusCode.BadGateway, truncatedRes.StatusCode);
        var truncatedBody = await truncatedRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretCanary, truncatedBody);
        Assert.Contains("truncated", truncatedBody, StringComparison.OrdinalIgnoreCase);

        // 4. Upstream provider error carrying canary secret in exception message -> 502 without leaking secret
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((_, _) =>
            throw new AiProviderError(AiErrorCode.NetworkError, $"Upstream failure using bearer {secretCanary}", promptTokens: 20, completionTokens: 0, totalTokens: 20, modelUsed: "gpt-4o-mini"));
        var errRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "openai",
            apiKey = secretCanary
        });
        Assert.Equal(HttpStatusCode.BadGateway, errRes.StatusCode);
        var errBody = await errRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretCanary, errBody);
    }

    [Fact]
    public async Task ConflictDetails_TruncatedSafelyWithin1000Chars_ForMaxLengthSceneFactAndSpeechObservation()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, segments, ownerId, googleId, email) = await SeedNovelAsync(factory, new[] { "Ainz stood in the great hall." });
        var client = CreateAuthedClient(factory, googleId, email);

        var maxExistingSummary = new string('E', 1000);
        var maxIncomingSummary = new string('I', 1000);
        var maxExistingObs = new string('O', 1000);
        var maxIncomingObs = new string('N', 1000);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            db.SceneFacts.Add(new SceneFact
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "vi",
                FactKey = "HallState",
                NormalizedFactKey = "hallstate",
                FactType = SceneFactType.Setting,
                Summary = maxExistingSummary,
                Origin = StoryEntityOrigin.AiExtracted,
                Status = FactStatus.Provisional,
                IsLocked = false,
                EvidenceSegmentId = segments[0].Id,
                EvidenceSegmentStableId = segments[0].StableId,
                EvidenceSegmentOrder = 0,
                Version = 1
            });
            db.SpeechObservations.Add(new SpeechObservation
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "vi",
                SpeakerName = "Ainz",
                NormalizedSpeakerName = "ainz",
                AddresseeName = null,
                NormalizedAddresseeName = string.Empty,
                SpeechRegister = "Sovereign",
                Observation = maxExistingObs,
                Origin = StoryEntityOrigin.AiExtracted,
                Status = FactStatus.Provisional,
                IsLocked = false,
                EvidenceSegmentId = segments[0].Id,
                EvidenceSegmentStableId = segments[0].StableId,
                EvidenceSegmentOrder = 0,
                Version = 1
            });
            await db.SaveChangesAsync();
        }

        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                entities = Array.Empty<object>(),
                sceneFacts = new[]
                {
                    new { factKey = "HallState", factType = "Setting", summary = maxIncomingSummary, evidenceSegmentOrder = 0 }
                },
                speechObservations = new[]
                {
                    new { speakerName = "Ainz", speechRegister = "Casual", observation = maxIncomingObs, evidenceSegmentOrder = 0 }
                }
            });
            return Task.FromResult(new AiResponse(payload, 30, 30, 60, req.Model, FinishReason.Stop));
        });

        var res = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("conflicted").GetInt32());

        var factDetails = doc.RootElement.GetProperty("sceneFacts")[0].GetProperty("conflictDetails").GetString()!;
        var speechDetails = doc.RootElement.GetProperty("speechObservations")[0].GetProperty("conflictDetails").GetString()!;
        Assert.Equal("Conflicted", doc.RootElement.GetProperty("sceneFacts")[0].GetProperty("status").GetString());
        Assert.Equal("Conflicted", doc.RootElement.GetProperty("speechObservations")[0].GetProperty("status").GetString());
        Assert.InRange(factDetails.Length, 1, 1000);
        Assert.InRange(speechDetails.Length, 1, 1000);
    }

    [Fact]
    public async Task Extraction_RejectsConflictingDuplicatesWithinSingleResponse_Returns502WithZeroWrites()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, _, _, googleId, email) = await SeedNovelAsync(factory, new[] { "Alice arrived cleanly at dawn." });
        var client = CreateAuthedClient(factory, googleId, email);

        string payload = """
            {
              "entities":[
                {"sourceTerm":"Alice","translatedTerm":"Alice","category":"Character","evidenceSegmentOrder":0},
                {"sourceTerm":"alice","translatedTerm":"Different","category":"Character","evidenceSegmentOrder":0}
              ]
            }
            """;
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
            Task.FromResult(new AiResponse(payload, 15, 15, 30, req.Model, FinishReason.Stop)));

        var entityConflictRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new { targetLanguage = "vi", provider = "mock" });
        Assert.Equal(HttpStatusCode.BadGateway, entityConflictRes.StatusCode);

        payload = """
            {
              "entities":[],
              "sceneFacts":[
                {"factKey":"Dawn","factType":"Time","summary":"Early dawn","evidenceSegmentOrder":0},
                {"factKey":"dawn","factType":"Time","summary":"Midnight","evidenceSegmentOrder":0}
              ]
            }
            """;
        var factConflictRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new { targetLanguage = "vi", provider = "mock" });
        Assert.Equal(HttpStatusCode.BadGateway, factConflictRes.StatusCode);

        payload = """
            {
              "entities":[],
              "speechObservations":[
                {"speakerName":"Alice","speechRegister":"Polite","observation":"Uses formal tone","evidenceSegmentOrder":0},
                {"speakerName":"alice","speechRegister":"Rude","observation":"Uses formal tone","evidenceSegmentOrder":0}
              ]
            }
            """;
        var speechConflictRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new { targetLanguage = "vi", provider = "mock" });
        Assert.Equal(HttpStatusCode.BadGateway, speechConflictRes.StatusCode);

        using var entitiesDoc = JsonDocument.Parse(await (await client.GetAsync($"/api/novels/{novelId}/story-memory")).Content.ReadAsStringAsync());
        using var factsDoc = JsonDocument.Parse(await (await client.GetAsync($"/api/novels/{novelId}/scene-facts")).Content.ReadAsStringAsync());
        using var speechDoc = JsonDocument.Parse(await (await client.GetAsync($"/api/novels/{novelId}/speech-observations")).Content.ReadAsStringAsync());
        Assert.Equal(0, entitiesDoc.RootElement.GetArrayLength());
        Assert.Equal(0, factsDoc.RootElement.GetArrayLength());
        Assert.Equal(0, speechDoc.RootElement.GetArrayLength());
    }
}
