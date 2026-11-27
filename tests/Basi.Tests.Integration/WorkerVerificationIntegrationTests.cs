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
using Microsoft.Extensions.Logging;
using Xunit;

namespace Basi.Tests.Integration;

public sealed class ControlledWorkerTestFactory : AuthTestFactory
{
    public Func<Guid, string, IAiProvider>? ProviderSelector { get; set; }
    public bool DisableBackgroundWorker { get; set; }

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
                new ConfigurableTestProviderFactory(
                    sp.GetRequiredService<IApiKeyVault>(),
                    this));
        });
    }

    private sealed class ConfigurableTestProviderFactory : IAiProviderFactory
    {
        private readonly IApiKeyVault _vault;
        private readonly ControlledWorkerTestFactory _owner;
        private readonly MockAiProvider _fallbackMock = new();

        public ConfigurableTestProviderFactory(IApiKeyVault vault, ControlledWorkerTestFactory owner)
        {
            _vault = vault;
            _owner = owner;
        }

        public IAiProvider CreateProvider(string providerName, string apiKey)
        {
            if (_owner.ProviderSelector != null)
            {
                return _owner.ProviderSelector(Guid.Empty, providerName);
            }
            return _fallbackMock;
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
            return _owner.ProviderSelector != null ? _owner.ProviderSelector(Guid.Empty, norm) : _fallbackMock;
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
            return _owner.ProviderSelector != null ? _owner.ProviderSelector(jobId, norm) : _fallbackMock;
        }
    }
}

internal sealed class DelegateAiProvider : IAiProvider
{
    private readonly Func<AiRequest, CancellationToken, Task<AiResponse>> _handler;

    public DelegateAiProvider(Func<AiRequest, CancellationToken, Task<AiResponse>> handler)
    {
        _handler = handler;
    }

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
        => _handler(request, ct);
}

public class WorkerVerificationIntegrationTests
{
    private static async Task<(Guid NovelId, Guid OwnerId, string GoogleId, string Email)> SeedNovelAsync(
        AuthTestFactory factory,
        int segmentCount,
        Func<int, string>? contentBuilder = null)
    {
        var googleId = $"google-wvt-{Guid.NewGuid():N}";
        var email = $"wvt-{Guid.NewGuid():N}@example.com";
        var user = await factory.SeedUserAsync(googleId, email, "Test Owner");

        var novelId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();

        var novel = new Novel
        {
            Id = novelId,
            OwnerId = user.Id,
            Title = "Worker Verification Novel",
            SourceLanguage = "ja",
            RawContent = "Raw novel text",
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
                Content = contentBuilder != null ? contentBuilder(i) : $"Source paragraph {i}"
            };
            segments.Add(seg);
            novel.Segments.Add(seg);
        }

        db.Novels.Add(novel);
        db.Segments.AddRange(segments);
        await db.SaveChangesAsync();

