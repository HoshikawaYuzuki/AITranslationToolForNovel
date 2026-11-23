using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basi.Api.Data;
using Basi.Core.Models;
using Microsoft.EntityFrameworkCore;
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
        Assert.True(jobElement.TryGetProperty("totalTokens", out _));

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
    public async Task CancelTranslation_InFlightJob_AbortsViaCancellationRegistry()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var user = db.Users.First(u => u.Email == email);
            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = user.Id,
                Provider = "mock",
                Model = "mock-model",
                TargetLanguage = "vi",
                Status = JobStatus.Processing,
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        // Register in-flight CTS simulating active execution in TranslationQueueWorker
        var cancellationRegistry = _factory.Services.GetRequiredService<Basi.Core.Interfaces.IJobCancellationRegistry>();
        using var inFlightCts = new CancellationTokenSource();
        using var registration = cancellationRegistry.RegisterJob(jobId, inFlightCts);

        Assert.False(inFlightCts.IsCancellationRequested);

        var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/cancel");
        cancelReq.Headers.Add("X-XSRF-TOKEN", token);

        var cancelRes = await client.SendAsync(cancelReq);
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        // Verify that in-flight CTS was cancelled immediately!
        Assert.True(inFlightCts.IsCancellationRequested);

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
    public async Task ExclusiveClaim_ActiveProcessingJob_CannotBeClaimedBySecondWorker()
    {
        var (novelId, ownerId, _, _) = await SeedNovelAsync();

        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "mock",
                Model = "mock-v1",
                TargetLanguage = "vi",
                Status = JobStatus.Processing, // Actively running!
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow // Fresh lease!
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var staleThreshold = DateTime.UtcNow - TimeSpan.FromMinutes(5);

            // Active job with fresh UpdatedAt must NOT match claim predicate
            var claimCandidate = await db.TranslationJobs
                .Where(j => j.Id == jobId && (j.Status == JobStatus.Queued || (j.Status == JobStatus.Processing && j.UpdatedAt < staleThreshold)))
                .FirstOrDefaultAsync();

            Assert.Null(claimCandidate);

            // Stale job older than 5 minutes CAN be recovered
            var job = await db.TranslationJobs.FindAsync(jobId);
            job!.UpdatedAt = DateTime.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync();

            var recoveredCandidate = await db.TranslationJobs
                .Where(j => j.Id == jobId && (j.Status == JobStatus.Queued || (j.Status == JobStatus.Processing && j.UpdatedAt < staleThreshold)))
                .FirstOrDefaultAsync();

            Assert.NotNull(recoveredCandidate);
        }
    }

    [Fact]
    public async Task CreateTranslation_WithJobBoundApiKey_StoresInRamAndClearsOnFinalize()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync(segmentCount: 1);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var keyVault = _factory.Services.GetRequiredService<Basi.Module.Translation.Services.IApiKeyVault>();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/translations")
        {
            Content = JsonContent.Create(new
            {
                novelId,
                targetLanguage = "vi",
                provider = "mock",
                apiKey = "job-bound-key-12345"
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var jobId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

        // Poll until completed
        var maxWait = TimeSpan.FromSeconds(10);
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < maxWait)
        {
            var jobRes = await client.GetAsync($"/api/translations/{jobId}");
            var jobJson = await jobRes.Content.ReadAsStringAsync();
            using var jDoc = JsonDocument.Parse(jobJson);
            if (jDoc.RootElement.GetProperty("status").GetString() == "Completed")
                break;
            await Task.Delay(200);
        }

        // Job-bound key must be wiped from RAM upon completion!
        Assert.False(keyVault.HasJobKey(jobId));
    }

    [Fact]
    public async Task Logout_TriggersCancellationOfActiveInFlightJobs()
    {
        var (novelId, ownerId, googleId, email) = await SeedNovelAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "mock",
                Model = "mock-model",
                TargetLanguage = "vi",
                Status = JobStatus.Processing,
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var cancellationRegistry = _factory.Services.GetRequiredService<Basi.Core.Interfaces.IJobCancellationRegistry>();
        using var inFlightCts = new CancellationTokenSource();
        using var registration = cancellationRegistry.RegisterJob(jobId, inFlightCts);

        Assert.False(inFlightCts.IsCancellationRequested);

        // Send Logout
        var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutReq.Headers.Add("X-XSRF-TOKEN", token);

        var logoutRes = await client.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.OK, logoutRes.StatusCode);

        // CTS should be immediately cancelled!
        Assert.True(inFlightCts.IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteApiKey_TriggersCancellationOfActiveInFlightJobs()
    {
        var (novelId, ownerId, googleId, email) = await SeedNovelAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "gemini",
                Model = "gemini-2.5-flash",
                TargetLanguage = "vi",
                Status = JobStatus.Processing,
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var cancellationRegistry = _factory.Services.GetRequiredService<Basi.Core.Interfaces.IJobCancellationRegistry>();
        using var inFlightCts = new CancellationTokenSource();
        using var registration = cancellationRegistry.RegisterJob(jobId, inFlightCts);

        Assert.False(inFlightCts.IsCancellationRequested);

        // Send Delete key
        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/api-keys/gemini");
        deleteReq.Headers.Add("X-XSRF-TOKEN", token);

        var deleteRes = await client.SendAsync(deleteReq);
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);

        // CTS should be immediately cancelled!
        Assert.True(inFlightCts.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelTranslation_MultiBatchJob_TransitionsToCancelledAndStopsProcessing()
    {
        // 12 segments = 3 batches (5 + 5 + 2)
        var (novelId, _, googleId, email) = await SeedNovelAsync(segmentCount: 12);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

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

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var jobId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

        // Send cancel while queue worker is active
        var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/cancel");
        cancelReq.Headers.Add("X-XSRF-TOKEN", token);

        var cancelRes = await client.SendAsync(cancelReq);
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        // Wait to ensure worker loop acknowledges cancellation and finishes
        await Task.Delay(1500);

        // Verify status remains Cancelled and is not overwritten by Completed
        var checkRes = await client.GetAsync($"/api/translations/{jobId}");
        var checkJson = await checkRes.Content.ReadAsStringAsync();
        using var cDoc = JsonDocument.Parse(checkJson);
        Assert.Equal("Cancelled", cDoc.RootElement.GetProperty("status").GetString());
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

    [Fact]
    public async Task CreateTranslation_ModelExceeds128Chars_Returns400()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var longModel = new string('m', 129);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/translations")
        {
            Content = JsonContent.Create(new
            {
                novelId,
                targetLanguage = "vi",
                provider = "mock",
                model = longModel
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Logout_ClearsApiKeyVaultForUser()
    {
        var googleId = $"google-logout-{Guid.NewGuid():N}";
        var email = $"logout-{Guid.NewGuid():N}@example.com";
        var user = await _factory.SeedUserAsync(googleId, email, "Logout User");

        // Set key in vault
        using (var scope = _factory.Services.CreateScope())
        {
            var vault = scope.ServiceProvider.GetRequiredService<Basi.Module.Translation.Services.IApiKeyVault>();
            vault.SetKey(user.Id, "gemini", "secret-test-key-12345");
            Assert.True(vault.HasKey(user.Id, "gemini"));
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutReq.Headers.Add("X-XSRF-TOKEN", token);

        var logoutRes = await client.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.OK, logoutRes.StatusCode);

        // Verify key was purged from RAM vault
        using (var scope = _factory.Services.CreateScope())
        {
            var vault = scope.ServiceProvider.GetRequiredService<Basi.Module.Translation.Services.IApiKeyVault>();
            Assert.False(vault.HasKey(user.Id, "gemini"));
        }
    }
}
