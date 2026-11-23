using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basi.Api.Data;
using Basi.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Basi.Tests.Integration;

public class TranslationEndpointIntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;

    public TranslationEndpointIntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
        _factory.EnableTestAuth = true;
    }

    private async Task<(Guid NovelId, Guid OwnerId, string GoogleId, string Email)> SeedNovelAsync(int segmentCount = 2)
    {
        var googleId = $"google-novel-{Guid.NewGuid():N}";
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var user = await _factory.SeedUserAsync(googleId, email, "Novel Owner");

        var novelId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();

        var novel = new Novel
        {
            Id = novelId,
            OwnerId = user.Id,
            Title = "Integration Test Novel",
            SourceLanguage = "ja",
            RawContent = "Raw novel content",
            UploadedAt = DateTime.UtcNow
        };

        var segments = new List<Segment>();
        for (var i = 0; i < segmentCount; i++)
        {
            var seg = new Segment
            {
                Id = Guid.NewGuid(),
                NovelId = novelId,
                StableId = $"{novelId:N}:{i:D6}",
                Order = i,
                Content = $"Segment {i} Japanese text"
            };
            segments.Add(seg);
            novel.Segments.Add(seg);
        }

        db.Novels.Add(novel);
        db.Segments.AddRange(segments);
        await db.SaveChangesAsync();

        return (novelId, user.Id, googleId, email);
    }

    [Fact]
    public async Task CreateTranslation_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/translations")
        {
            Content = JsonContent.Create(new
            {
                novelId = Guid.NewGuid(),
                targetLanguage = "vi",
                provider = "mock"
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task CreateTranslation_InvalidTargetLanguage_Returns400()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/translations")
        {
            Content = JsonContent.Create(new
            {
                novelId,
                targetLanguage = "invalid-lang-$$$",
                provider = "mock"
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task CreateTranslation_NovelNotOwned_Returns404()
    {
        var (novelId, _, _, _) = await SeedNovelAsync();

        // Other user
        var otherGoogleId = $"other-user-{Guid.NewGuid():N}";
        var otherEmail = $"other-{Guid.NewGuid():N}@example.com";
        await _factory.SeedUserAsync(otherGoogleId, otherEmail, "Other User");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", otherGoogleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", otherEmail);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/translations")
        {
            Content = JsonContent.Create(new
            {
                novelId,
                targetLanguage = "vi",
                provider = "mock"
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task CreateTranslation_EndToEnd_ExecutesThroughQueueWorkerToCompletion()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync(segmentCount: 3);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // 1. Submit translation job
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/translations")
        {
            Content = JsonContent.Create(new
            {
                novelId,
                targetLanguage = "vi",
                provider = "mock",
                model = "mock-v1"
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var createdJson = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(createdJson);
        var jobId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);
        Assert.Equal("Queued", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("totalSegments").GetInt32());

        // 2. Poll until Completed (worker runs in background)
        var maxWait = TimeSpan.FromSeconds(15);
        var start = DateTime.UtcNow;
        JsonElement jobElement = default;
        string? status = null;

        while (DateTime.UtcNow - start < maxWait)
        {
            var jobRes = await client.GetAsync($"/api/translations/{jobId}");
            Assert.Equal(HttpStatusCode.OK, jobRes.StatusCode);

            var jobJson = await jobRes.Content.ReadAsStringAsync();
            using var jDoc = JsonDocument.Parse(jobJson);
            jobElement = jDoc.RootElement.Clone();
            status = jobElement.GetProperty("status").GetString();

            if (status == "Completed" || status == "Failed")
            {
                break;
            }
            await Task.Delay(500);
        }

        Assert.Equal("Completed", status);
        Assert.Equal(3, jobElement.GetProperty("completedSegments").GetInt32());

        var segments = jobElement.GetProperty("segments");
        Assert.Equal(3, segments.GetArrayLength());

        foreach (var seg in segments.EnumerateArray())
        {
            Assert.Equal("Completed", seg.GetProperty("status").GetString());
            var translated = seg.GetProperty("translatedText").GetString();
            Assert.NotNull(translated);
            Assert.Contains("[Translated]", translated);
        }
    }

    [Fact]
    public async Task CancelTranslation_QueuedJob_TransitionsToCancelled()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // Directly seed a job in Queued without letting worker process it immediately
        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var user = db.Users.First(u => u.Email == email);
            var job = new TranslationJob
            {
                Id = Guid.NewGuid(),
                OwnerId = user.Id,
                NovelId = novelId,
                TargetLanguage = "vi",
                Provider = "gemini",
                Model = "gemini-2.5-flash",
                Status = JobStatus.WaitingForApiKey,
                ErrorMessage = "Waiting for RAM key",
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/cancel");
        cancelReq.Headers.Add("X-XSRF-TOKEN", token);

        var cancelRes = await client.SendAsync(cancelReq);
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        // Verify status in DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = db.TranslationJobs.Find(jobId);
            Assert.NotNull(job);
            Assert.Equal(JobStatus.Cancelled, job.Status);
        }
    }

    [Fact]
    public async Task UserIsolation_CannotAccessOtherUserJob()
    {
        var (novelId, ownerId, ownerGoogleId, ownerEmail) = await SeedNovelAsync();

        // User A creates job
        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = new TranslationJob
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "vi",
                Provider = "mock",
                Model = "mock-v1",
                Status = JobStatus.Queued,
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        // User B tries to view and cancel User A's job
        var otherGoogleId = $"other-isolation-{Guid.NewGuid():N}";
        var otherEmail = $"other-{Guid.NewGuid():N}@example.com";
        await _factory.SeedUserAsync(otherGoogleId, otherEmail, "Other User");

        var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-Test-UserId", otherGoogleId);
        clientB.DefaultRequestHeaders.Add("X-Test-Email", otherEmail);

        var tokenB = await _factory.FetchAntiforgeryTokenAsync(clientB);

        // GET other user's job -> 404
        var getRes = await clientB.GetAsync($"/api/translations/{jobId}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        // POST cancel other user's job -> 404
        var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/cancel");
        cancelReq.Headers.Add("X-XSRF-TOKEN", tokenB);
        var cancelRes = await clientB.SendAsync(cancelReq);
        Assert.Equal(HttpStatusCode.NotFound, cancelRes.StatusCode);
    }
}
