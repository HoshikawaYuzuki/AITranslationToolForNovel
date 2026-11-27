using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                if (hostedWorker != null)
                {
                    services.Remove(hostedWorker);
                }
            }

            var factoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAiProviderFactory));
            if (factoryDescriptor != null)
            {
                services.Remove(factoryDescriptor);
            }

            services.AddScoped<IAiProviderFactory>(sp =>
                new StoryMemoryTestProviderFactory(
                    sp.GetRequiredService<IApiKeyVault>(),
                    this));
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

        public IAiProvider CreateProvider(string providerName, string apiKey)
        {
            return _owner.ProviderSelector != null
                ? _owner.ProviderSelector(Guid.Empty, providerName)
                : _fallbackMock;
        }

        public IAiProvider GetProviderForUser(Guid userId, string providerName)
        {
            var norm = providerName.Trim().ToLowerInvariant();
            if (norm != "mock")
            {
                var key = _vault.GetKey(userId, norm);
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new AiProviderError(AiErrorCode.InvalidApiKey, $"Missing key for {providerName}");
                }
            }
            return _owner.ProviderSelector != null
                ? _owner.ProviderSelector(Guid.Empty, norm)
                : _fallbackMock;
        }

        public IAiProvider GetProviderForJob(Guid userId, string providerName, Guid jobId)
        {
            var norm = providerName.Trim().ToLowerInvariant();
            if (norm != "mock")
            {
                var key = _vault.GetJobKey(userId, jobId) ?? _vault.GetKey(userId, norm);
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new AiProviderError(AiErrorCode.InvalidApiKey, $"Missing key for {providerName}");
                }
            }
            return _owner.ProviderSelector != null
                ? _owner.ProviderSelector(jobId, norm)
                : _fallbackMock;
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
    private static async Task<(Guid NovelId, Guid OwnerId, string GoogleId, string Email)> SeedNovelAsync(
        StoryMemoryTestFactory factory,
        string[] segmentContents)
    {
        var googleId = $"google-sm-{Guid.NewGuid():N}";
        var email = $"sm-{Guid.NewGuid():N}@example.com";
        var user = await factory.SeedUserAsync(googleId, email, "Story Owner");

        var novelId = Guid.NewGuid();
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
        }

        db.Novels.Add(novel);
        await db.SaveChangesAsync();
        return (novelId, user.Id, googleId, email);
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
        if (body != null)
        {
            req.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task StoryMemoryEndpoints_RequireAuthAndCsrf_AndIsolateAcrossOwners()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, _, ownerGoogleId, ownerEmail) = await SeedNovelAsync(factory, new[] { "「アインズ」は立ち上がった。" });

        // 1. Unauthenticated requests return 401
        var anonClient = factory.CreateClient();
        anonClient.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync($"/api/novels/{novelId}/story-memory")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync($"/api/translations/{Guid.NewGuid()}/consistency")).StatusCode);

        // 2. Write without CSRF returns 400
        var ownerClient = CreateAuthedClient(factory, ownerGoogleId, ownerEmail);
        var noCsrfRes = await ownerClient.PostAsJsonAsync($"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "Ainz",
            translatedTerm = "Ainz"
        });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrfRes.StatusCode);

        // Create entry as Owner
        var createRes = await SendWithCsrfAsync(factory, ownerClient, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "アインズ",
            translatedTerm = "Ainz",
            category = "Character",
            aliases = new[] { "モモンガ" },
            description = "Guild leader"
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        using var createdDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var entryId = createdDoc.RootElement.GetProperty("id").GetGuid();

        // 3. Another user cannot read, add, update, extract, or delete User A's story memory
        var otherGoogleId = $"google-other-{Guid.NewGuid():N}";
        var otherEmail = $"other-{Guid.NewGuid():N}@example.com";
        await factory.SeedUserAsync(otherGoogleId, otherEmail, "Other User");
        var otherClient = CreateAuthedClient(factory, otherGoogleId, otherEmail);

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/novels/{novelId}/story-memory")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(factory, otherClient, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "Other",
            translatedTerm = "Khác"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(factory, otherClient, HttpMethod.Put, $"/api/novels/{novelId}/story-memory/{entryId}", new
        {
            targetLanguage = "vi",
            sourceTerm = "Hijack",
            translatedTerm = "Hijack"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(factory, otherClient, HttpMethod.Delete, $"/api/novels/{novelId}/story-memory/{entryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(factory, otherClient, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "mock"
        })).StatusCode);
    }

    [Fact]
    public async Task StoryMemoryCrud_SupportsCreateFilterUpdateDelete_AndRejectsNfkcDuplicates()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, _, googleId, email) = await SeedNovelAsync(factory, new[] { "Sample text" });
        var client = CreateAuthedClient(factory, googleId, email);

        // Create entry
        var createRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "Ainz Ooal Gown",
            translatedTerm = "Ainz Ooal Gown",
            category = "Character",
            aliases = new[] { "Momonga", "momonga", "Suzuki Satoru" },
            description = "Guild Master of Nazarick"
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        using var createdJson = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var entryId = createdJson.RootElement.GetProperty("id").GetGuid();

        // Duplicate normalized key (full-width / whitespace variant) -> 409 Conflict
        var dupRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "  Ａｉｎｚ   Ooal Gown ",
            translatedTerm = "Duplicate"
        });
        Assert.Equal(HttpStatusCode.Conflict, dupRes.StatusCode);

        // Invalid category -> 400 BadRequest
        var badCatRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "Nazarick",
            translatedTerm = "Nazarick",
            category = "InvalidCategoryXYZ"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badCatRes.StatusCode);

        // Same term in different targetLanguage ("en") succeeds cleanly
        var enRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "en",
            sourceTerm = "Ainz Ooal Gown",
            translatedTerm = "Ainz Ooal Gown",
            category = "Character"
        });
        Assert.Equal(HttpStatusCode.Created, enRes.StatusCode);

        // Filter by targetLanguage and search alias ("Suzuki")
        var listRes = await client.GetAsync($"/api/novels/{novelId}/story-memory?targetLanguage=vi&search=Suzuki");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        using var listDoc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync());
        var arr = listDoc.RootElement;
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("Ainz Ooal Gown", arr[0].GetProperty("sourceTerm").GetString());
        Assert.Equal(2, arr[0].GetProperty("aliases").GetArrayLength());

        // Update entry
        var putRes = await SendWithCsrfAsync(factory, client, HttpMethod.Put, $"/api/novels/{novelId}/story-memory/{entryId}", new
        {
            targetLanguage = "vi",
            sourceTerm = "Ainz Ooal Gown",
            translatedTerm = "Chúa Tể Ainz Ooal Gown",
            category = "Organization",
            aliases = new[] { "Momonga" },
            description = "Updated guild name",
            isLocked = false
        });
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);
        using var putDoc = JsonDocument.Parse(await putRes.Content.ReadAsStringAsync());
        Assert.Equal("Chúa Tể Ainz Ooal Gown", putDoc.RootElement.GetProperty("translatedTerm").GetString());
        Assert.Equal("Organization", putDoc.RootElement.GetProperty("category").GetString());
        Assert.False(putDoc.RootElement.GetProperty("isLocked").GetBoolean());

        // Delete entry
        var delRes = await SendWithCsrfAsync(factory, client, HttpMethod.Delete, $"/api/novels/{novelId}/story-memory/{entryId}");
        Assert.Equal(HttpStatusCode.OK, delRes.StatusCode);

        var afterDelRes = await client.GetAsync($"/api/novels/{novelId}/story-memory?targetLanguage=vi");
        using var afterDelDoc = JsonDocument.Parse(await afterDelRes.Content.ReadAsStringAsync());
        Assert.Equal(0, afterDelDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ExtractStoryMemory_PreservesLockedManualEdits_AndUsesEphemeralKeyWithoutPersisting()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, _, googleId, email) = await SeedNovelAsync(factory, new[]
        {
            "「アインズ」はナザリックに立っていた。"
        });
        var client = CreateAuthedClient(factory, googleId, email);

        var aiExtractRound = 0;
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
        {
            var round = Interlocked.Increment(ref aiExtractRound);
            var payload = round == 1
                ? """
                  {"entities":[
                    {"sourceTerm":"アインズ","translatedTerm":"Ainz-Draft","category":"Character","aliases":["モモンガ"],"description":"Hero"},
                    {"sourceTerm":"ナザリック","translatedTerm":"Nazarick-Draft","category":"Location","aliases":[]}
                  ]}
                  """
                : """
                  {"entities":[
                    {"sourceTerm":"アインズ","translatedTerm":"Ainz-NewAiAttempt","category":"Character","aliases":["鈴木悟"],"description":"New"},
                    {"sourceTerm":"ナザリック","translatedTerm":"Đại Lăng Mộ Nazarick","category":"Location","aliases":["Tomb"]}
                  ]}
                  """;

            return Task.FromResult(new AiResponse(payload, 40, 30, 70, req.Model, FinishReason.Stop));
        });

        // Missing Gemini key in RAM vault and omitted in request -> 400 Bad Request
        var missingKeyRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "gemini"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingKeyRes.StatusCode);

        // Round 1 with ephemeral request API key -> succeeds and creates 2 unlocked AiExtracted entries
        var r1Res = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "gemini",
            model = "gemini-2.5-flash",
            apiKey = "ephemeral-secret-key-xyz"
        });
        Assert.Equal(HttpStatusCode.OK, r1Res.StatusCode);
        using var r1Doc = JsonDocument.Parse(await r1Res.Content.ReadAsStringAsync());
        Assert.Equal(2, r1Doc.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(70, r1Doc.RootElement.GetProperty("totalTokens").GetInt32());

        // Ephemeral key must NOT be saved into account RAM vault
        var keyStatusRes = await client.GetAsync("/api/auth/api-keys");
        using var keyStatusDoc = JsonDocument.Parse(await keyStatusRes.Content.ReadAsStringAsync());
        Assert.False(keyStatusDoc.RootElement.GetProperty("gemini").GetBoolean());

        // User edits & locks "アインズ" manually; leaves "ナザリック" unlocked
        var ainzEntry = r1Doc.RootElement.GetProperty("glossary").EnumerateArray()
            .First(x => x.GetProperty("sourceTerm").GetString() == "アインズ");
        var ainzId = ainzEntry.GetProperty("id").GetGuid();

        var lockRes = await SendWithCsrfAsync(factory, client, HttpMethod.Put, $"/api/novels/{novelId}/story-memory/{ainzId}", new
        {
            targetLanguage = "vi",
            sourceTerm = "アインズ",
            translatedTerm = "Chúa Tể Ainz (Canonical)",
            category = "Character",
            aliases = new[] { "モモンガ" },
            description = "Locked canonical name",
            isLocked = true
        });
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);

        // Round 2 extraction with another ephemeral key: skips locked "アインズ", updates unlocked "ナザリック"
        var r2Res = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "gemini",
            apiKey = "ephemeral-secret-key-xyz"
        });
        Assert.Equal(HttpStatusCode.OK, r2Res.StatusCode);
        using var r2Doc = JsonDocument.Parse(await r2Res.Content.ReadAsStringAsync());
        Assert.Equal(0, r2Doc.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(1, r2Doc.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal(1, r2Doc.RootElement.GetProperty("skippedLocked").GetInt32());

        var finalGlossary = r2Doc.RootElement.GetProperty("glossary").EnumerateArray().ToList();
        var preservedAinz = finalGlossary.First(x => x.GetProperty("sourceTerm").GetString() == "アインズ");
        var updatedNazarick = finalGlossary.First(x => x.GetProperty("sourceTerm").GetString() == "ナザリック");
        Assert.Equal("Chúa Tể Ainz (Canonical)", preservedAinz.GetProperty("translatedTerm").GetString());
        Assert.Equal("Đại Lăng Mộ Nazarick", updatedNazarick.GetProperty("translatedTerm").GetString());
        Assert.Contains("Tomb", updatedNazarick.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task ExtractStoryMemory_UpstreamTruncatedOrMalformed_Returns502SanitizedWithoutSecretLeak()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, _, googleId, email) = await SeedNovelAsync(factory, new[] { "Short novel text" });
        var client = CreateAuthedClient(factory, googleId, email);

        const string secretCanary = "AIzaSyCanarySecret9999";
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
            Task.FromResult(new AiResponse($"invalid json raw body {secretCanary}", 50, 20, 70, req.Model, FinishReason.MaxTokens)));

        var res = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory/extract", new
        {
            targetLanguage = "vi",
            provider = "openai",
            apiKey = secretCanary
        });

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretCanary, body);
        Assert.Contains("truncated", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslationWorker_InjectsGlossaryIntoPrompt_AndConsistencyEndpointDetectsMismatches()
    {
        using var factory = new StoryMemoryTestFactory { EnableTestAuth = true };
        var (novelId, _, googleId, email) = await SeedNovelAsync(factory, new[]
        {
            "Momonga walked across the hall.",
            "Momonga checked his magic scroll."
        });
        var client = CreateAuthedClient(factory, googleId, email);

        // Seed canonical glossary term with alias "Momonga"
        var addRes = await SendWithCsrfAsync(factory, client, HttpMethod.Post, $"/api/novels/{novelId}/story-memory", new
        {
            targetLanguage = "vi",
            sourceTerm = "Ainz",
            translatedTerm = "Chúa Tể Ainz",
            category = "Character",
            aliases = new[] { "Momonga" },
            description = "Protagonist"
        });
        Assert.Equal(HttpStatusCode.Created, addRes.StatusCode);

        string? capturedPrompt = null;
        factory.ProviderSelector = (_, _) => new StoryMemoryDelegateProvider((req, _) =>
        {
            capturedPrompt = req.UserPrompt;
            // Segment 0 includes canonical translation; Segment 1 uses inconsistent alias "Momonga" instead!
            const string reply =
                "[SEG:0] Chúa Tể Ainz bước qua đại sảnh.\n" +
                "[SEG:1] Momonga kiểm tra cuộn ma thuật của mình.";
            return Task.FromResult(new AiResponse(reply, 60, 40, 100, req.Model, FinishReason.Stop));
        });

        // Submit translation job
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

        // Execute worker once
        using (var scope = factory.Services.CreateScope())
        {
            var worker = new TranslationQueueWorker(
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TranslationQueueWorker>>(),
                scope.ServiceProvider.GetRequiredService<IJobCancellationRegistry>());
            var processed = await worker.ProcessNextJobAsync(CancellationToken.None);
            Assert.True(processed);
        }

        // Verify worker injected Story Memory glossary BEFORE target segments
        Assert.NotNull(capturedPrompt);
        var glossaryIndex = capturedPrompt!.IndexOf("--- STORY MEMORY & GLOSSARY", StringComparison.Ordinal);
        var targetIndex = capturedPrompt.IndexOf("--- SEGMENTS TO TRANSLATE ---", StringComparison.Ordinal);
        Assert.True(glossaryIndex >= 0);
        Assert.True(targetIndex > glossaryIndex);
        Assert.Contains("Ainz -> Chúa Tể Ainz [Character] (Aliases: Momonga)", capturedPrompt);

        // Verify consistency endpoint identifies Segment 1 mismatch and spares Segment 0
        var consistencyRes = await client.GetAsync($"/api/translations/{jobId}/consistency");
        Assert.Equal(HttpStatusCode.OK, consistencyRes.StatusCode);
        using var cDoc = JsonDocument.Parse(await consistencyRes.Content.ReadAsStringAsync());
        Assert.Equal(2, cDoc.RootElement.GetProperty("checkedSegments").GetInt32());
        Assert.Equal(1, cDoc.RootElement.GetProperty("glossaryCount").GetInt32());
        Assert.Equal(1, cDoc.RootElement.GetProperty("issueCount").GetInt32());
        var issue = cDoc.RootElement.GetProperty("issues")[0];
        Assert.Equal(1, issue.GetProperty("segmentOrder").GetInt32());
        Assert.Equal("Chúa Tể Ainz", issue.GetProperty("expectedTranslation").GetString());
    }
}