        return (novelId, user.Id, googleId, email);
    }

    private static string BuildMockSegmentedResponse(string userPrompt, string tag = "Translated")
    {
        const string marker = "--- SEGMENTS TO TRANSLATE ---";
        var idx = userPrompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var slice = idx >= 0 ? userPrompt[(idx + marker.Length)..] : userPrompt;

        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(slice, @"\[SEG:(\d+)\]\s*([^\r\n]+)"))
        {
            sb.AppendLine($"[SEG:{m.Groups[1].Value}] [{tag}] {m.Groups[2].Value.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public async Task InFlightCancellation_RealApiToWorkerToProvider_AbortsProviderAndPreservesCheckpoints()
    {
        // V02: True E2E API -> real TranslationQueueWorker -> TranslationEngine -> blocking fake IAiProvider
        using var factory = new ControlledWorkerTestFactory { EnableTestAuth = true };

        var batch2Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerObservedCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        factory.ProviderSelector = (_, _) => new DelegateAiProvider(async (req, ct) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                // Batch 1 (segments 0..4) succeeds and checkpoints to DB
                return new AiResponse(
                    BuildMockSegmentedResponse(req.UserPrompt, "Batch1"),
                    PromptTokens: 100,
                    CompletionTokens: 100,
                    TotalTokens: 200,
                    ModelUsed: "fake-v1",
                    FinishReason: FinishReason.Stop);
            }

            // Batch 2 (segments 5..9) signals Started and blocks until cancelled
            batch2Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("Should not reach after Delay(Infinite)");
            }
            catch (OperationCanceledException)
            {
                providerObservedCancel.TrySetResult(true);
                throw;
            }
        });

        var (novelId, _, googleId, email) = await SeedNovelAsync(factory, segmentCount: 10);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        var token = await factory.FetchAntiforgeryTokenAsync(client);

        // 1. Create translation job via API
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/translations")
        {
            Content = JsonContent.Create(new
            {
                novelId,
                targetLanguage = "vi",
                provider = "mock",
                model = "fake-v1"
            })
        };
        createReq.Headers.Add("X-XSRF-TOKEN", token);

        var createRes = await client.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var jobId = Guid.Parse(createDoc.RootElement.GetProperty("id").GetString()!);

        // 2. Wait until Batch 1 has completed/checkpointed AND Batch 2 is actively in-flight in the provider
        await batch2Started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 3. Call POST /api/translations/{id}/cancel while provider request is blocked
        var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/cancel");
        cancelReq.Headers.Add("X-XSRF-TOKEN", token);

        var cancelRes = await client.SendAsync(cancelReq);
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        // 4. Verify real IAiProvider received CancellationToken abort
        await providerObservedCancel.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 5. Wait for worker cleanup to finish releasing active claim
        var waitAttempts = 40;
        while (waitAttempts-- > 0 && TranslationQueueWorker.HasActiveClaim(jobId))
        {
            await Task.Delay(100);
        }
        Assert.False(TranslationQueueWorker.HasActiveClaim(jobId));

        // 6. Verify DB state & API response: Cancelled preserved, Batch 1 checkpoints preserved, Batch 2 reverted cleanly
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = await db.TranslationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert.NotNull(job);
            Assert.Equal(JobStatus.Cancelled, job.Status);
            Assert.Equal(5, job.CompletedSegments);

            var segments = await db.TranslatedSegments
                .AsNoTracking()
                .Where(s => s.JobId == jobId)
                .OrderBy(s => s.Order)
                .ToListAsync();
            Assert.Equal(10, segments.Count);

            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(SegmentStatus.Completed, segments[i].Status);
                Assert.Contains("[Batch1]", segments[i].TranslatedText);
            }
            for (var i = 5; i < 10; i++)
            {
                Assert.Equal(SegmentStatus.Pending, segments[i].Status);
                Assert.Null(segments[i].TranslatedText);
            }
        }

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task TokenBudget_PreflightStopNearThreshold_HaltsBeforeCallingProviderAndPreservesCheckpoints()
    {
        // F07: When remaining tokens cannot accommodate next batch estimate, preflight halts BEFORE calling provider
        using var factory = new ControlledWorkerTestFactory { EnableTestAuth = true, DisableBackgroundWorker = true };

        var providerCalls = 0;
        factory.ProviderSelector = (_, _) => new DelegateAiProvider((req, _) =>
        {
            Interlocked.Increment(ref providerCalls);
            return Task.FromResult(new AiResponse(BuildMockSegmentedResponse(req.UserPrompt), 100, 150, 250, req.Model, FinishReason.Stop));
        });

        var (novelId, ownerId, _, _) = await SeedNovelAsync(factory, segmentCount: 5);

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var segs = await db.Segments.Where(s => s.NovelId == novelId).OrderBy(s => s.Order).ToListAsync();

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
                RequestCount = 4,
                PromptTokens = 50_000,
                CompletionTokens = 49_800,
                TotalTokens = 99_800, // 200 remaining (< min batch estimate)
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);

            for (var i = 0; i < 2; i++)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = segs[i].Id,
                    Order = i,
                    SourceText = segs[i].Content,
                    TranslatedText = $"Checkpoint {i}",
                    Status = SegmentStatus.Completed
                });
            }
            for (var i = 2; i < 5; i++)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = segs[i].Id,
                    Order = i,
                    SourceText = segs[i].Content,
                    Status = SegmentStatus.Pending
                });
            }

            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = new TranslationQueueWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<TranslationQueueWorker>>(),
            factory.Services.GetRequiredService<IJobCancellationRegistry>(),
            "preflight-worker");

        var processed = await worker.ProcessNextJobAsync(CancellationToken.None);
        Assert.True(processed);
        Assert.Equal(0, providerCalls);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = await db.TranslationJobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("preflight", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, job.RequestCount);
            Assert.Equal(99_800, job.TotalTokens);

            var completedCount = await db.TranslatedSegments.CountAsync(s => s.JobId == jobId && s.Status == SegmentStatus.Completed);
            Assert.Equal(2, completedCount);
        }
    }

    [Fact]
    public async Task TokenBudget_OverrunOnResponse_SavesPaidBatchCheckpointThenHaltsRemaining()
    {
        // F07: Batch 1 fits preflight (remaining 600 vs ~380 est), but provider returns 1100 tokens -> total 100,500
        // Batch 1 checkpoint MUST be saved, and Batch 2 MUST NOT be dispatched
        using var factory = new ControlledWorkerTestFactory { EnableTestAuth = true, DisableBackgroundWorker = true };

        var providerCalls = 0;
        factory.ProviderSelector = (_, _) => new DelegateAiProvider((req, _) =>
        {
            Interlocked.Increment(ref providerCalls);
            return Task.FromResult(new AiResponse(
                BuildMockSegmentedResponse(req.UserPrompt, "Batch1Paid"),
                PromptTokens: 600,
                CompletionTokens: 500,
                TotalTokens: 1_100,
                ModelUsed: req.Model,
                FinishReason: FinishReason.Stop));
        });

        var (novelId, ownerId, _, _) = await SeedNovelAsync(factory, segmentCount: 8, i => $"Line {i}");

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var segs = await db.Segments.Where(s => s.NovelId == novelId).OrderBy(s => s.Order).ToListAsync();

            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "mock",
                Model = "mock-v1",
                TargetLanguage = "vi",
                Status = JobStatus.Queued,
                TotalSegments = 8,
                CompletedSegments = 0,
                RequestCount = 0,
                PromptTokens = 50_000,
                CompletionTokens = 49_400,
                TotalTokens = 99_400, // 600 remaining
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);

            foreach (var s in segs)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = s.Id,
                    Order = s.Order,
                    SourceText = s.Content,
                    Status = SegmentStatus.Pending
                });
            }

            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = new TranslationQueueWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<TranslationQueueWorker>>(),
            factory.Services.GetRequiredService<IJobCancellationRegistry>(),
            "overrun-worker");

        var processed = await worker.ProcessNextJobAsync(CancellationToken.None);
        Assert.True(processed);
        Assert.Equal(1, providerCalls);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = await db.TranslationJobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("budget", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, job.RequestCount);
            Assert.Equal(100_500, job.TotalTokens);
            Assert.Equal(5, job.CompletedSegments);

            var completed = await db.TranslatedSegments.CountAsync(s => s.JobId == jobId && s.Status == SegmentStatus.Completed);
            var pending = await db.TranslatedSegments.CountAsync(s => s.JobId == jobId && s.Status == SegmentStatus.Pending);
            Assert.Equal(5, completed);
            Assert.Equal(3, pending);
        }
    }

    [Fact]
    public async Task TokenBudget_MaxRequestsLimit_HaltsBeforeCallingProvider()
    {
        using var factory = new ControlledWorkerTestFactory { EnableTestAuth = true, DisableBackgroundWorker = true };

        var providerCalls = 0;
        factory.ProviderSelector = (_, _) => new DelegateAiProvider((req, _) =>
        {
            Interlocked.Increment(ref providerCalls);
            return Task.FromResult(new AiResponse(BuildMockSegmentedResponse(req.UserPrompt), 10, 10, 20, req.Model, FinishReason.Stop));
        });

        var (novelId, ownerId, _, _) = await SeedNovelAsync(factory, segmentCount: 3);

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var segs = await db.Segments.Where(s => s.NovelId == novelId).OrderBy(s => s.Order).ToListAsync();

            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "mock",
                Model = "mock-v1",
                TargetLanguage = "vi",
                Status = JobStatus.Queued,
                TotalSegments = 3,
                CompletedSegments = 1,
                RequestCount = TranslationQueueWorker.MaxJobRequests,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);

            db.TranslatedSegments.Add(new TranslatedSegment
            {
                JobId = job.Id,
                SegmentId = segs[0].Id,
                Order = 0,
                SourceText = segs[0].Content,
                TranslatedText = "Done 0",
                Status = SegmentStatus.Completed
            });
            for (var i = 1; i < 3; i++)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = segs[i].Id,
                    Order = i,
                    SourceText = segs[i].Content,
                    Status = SegmentStatus.Pending
                });
            }
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = new TranslationQueueWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<TranslationQueueWorker>>(),
            factory.Services.GetRequiredService<IJobCancellationRegistry>(),
            "req-limit-worker");

        Assert.True(await worker.ProcessNextJobAsync(CancellationToken.None));
        Assert.Equal(0, providerCalls);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = await db.TranslationJobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("request budget", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await db.TranslatedSegments.CountAsync(s => s.JobId == jobId && s.Status == SegmentStatus.Completed));
        }
    }

    [Fact]
    public async Task TokenBudget_OversizedParagraph_FailsOversizedSegmentSafelyWithoutAbortingNormalSegments()
    {
        using var factory = new ControlledWorkerTestFactory { EnableTestAuth = true, DisableBackgroundWorker = true };

        var providerCalls = 0;
        factory.ProviderSelector = (_, _) => new DelegateAiProvider((req, _) =>
        {
            Interlocked.Increment(ref providerCalls);
            return Task.FromResult(new AiResponse(BuildMockSegmentedResponse(req.UserPrompt, "NormalOk"), 20, 20, 40, req.Model, FinishReason.Stop));
        });

        var (novelId, ownerId, _, _) = await SeedNovelAsync(
            factory,
            segmentCount: 2,
            i => i == 0 ? new string('X', TranslationQueueWorker.MaxSingleSegmentChars + 5) : "Normal short paragraph");

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var segs = await db.Segments.Where(s => s.NovelId == novelId).OrderBy(s => s.Order).ToListAsync();

            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "mock",
                Model = "mock-v1",
                TargetLanguage = "vi",
                Status = JobStatus.Queued,
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);

            foreach (var s in segs)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = s.Id,
                    Order = s.Order,
                    SourceText = s.Content,
                    Status = SegmentStatus.Pending
                });
            }
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = new TranslationQueueWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<TranslationQueueWorker>>(),
            factory.Services.GetRequiredService<IJobCancellationRegistry>(),
            "oversized-worker");

        Assert.True(await worker.ProcessNextJobAsync(CancellationToken.None));
        Assert.Equal(1, providerCalls); // Only segment 1 sent to provider; segment 0 blocked cleanly

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var seg0 = await db.TranslatedSegments.FirstAsync(s => s.JobId == jobId && s.Order == 0);
            var seg1 = await db.TranslatedSegments.FirstAsync(s => s.JobId == jobId && s.Order == 1);

            Assert.Equal(SegmentStatus.Failed, seg0.Status);
            Assert.Contains("maximum supported length", seg0.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SegmentStatus.Completed, seg1.Status);
            Assert.Contains("[NormalOk]", seg1.TranslatedText);
        }
    }

    [Fact]
    public async Task Resume_PartiallyCompletedJob_SkipsCheckpointedSegmentsAndNeverRetranslates()
    {
        // V03: Resume job from WaitingForApiKey -> verify only Pending chunks (5..7) are dispatched, never Completed chunks (0..4)
        using var factory = new ControlledWorkerTestFactory { EnableTestAuth = true };

        var recordedTargetSections = new List<string>();
        factory.ProviderSelector = (_, _) => new DelegateAiProvider((req, _) =>
        {
            const string marker = "--- SEGMENTS TO TRANSLATE ---";
            var idx = req.UserPrompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            recordedTargetSections.Add(idx >= 0 ? req.UserPrompt[idx..] : req.UserPrompt);

            return Task.FromResult(new AiResponse(
                BuildMockSegmentedResponse(req.UserPrompt, "Resumed"),
                PromptTokens: 60,
                CompletionTokens: 60,
                TotalTokens: 120,
                ModelUsed: "gemini-2.5-flash",
                FinishReason: FinishReason.Stop));
        });

        var (novelId, ownerId, googleId, email) = await SeedNovelAsync(factory, segmentCount: 8);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", googleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var segs = await db.Segments.Where(s => s.NovelId == novelId).OrderBy(s => s.Order).ToListAsync();

            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "gemini",
                Model = "gemini-2.5-flash",
                TargetLanguage = "vi",
                Status = JobStatus.WaitingForApiKey,
                TotalSegments = 8,
                CompletedSegments = 5,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);

            for (var i = 0; i < 5; i++)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = segs[i].Id,
                    Order = i,
                    SourceText = segs[i].Content,
                    TranslatedText = $"Checkpointed {i}",
                    Status = SegmentStatus.Completed
                });
            }
            for (var i = 5; i < 8; i++)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = segs[i].Id,
                    Order = i,
                    SourceText = segs[i].Content,
                    Status = SegmentStatus.Pending
                });
            }

            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        // Provide key for the paused job via API re-entry endpoint -> resumes to Queued
        var token = await factory.FetchAntiforgeryTokenAsync(client);
        var keyReq = new HttpRequestMessage(HttpMethod.Post, $"/api/translations/{jobId}/api-key")
        {
            Content = JsonContent.Create(new { apiKey = "resume-secret-key-123" })
        };
        keyReq.Headers.Add("X-XSRF-TOKEN", token);

        var keyRes = await client.SendAsync(keyReq);
        Assert.Equal(HttpStatusCode.OK, keyRes.StatusCode);

        // Poll until Completed
        var attempts = 40;
        var status = "Queued";
        while (attempts-- > 0 && status != "Completed" && status != "Failed")
        {
            await Task.Delay(200);
            var pollRes = await client.GetAsync($"/api/translations/{jobId}");
            using var doc = JsonDocument.Parse(await pollRes.Content.ReadAsStringAsync());
            status = doc.RootElement.GetProperty("status").GetString()!;
        }

        Assert.Equal("Completed", status);
        Assert.Single(recordedTargetSections);
        Assert.Contains("[SEG:5]", recordedTargetSections[0]);
        Assert.Contains("[SEG:6]", recordedTargetSections[0]);
        Assert.Contains("[SEG:7]", recordedTargetSections[0]);
        Assert.DoesNotContain("[SEG:0]", recordedTargetSections[0]);
        Assert.DoesNotContain("[SEG:1]", recordedTargetSections[0]);
        Assert.DoesNotContain("[SEG:2]", recordedTargetSections[0]);
        Assert.DoesNotContain("[SEG:3]", recordedTargetSections[0]);
        Assert.DoesNotContain("[SEG:4]", recordedTargetSections[0]);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var allSegs = await db.TranslatedSegments.Where(s => s.JobId == jobId).OrderBy(s => s.Order).ToListAsync();
            Assert.Equal(8, allSegs.Count);
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal($"Checkpointed {i}", allSegs[i].TranslatedText);
            }
            for (var i = 5; i < 8; i++)
            {
                Assert.Equal(SegmentStatus.Completed, allSegs[i].Status);
                Assert.Contains("[Resumed]", allSegs[i].TranslatedText);
            }
        }
    }

    [Fact]
    public async Task LeaseRecovery_TwoWorkersConcurrentAndStaleLease_FencingPreventsStaleOverwrite()
    {
        // V01: Simulate two independent worker processes:
        // 1. Worker 1 claims job and enters provider (stalls).
        // 2. Worker 2 tries while lease fresh -> rejected.
        // 3. Lease ages > 5 min; Worker 2 steals lease and completes with "Worker2".
        // 4. Worker 1 wakes up with stale "Worker1 STALE" -> fencing check rejects write; "Worker2" preserved!
        using var factory = new ControlledWorkerTestFactory { EnableTestAuth = true, DisableBackgroundWorker = true };

        var worker1InProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWorker1ToReturn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callNumber = 0;

        factory.ProviderSelector = (_, _) => new DelegateAiProvider(async (req, _) =>
        {
            var n = Interlocked.Increment(ref callNumber);
            if (n == 1)
            {
                worker1InProvider.TrySetResult(true);
                await allowWorker1ToReturn.Task.WaitAsync(TimeSpan.FromSeconds(15));
                return new AiResponse(
                    BuildMockSegmentedResponse(req.UserPrompt, "Worker1_STALE"),
                    PromptTokens: 10, CompletionTokens: 10, TotalTokens: 20,
                    ModelUsed: req.Model, FinishReason: FinishReason.Stop);
            }

            return new AiResponse(
                BuildMockSegmentedResponse(req.UserPrompt, "Worker2_FRESH"),
                PromptTokens: 15, CompletionTokens: 15, TotalTokens: 30,
                ModelUsed: req.Model, FinishReason: FinishReason.Stop);
        });

        var (novelId, ownerId, _, _) = await SeedNovelAsync(factory, segmentCount: 2);

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var segs = await db.Segments.Where(s => s.NovelId == novelId).OrderBy(s => s.Order).ToListAsync();

            var job = new TranslationJob
            {
                NovelId = novelId,
                OwnerId = ownerId,
                Provider = "mock",
                Model = "mock-v1",
                TargetLanguage = "vi",
                Status = JobStatus.Queued,
                TotalSegments = 2,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TranslationJobs.Add(job);

            foreach (var s in segs)
            {
                db.TranslatedSegments.Add(new TranslatedSegment
                {
                    JobId = job.Id,
                    SegmentId = s.Id,
                    Order = s.Order,
                    SourceText = s.Content,
                    Status = SegmentStatus.Pending
                });
            }
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var logger = factory.Services.GetRequiredService<ILogger<TranslationQueueWorker>>();
        var cancelReg = factory.Services.GetRequiredService<IJobCancellationRegistry>();

        var worker1 = new TranslationQueueWorker(scopeFactory, logger, cancelReg, "proc-1");
        var worker2 = new TranslationQueueWorker(scopeFactory, logger, cancelReg, "proc-2");

        // 1. Start worker1 and wait until it is stalled inside provider call
        var worker1Task = Task.Run(() => worker1.ProcessNextJobAsync(CancellationToken.None));
        await worker1InProvider.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 2. While lease is fresh, worker2 MUST NOT claim the job
        var claimedWhileFresh = await worker2.ProcessNextJobAsync(CancellationToken.None);
        Assert.False(claimedWhileFresh);

        // 3. Age job.UpdatedAt past 5-minute lease timeout (simulating worker1 hang > 5m)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var j = await db.TranslationJobs.FindAsync(jobId);
            Assert.NotNull(j);
            j.UpdatedAt = DateTime.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync();
        }

        // 4. Worker2 steals expired lease and completes the job
        var claimedAfterExpiry = await worker2.ProcessNextJobAsync(CancellationToken.None);
        Assert.True(claimedAfterExpiry);

        // 5. Worker1 wakes up and attempts to commit stale translation results
        allowWorker1ToReturn.TrySetResult(true);
        await worker1Task;

        // 6. Verify fencing protected Worker2's results and status
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var finalJob = await db.TranslationJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
            Assert.Equal(JobStatus.Completed, finalJob.Status);
            Assert.Equal(2, finalJob.CompletedSegments);
            Assert.Equal(30, finalJob.TotalTokens); // Worker2's usage, NOT overwritten by Worker1!

            var finalSegs = await db.TranslatedSegments.AsNoTracking().Where(s => s.JobId == jobId).OrderBy(s => s.Order).ToListAsync();
            Assert.All(finalSegs, s =>
            {
                Assert.Equal(SegmentStatus.Completed, s.Status);
                Assert.Contains("[Worker2_FRESH]", s.TranslatedText);
                Assert.DoesNotContain("[Worker1_STALE]", s.TranslatedText);
            });
        }
    }
}
