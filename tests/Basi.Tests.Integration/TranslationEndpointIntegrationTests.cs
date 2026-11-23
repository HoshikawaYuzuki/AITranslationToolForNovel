using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basi.Api.Data;
using Basi.Api.Workers;
using Basi.Core.Interfaces;
using Basi.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task CancelJob_QueuedStatus_RemovesJobKeyImmediatelyFromRam()
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
                targetLanguage = "vi",
                provider = "mock",
                model = "mock-v1",
                apiKey = "queued-key-12345"
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var jobId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

        // Cancel job
        var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/cancel");
        cancelReq.Headers.Add("X-XSRF-TOKEN", token);

        var cancelRes = await client.SendAsync(cancelReq);
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        // Verify key is immediately purged from RAM
        using (var scope = _factory.Services.CreateScope())
        {
            var vault = scope.ServiceProvider.GetRequiredService<Basi.Module.Translation.Services.IApiKeyVault>();
            Assert.False(vault.HasJobKey(jobId));
        }
    }

    [Fact]
    public async Task SetJobApiKey_WhenWaitingForApiKey_WakesJobAndTransitionsToQueued()
    {
        var (novelId, ownerId, googleId, email) = await SeedNovelAsync();

        // Seed a job in WaitingForApiKey status
        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = new TranslationJob
            {
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "vi",
                Provider = "gemini",
                Model = "gemini-2.5-flash",
                Status = JobStatus.WaitingForApiKey,
                TotalSegments = 1,
                CompletedSegments = 0
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // Call POST /api/translations/{id}/api-key to supply key and wake the job
        var setKeyReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/api-key")
        {
            Content = JsonContent.Create(new { apiKey = "AIzaSyResumedKey123" })
        };
        setKeyReq.Headers.Add("X-XSRF-TOKEN", token);

        var setKeyRes = await client.SendAsync(setKeyReq);
        Assert.Equal(HttpStatusCode.OK, setKeyRes.StatusCode);

        // Verify DB job transitioned to Queued
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var updatedJob = await db.TranslationJobs.FindAsync(jobId);
            Assert.NotNull(updatedJob);
            Assert.Equal(JobStatus.Queued, updatedJob.Status);
            Assert.Null(updatedJob.ErrorMessage);

            var vault = scope.ServiceProvider.GetRequiredService<Basi.Module.Translation.Services.IApiKeyVault>();
            Assert.True(vault.HasJobKey(jobId));
            Assert.Equal("AIzaSyResumedKey123", vault.GetJobKey(ownerId, jobId));
        }
    }

    [Fact]
    public async Task TwoJobs_SameUser_DifferentKeys_ReEntryOnlyWakesTargetJob()
    {
        var (novelId, ownerId, googleId, email) = await SeedNovelAsync();

        Guid job1Id;
        Guid job2Id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job1 = new TranslationJob
            {
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "vi",
                Provider = "gemini",
                Model = "gemini-2.5-flash",
                Status = JobStatus.WaitingForApiKey,
                TotalSegments = 1
            };
            var job2 = new TranslationJob
            {
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "en",
                Provider = "openai",
                Model = "gpt-4o-mini",
                Status = JobStatus.WaitingForApiKey,
                TotalSegments = 1
            };
            db.TranslationJobs.AddRange(job1, job2);
            await db.SaveChangesAsync();
            job1Id = job1.Id;
            job2Id = job2.Id;
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // Re-enter key ONLY for job1
        var setKeyReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{job1Id}/api-key")
        {
            Content = JsonContent.Create(new { apiKey = "key-for-job-1" })
        };
        setKeyReq.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(setKeyReq);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Verify job1 is Queued with key, while job2 remains WaitingForApiKey without key!
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var dbJob1 = await db.TranslationJobs.FindAsync(job1Id);
            var dbJob2 = await db.TranslationJobs.FindAsync(job2Id);

            Assert.Equal(JobStatus.Queued, dbJob1!.Status);
            Assert.Equal(JobStatus.WaitingForApiKey, dbJob2!.Status);

            var vault = scope.ServiceProvider.GetRequiredService<Basi.Module.Translation.Services.IApiKeyVault>();
            Assert.True(vault.HasJobKey(job1Id));
            Assert.Equal("key-for-job-1", vault.GetJobKey(ownerId, job1Id));

            Assert.False(vault.HasJobKey(job2Id));
        }
    }

    [Fact]
    public async Task SetJobApiKey_DifferentUser_ReturnsNotFound()
    {
        var (novelId, ownerId, _, _) = await SeedNovelAsync();

        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = new TranslationJob
            {
                OwnerId = ownerId,
                NovelId = novelId,
                TargetLanguage = "vi",
                Provider = "gemini",
                Model = "gemini-2.5-flash",
                Status = JobStatus.WaitingForApiKey,
                TotalSegments = 1
            };
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        // Another user attempts to set key
        var attackerGoogleId = $"google-attacker-{Guid.NewGuid():N}";
        var attackerEmail = $"attacker-{Guid.NewGuid():N}@example.com";
        await _factory.SeedUserAsync(attackerGoogleId, attackerEmail, "Attacker User");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", attackerGoogleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", attackerEmail);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/api-key")
        {
            Content = JsonContent.Create(new { apiKey = "attacker-key" })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Translation_RecordsRequestCountAndActualModel_WithoutOverwritingModel()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync(segmentCount: 3);
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
                model = "requested-model-v1"
            })
        };
        req.Headers.Add("X-XSRF-TOKEN", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var jobId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

        // Poll until Completed
        var maxAttempts = 30;
        var status = "Queued";
        JsonDocument? finalDoc = null;
        while (maxAttempts-- > 0 && status != "Completed" && status != "Failed")
        {
            await Task.Delay(200);
            var pollRes = await client.GetAsync($"/api/translations/{jobId}");
            var pollJson = await pollRes.Content.ReadAsStringAsync();
            finalDoc?.Dispose();
            finalDoc = JsonDocument.Parse(pollJson);
            status = finalDoc.RootElement.GetProperty("status").GetString()!;
        }

        Assert.NotNull(finalDoc);
        using (finalDoc)
        {
            var root = finalDoc.RootElement;
            Assert.Equal("Completed", root.GetProperty("status").GetString());
            // Requested model is preserved
            Assert.Equal("requested-model-v1", root.GetProperty("model").GetString());
            // Request count is tracked
            Assert.True(root.GetProperty("requestCount").GetInt32() >= 1);
        }
    }

    [Fact]
    public async Task ClaimCleanup_WhenDatabaseThrowsInFinally_ReleasesClaimAndAllowsSubsequentProcessing()
    {
        var (novelId, _, googleId, email) = await SeedNovelAsync(segmentCount: 1);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var hookTriggered = false;
        Guid targetJobId = Guid.Empty;

        TranslationQueueWorker.OnBeforeFinallyStatusQueryForTesting = (id) =>
        {
            if (id == targetJobId)
            {
                hookTriggered = true;
                throw new InvalidOperationException("Simulated transient DB failure during finally cleanup");
            }
            return Task.CompletedTask;
        };

        try
        {
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
            targetJobId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

            // Poll until worker executes the job and hits the hook in finally
            var maxAttempts = 30;
            while (maxAttempts-- > 0 && !hookTriggered)
            {
                await Task.Delay(200);
            }

            Assert.True(hookTriggered, "Hook in finally block should have executed and thrown");

            // F08-R: The in-process claim MUST be released even though finally DB query threw
            Assert.False(TranslationQueueWorker.HasActiveClaim(targetJobId), "Claim must be removed from RAM even when finally DB query throws");
        }
        finally
        {
            TranslationQueueWorker.OnBeforeFinallyStatusQueryForTesting = null;
            TranslationQueueWorker.ClearActiveClaims();
        }
    }

    [Fact]
    public async Task TokenBudget_SoftStop_HaltsFurtherBatchesAndPreservesCompletedSegments()
    {
        var (novelId, ownerId, _, _) = await SeedNovelAsync(segmentCount: 5);

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
                Status = JobStatus.Queued,
                TotalSegments = 5,
                CompletedSegments = 2,
                TotalTokens = 100_000, // Reached MaxJobTotalTokens
                PromptTokens = 50_000,
                CompletionTokens = 50_000,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);

            var segments = await db.Segments.Where(s => s.NovelId == novelId).OrderBy(s => s.Order).ToListAsync();
            // 2 completed segments
            db.TranslatedSegments.Add(new TranslatedSegment
            {
                JobId = job.Id,
                SegmentId = segments[0].Id,
                Order = 0,
                SourceText = segments[0].Content,
                TranslatedText = "Bản dịch 0",
                Status = SegmentStatus.Completed
            });
            db.TranslatedSegments.Add(new TranslatedSegment
            {
                JobId = job.Id,
                SegmentId = segments[1].Id,
                Order = 1,
                SourceText = segments[1].Content,
                TranslatedText = "Bản dịch 1",
                Status = SegmentStatus.Completed
            });
            // 3 pending segments
            for (var i = 2; i < 5; i++)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = segments[i].Id,
                    Order = i,
                    SourceText = segments[i].Content,
                    Status = SegmentStatus.Pending
                });
            }

            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        // Poll until background worker picks up and processes the job
        var maxAttempts = 30;
        JobStatus finalStatus = JobStatus.Queued;
        string? finalError = null;

        while (maxAttempts-- > 0 && finalStatus != JobStatus.Failed && finalStatus != JobStatus.Completed)
        {
            await Task.Delay(200);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var j = await db.TranslationJobs.FindAsync(jobId);
            if (j != null)
            {
                finalStatus = j.Status;
                finalError = j.ErrorMessage;
            }
        }

        Assert.Equal(JobStatus.Failed, finalStatus);
        Assert.NotNull(finalError);
        Assert.Contains("budget", finalError, StringComparison.OrdinalIgnoreCase);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            // Completed segments from prior checkpoint must be preserved
            var completedCount = await db.TranslatedSegments.CountAsync(s => s.JobId == jobId && s.Status == SegmentStatus.Completed);
            Assert.Equal(2, completedCount);
        }
    }
}
