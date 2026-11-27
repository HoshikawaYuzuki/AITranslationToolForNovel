using System.Diagnostics;
using Basi.Api.Data;
using Basi.Core.Interfaces;
using Basi.Core.Models;
using Basi.Module.StoryMemory.Services;
using Basi.Module.Translation.Providers;
using Basi.Module.Translation.Services;
using Microsoft.EntityFrameworkCore;

namespace Basi.Api.Workers;

/// <summary>
/// Background worker processing queued translation jobs from PostgreSQL (ADR-005).
/// Executes the state machine transitions defined in CONTRACTS.md.
/// </summary>
public sealed class TranslationQueueWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranslationQueueWorker> _logger;
    private readonly Basi.Core.Interfaces.IJobCancellationRegistry _cancellationRegistry;
    private readonly string _workerInstanceId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string WorkerId, Guid JobId), byte> _activeJobClaims = new();

    internal static bool HasActiveClaim(Guid jobId) => _activeJobClaims.Keys.Any(k => k.JobId == jobId);
    internal static void ClearActiveClaims() => _activeJobClaims.Clear();
    internal static int ActiveClaimCount => _activeJobClaims.Count;
    internal static Func<Guid, Task>? OnBeforeFinallyStatusQueryForTesting { get; set; }
    internal static TimeSpan? RunDurationOverrideForTesting { get; set; }

    internal const int MaxBatchSize = 5;
    internal const int MaxBatchChars = 3000;
    internal const int MaxSingleSegmentChars = 12000;
    internal const int MaxContextChars = 1500;
    internal const int MaxJobTotalTokens = 100_000;
    internal const int MaxJobRequests = 200;
    internal static readonly TimeSpan MaxJobRunDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    public TranslationQueueWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TranslationQueueWorker> logger,
        Basi.Core.Interfaces.IJobCancellationRegistry cancellationRegistry)
        : this(scopeFactory, logger, cancellationRegistry, "default")
    {
    }

    internal TranslationQueueWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TranslationQueueWorker> logger,
        Basi.Core.Interfaces.IJobCancellationRegistry cancellationRegistry,
        string workerInstanceId)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _workerInstanceId = string.IsNullOrWhiteSpace(workerInstanceId) ? "default" : workerInstanceId;
    }

    internal static int EstimatePreflightInputTokens(int batchChars, int batchCount, int contextChars, int contextCount, int glossaryChars = 0, int glossaryCount = 0)
    {
        const int systemPromptTokens = 160;
        var glossaryOverhead = glossaryCount > 0 ? 20 + (glossaryChars / 3) + (glossaryCount * 6) : 0;
        return systemPromptTokens
            + (batchChars / 3) + (batchCount * 8)
            + (contextChars / 3) + (contextCount * 8)
            + glossaryOverhead;
    }

    internal static int EstimatePreflightBatchTokens(int batchChars, int batchCount, int contextChars, int contextCount, int glossaryChars = 0, int glossaryCount = 0)
    {
        var inputTokens = EstimatePreflightInputTokens(batchChars, batchCount, contextChars, contextCount, glossaryChars, glossaryCount);
        var expectedOutputTokens = Math.Max(150, (batchChars / 2) + (batchCount * 8));
        return inputTokens + expectedOutputTokens;
    }

    private static DateTime NextLeaseTimestamp(DateTime previousLease)
    {
        var baseUtc = previousLease.Kind == DateTimeKind.Utc ? previousLease : previousLease.ToUniversalTime();
        var baseMicro = new DateTime(baseUtc.Ticks - (baseUtc.Ticks % 10), DateTimeKind.Utc);
        var nowUtc = DateTime.UtcNow;
        var nowMicro = new DateTime(nowUtc.Ticks - (nowUtc.Ticks % 10), DateTimeKind.Utc);
        return nowMicro <= baseMicro ? baseMicro.AddTicks(10) : nowMicro;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TranslationQueueWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextJobAsync(stoppingToken);
                if (!processed)
                {
                    // No queued jobs found, wait before polling again
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in TranslationQueueWorker loop.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }

        _logger.LogInformation("TranslationQueueWorker stopped.");
    }

    internal async Task<bool> ProcessNextJobAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var providerFactory = scope.ServiceProvider.GetRequiredService<IAiProviderFactory>();
        var translationEngine = scope.ServiceProvider.GetRequiredService<ITranslationEngine>();
        var apiKeyVault = scope.ServiceProvider.GetRequiredService<IApiKeyVault>();

        // N02 / F08: Exclusive claim condition
        // A job can only be claimed if it is Queued OR stalled in Processing beyond LeaseTimeout
        var staleThreshold = DateTime.UtcNow - LeaseTimeout;

        var candidate = await db.TranslationJobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued || (j.Status == JobStatus.Processing && j.UpdatedAt < staleThreshold))
            .OrderBy(j => j.CreatedAt)
            .Select(j => new { j.Id, j.Status })
            .FirstOrDefaultAsync(ct);

        if (candidate == null) return false;

        // In-process guard preventing concurrent execution by the same worker instance
        var claimKey = (_workerInstanceId, candidate.Id);
        if (!_activeJobClaims.TryAdd(claimKey, 0))
        {
            return false;
        }

        try
        {
            var currentLease = NextLeaseTimestamp(DateTime.UtcNow);
            if (!await TryClaimJobAsync(db, candidate.Id, staleThreshold, currentLease, ct))
            {
                return false;
            }

            var job = await db.TranslationJobs
                .AsNoTracking()
                .Include(j => j.Novel)
                .FirstOrDefaultAsync(j => j.Id == candidate.Id, ct);

            if (job == null) return false;

            var currentTotalTokens = job.TotalTokens;
            var currentRequestCount = job.RequestCount;
            var currentActualModel = job.ActualModel;
            var runStopwatch = Stopwatch.StartNew();

            // F08: Register linked cancellation source for genuine in-flight cancellation
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var jobCancelReg = _cancellationRegistry.RegisterJob(job.Id, jobCts);

            _logger.LogInformation("Processing translation job {JobId} for novel '{NovelTitle}'", job.Id, job.Novel?.Title);

            List<TranslatedSegment>? currentBatch = null;
            try
            {
                // 1. Initial key existence check (F04: check Job-bound key first, then user key)
                if (!string.Equals(job.Provider, "mock", StringComparison.OrdinalIgnoreCase))
                {
                    var hasKey = apiKeyVault.HasJobKey(job.Id) || apiKeyVault.HasKey(job.OwnerId, job.Provider);
                    if (!hasKey)
                    {
                        _logger.LogWarning("Job {JobId} paused: API key missing for provider {Provider}.", job.Id, job.Provider);
                        await TryFencedPauseWaitingForKeyAsync(
                            db, job.Id, currentLease, "API key is missing or invalid. Please provide your API key to resume.", ct);
                        return true;
                    }
                }

                // 2. Load uncompleted segments
                var pendingSegments = await db.TranslatedSegments
                    .Where(ts => ts.JobId == job.Id && ts.Status != SegmentStatus.Completed)
                    .OrderBy(ts => ts.Order)
                    .ToListAsync(jobCts.Token);

                if (pendingSegments.Count == 0)
                {
                    var totalCount = await db.TranslatedSegments.CountAsync(ts => ts.JobId == job.Id, jobCts.Token);
                    if (await TryFinalizeJobAsync(db, job.Id, currentLease, totalCount, totalCount > 0 ? 0 : 1, totalCount == 0 ? "Job has no segments to translate." : null, ct))
                    {
                        apiKeyVault.RemoveJobKey(job.Id);
                    }
                    return true;
                }

                var sourceLanguage = job.Novel?.SourceLanguage ?? "ja";
                var storyMemorySelector = scope.ServiceProvider.GetService<IStoryMemorySelector>() ?? new StoryMemorySelector();
                var storyEntities = await db.StoryEntities
                    .AsNoTracking()
                    .Where(e => e.NovelId == job.NovelId && e.TargetLanguage.ToLower() == job.TargetLanguage.ToLower())
                    .ToListAsync(jobCts.Token);

                var effectiveRunDuration = RunDurationOverrideForTesting ?? MaxJobRunDuration;

                // 3. Process segments in bounded batches (by size, char count, token budget, request budget, and active run deadline)
                var segmentIndex = 0;
                while (segmentIndex < pendingSegments.Count)
                {
                    if (ct.IsCancellationRequested || jobCts.IsCancellationRequested) break;

                    var remainingRunTime = effectiveRunDuration - runStopwatch.Elapsed;
                    if (remainingRunTime <= TimeSpan.Zero)
                    {
                        _logger.LogWarning("Job {JobId} exceeded active processing time limit ({Duration}). Halting further batches.", job.Id, effectiveRunDuration);
                        await TryFencedFailJobAsync(db, job.Id, currentLease, $"Job exceeded maximum processing time budget of {effectiveRunDuration.TotalMinutes:0.##} minutes.", apiKeyVault, ct);
                        return true;
                    }

                    if (currentRequestCount >= MaxJobRequests)
                    {
                        _logger.LogWarning("Job {JobId} reached maximum request budget ({MaxRequests}). Halting further batches.", job.Id, MaxJobRequests);
                        await TryFencedFailJobAsync(db, job.Id, currentLease, $"Job exceeded maximum request budget of {MaxJobRequests} requests.", apiKeyVault, ct);
                        return true;
                    }

                    // F07: Inter-batch token budget check
                    if (currentTotalTokens >= MaxJobTotalTokens)
                    {
                        _logger.LogWarning("Job {JobId} reached total token budget ({Budget}). Halting further batches.", job.Id, MaxJobTotalTokens);
                        await TryFencedFailJobAsync(db, job.Id, currentLease, $"Job exceeded total token budget of {MaxJobTotalTokens:N0} tokens.", apiKeyVault, ct);
                        return true;
                    }

                    // In-flight check if job was cancelled concurrently by user or lease was stolen
                    var jobState = await ReadLatestJobStateAsync(db, job.Id, ct);

                    if (jobState == null || jobState.Status == JobStatus.Cancelled || jobCts.IsCancellationRequested)
                    {
                        _logger.LogInformation("Job {JobId} was cancelled by user.", job.Id);
                        apiKeyVault.RemoveJobKey(job.Id);
                        return true;
                    }

                    if (jobState.Status != JobStatus.Processing || jobState.UpdatedAt != currentLease)
                    {
                        _logger.LogWarning("Lost lease for job {JobId} before batch selection; aborting stale worker.", job.Id);
                        return true;
                    }

                    // Construct bounded batch (bounded by MaxBatchSize and MaxBatchChars)
                    var batch = new List<TranslatedSegment>();
                    var accumulatedChars = 0;
                    while (segmentIndex < pendingSegments.Count && batch.Count < MaxBatchSize)
                    {
                        var candidateSeg = pendingSegments[segmentIndex];

                        // F07: Single segment character bound check
                        if (candidateSeg.SourceText.Length > MaxSingleSegmentChars)
                        {
                            if (batch.Count == 0)
                            {
                                var nextSegLease = NextLeaseTimestamp(currentLease);
                                candidateSeg.Status = SegmentStatus.Failed;
                                candidateSeg.ErrorMessage = $"Segment source text exceeds maximum supported length ({MaxSingleSegmentChars} characters).";
                                candidateSeg.UpdatedAt = nextSegLease;
                                var savedSeg = await TryFencedSaveOversizedSegmentFailureAsync(db, job.Id, currentLease, nextSegLease, candidateSeg, jobCts.Token);
                                if (!savedSeg)
                                {
                                    _logger.LogWarning("Lost lease for job {JobId} while failing oversized segment.", job.Id);
                                    return true;
                                }
                                currentLease = nextSegLease;
                                segmentIndex++;
                                continue;
                            }
                            break;
                        }

                        var candidateChars = candidateSeg.SourceText.Length;
                        if (batch.Count > 0 && accumulatedChars + candidateChars > MaxBatchChars)
                        {
                            break;
                        }
                        batch.Add(candidateSeg);
                        accumulatedChars += candidateChars;
                        segmentIndex++;
                    }

                    if (batch.Count == 0) continue;

                    // Verify API key is still valid and present in RAM vault before executing batch (F04)
                    if (!string.Equals(job.Provider, "mock", StringComparison.OrdinalIgnoreCase))
                    {
                        var hasKey = apiKeyVault.HasJobKey(job.Id) || apiKeyVault.HasKey(job.OwnerId, job.Provider);
                        if (!hasKey)
                        {
                            _logger.LogWarning("Job {JobId} paused during processing: API key was revoked or cleared from RAM.", job.Id);
                            await TryFencedPauseWaitingForKeyAsync(
                                db, job.Id, currentLease, "API key was removed or cleared from memory. Please provide your key to resume.", ct);
                            return true;
                        }
                    }

                    // Fetch recent context with budget (F07: capped at MaxContextChars)
                    var minOrder = batch[0].Order;
                    var contextCandidates = await db.TranslatedSegments
                        .AsNoTracking()
                        .Where(ts => ts.JobId == job.Id && ts.Status == SegmentStatus.Completed && ts.Order < minOrder)
                        .OrderByDescending(ts => ts.Order)
                        .Take(3)
                        .Select(ts => new ContextSegmentItem(ts.Order, ts.SourceText, ts.TranslatedText ?? string.Empty))
                        .ToListAsync(jobCts.Token);

                    var contextHistory = new List<ContextSegmentItem>();
                    var totalContextChars = 0;
                    foreach (var ctxSeg in contextCandidates)
                    {
                        if (totalContextChars + ctxSeg.TranslatedText.Length <= MaxContextChars)
                        {
                            contextHistory.Add(ctxSeg);
                            totalContextChars += ctxSeg.TranslatedText.Length;
                        }
                    }
                    contextHistory.Reverse();

                    // BASI-004: Select batch-relevant bounded story memory / glossary terms
                    var glossaryTerms = storyMemorySelector.SelectForBatch(
                        storyEntities,
                        batch.Select(b => b.SourceText).ToList(),
                        contextHistory.Select(c => c.SourceText).ToList());
                    var glossaryChars = glossaryTerms.Sum(StoryMemorySelector.EstimatePromptLineLength);

                    // F07: Preflight token budget check & output cap BEFORE dispatching provider request
                    var remainingBudget = Math.Max(0, MaxJobTotalTokens - currentTotalTokens);
                    var estimatedInputTokens = EstimatePreflightInputTokens(accumulatedChars, batch.Count, totalContextChars, contextHistory.Count, glossaryChars, glossaryTerms.Count);
                    var estimatedBatchTokens = EstimatePreflightBatchTokens(accumulatedChars, batch.Count, totalContextChars, contextHistory.Count, glossaryChars, glossaryTerms.Count);
                    if (currentTotalTokens + estimatedBatchTokens > MaxJobTotalTokens)
                    {
                        _logger.LogWarning(
                            "Job {JobId} stopped by preflight token budget check: estimated batch tokens ({Estimated}) exceed remaining budget ({Remaining} of {Budget}).",
                            job.Id, estimatedBatchTokens, remainingBudget, MaxJobTotalTokens);
                        await TryFencedFailJobAsync(
                            db,
                            job.Id,
                            currentLease,
                            $"Job exceeded total token budget of {MaxJobTotalTokens:N0} tokens (preflight estimated {estimatedBatchTokens:N0} tokens exceed remaining {remainingBudget:N0}).",
                            apiKeyVault,
                            ct);
                        return true;
                    }

                    var remainingRunTimeForCall = effectiveRunDuration - runStopwatch.Elapsed;
                    if (remainingRunTimeForCall <= TimeSpan.Zero)
                    {
                        _logger.LogWarning("Job {JobId} exceeded active processing time limit ({Duration}) before provider dispatch.", job.Id, effectiveRunDuration);
                        await TryFencedFailJobAsync(db, job.Id, currentLease, $"Job exceeded maximum processing time budget of {effectiveRunDuration.TotalMinutes:0.##} minutes.", apiKeyVault, ct);
                        return true;
                    }

                    var maxOutputTokensForRequest = Math.Clamp(remainingBudget - estimatedInputTokens, 1, 4096);

                    IAiProvider provider;
                    try
                    {
                        provider = providerFactory.GetProviderForJob(job.OwnerId, job.Provider, job.Id);
                    }
                    catch (AiProviderError ex) when (ex.ErrorCode == AiErrorCode.InvalidApiKey)
                    {
                        _logger.LogWarning("Job {JobId} paused during processing: API key missing/invalid.", job.Id);
                        await TryFencedPauseWaitingForKeyAsync(
                            db, job.Id, currentLease, "API key is missing or invalid. Please provide a valid API key to resume.", ct);
                        return true;
                    }

                    // Mark batch as Translating & advance lease atomically with fencing check
                    var dispatchLease = NextLeaseTimestamp(currentLease);
                    if (!await TryMarkBatchTranslatingAndIncrementRequestAsync(db, job.Id, currentLease, dispatchLease, batch, jobCts.Token))
                    {
                        _logger.LogWarning("Lost lease or cancelled for job {JobId} before calling AI provider.", job.Id);
                        return true;
                    }
                    currentLease = dispatchLease;
                    currentRequestCount++;
                    currentBatch = batch;

                    var batchItems = batch.Select(b => new SegmentItem(b.Order, b.SourceText)).ToList();

                    using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(jobCts.Token);
                    deadlineCts.CancelAfter(remainingRunTimeForCall);

                    try
                    {
                        var batchResult = await translationEngine.TranslateBatchAsync(
                            provider,
                            job.Model,
                            sourceLanguage,
                            job.TargetLanguage,
                            batchItems,
                            contextHistory,
                            deadlineCts.Token,
                            maxOutputTokensForRequest,
                            glossaryTerms);

                        var commitLease = NextLeaseTimestamp(currentLease);
                        var nextActualModel = !string.IsNullOrWhiteSpace(batchResult.ModelUsed) ? batchResult.ModelUsed : currentActualModel;
                        var committed = await TryCommitBatchWithUsageAsync(
                            db,
                            job.Id,
                            currentLease,
                            commitLease,
                            batchResult.PromptTokens,
                            batchResult.CompletionTokens,
                            batchResult.TotalTokens,
                            nextActualModel,
                            batch,
                            (segs, ts) => ApplySuccessToBatchSegments(segs, batchResult, ts),
                            ct);
                        if (!committed)
                        {
                            _logger.LogWarning("Lost lease or cancelled for job {JobId}; discarding stale worker batch results.", job.Id);
                            return true;
                        }

                        currentLease = commitLease;
                        currentTotalTokens += batchResult.TotalTokens;
                        currentActualModel = nextActualModel;
                        currentBatch = null;

                        // If this batch pushed cumulative tokens to/over budget and more segments remain, halt immediately
                        if (currentTotalTokens >= MaxJobTotalTokens && segmentIndex < pendingSegments.Count)
                        {
                            _logger.LogWarning("Job {JobId} reached total token budget ({Budget}) after batch completion. Halting remaining segments.", job.Id, MaxJobTotalTokens);
                            await TryFencedFailJobAsync(db, job.Id, currentLease, $"Job exceeded total token budget of {MaxJobTotalTokens:N0} tokens.", apiKeyVault, ct);
                            return true;
                        }
                    }
                    catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw; // Handled uniformly by outer job-cancellation catch
                    }
                    catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("Job {JobId} active processing deadline elapsed during in-flight provider request.", job.Id);
                        await RevertTranslatingBatchAndSetStatusAsync(
                            db,
                            job.Id,
                            currentLease,
                            batch,
                            JobStatus.Failed,
                            $"Job exceeded maximum processing time budget of {effectiveRunDuration.TotalMinutes:0.##} minutes.",
                            ct);
                        apiKeyVault.RemoveJobKey(job.Id);
                        currentBatch = null;
                        return true;
                    }
                    catch (AiProviderError ex) when (ex.ErrorCode == AiErrorCode.InvalidApiKey)
                    {
                        _logger.LogWarning("Job {JobId} paused during processing: API key missing/invalid.", job.Id);
                        await RevertTranslatingBatchAndSetStatusAsync(
                            db,
                            job.Id,
                            currentLease,
                            batch,
                            JobStatus.WaitingForApiKey,
                            "API key is missing or invalid. Please provide a valid API key to resume.",
                            ct);
                        currentBatch = null;
                        return true;
                    }
                    catch (TranslationBatchException ex)
                    {
                        var sanitizedError = SanitizeErrorMessage(ex.Message);
                        _logger.LogError("Failed to translate batch starting at order {Order} for job {JobId}: {ErrorMessage}", batch[0].Order, job.Id, sanitizedError);

                        var failLease = NextLeaseTimestamp(currentLease);
                        var nextActualModel = !string.IsNullOrWhiteSpace(ex.ModelUsed) ? ex.ModelUsed : currentActualModel;
                        var committed = await TryCommitBatchWithUsageAsync(
                            db,
                            job.Id,
                            currentLease,
                            failLease,
                            ex.PromptTokens,
                            ex.CompletionTokens,
                            ex.TotalTokens,
                            nextActualModel,
                            batch,
                            (segs, ts) => ApplyFailureToBatchSegments(segs, sanitizedError, ts),
                            ct);
                        if (!committed)
                        {
                            _logger.LogWarning("Lost lease for job {JobId}; discarding stale batch error state.", job.Id);
                            return true;
                        }
                        currentLease = failLease;
                        currentTotalTokens += ex.TotalTokens;
                        currentActualModel = nextActualModel;
                        currentBatch = null;
                    }
                    catch (Exception ex)
                    {
                        var sanitizedError = SanitizeErrorMessage(ex.Message);
                        _logger.LogError("Failed to translate batch starting at order {Order} for job {JobId}: {ErrorMessage}", batch[0].Order, job.Id, sanitizedError);

                        var failLease = NextLeaseTimestamp(currentLease);
                        var committed = await TryCommitBatchWithUsageAsync(
                            db,
                            job.Id,
                            currentLease,
                            failLease,
                            0,
                            0,
                            0,
                            currentActualModel,
                            batch,
                            (segs, ts) => ApplyFailureToBatchSegments(segs, sanitizedError, ts),
                            ct);
                        if (!committed)
                        {
                            _logger.LogWarning("Lost lease for job {JobId}; discarding stale batch error state.", job.Id);
                            return true;
                        }
                        currentLease = failLease;
                        currentBatch = null;
                    }
                }

                // 4. Finalize Job Status (fenced to ensure we don't overwrite Cancelled or a newer worker's lease)
                if (!jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    var totalCount = await db.TranslatedSegments.CountAsync(ts => ts.JobId == job.Id, ct);
                    var finalUncompletedCount = await db.TranslatedSegments
                        .CountAsync(ts => ts.JobId == job.Id && ts.Status != SegmentStatus.Completed, ct);

                    var errorMsg = finalUncompletedCount == 0 ? null : $"{finalUncompletedCount} segment(s) failed translation.";
                    var finalized = await TryFinalizeJobAsync(db, job.Id, currentLease, totalCount, finalUncompletedCount, errorMsg, ct);
                    if (finalized)
                    {
                        apiKeyVault.RemoveJobKey(job.Id);
                    }
                }

                return true;
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("Job {JobId} in-flight execution cancelled.", job.Id);
                await HandleCancellationCleanupAsync(db, job.Id, currentLease, currentBatch, apiKeyVault);
                return true;
            }
        }
        finally
        {
            try
            {
                if (OnBeforeFinallyStatusQueryForTesting != null)
                {
                    await OnBeforeFinallyStatusQueryForTesting(candidate.Id);
                }

                var finalStatus = await db.TranslationJobs
                    .AsNoTracking()
                    .Where(j => j.Id == candidate.Id)
                    .Select(j => j.Status)
                    .FirstOrDefaultAsync(CancellationToken.None);

                if (finalStatus is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                {
                    apiKeyVault.RemoveJobKey(candidate.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query or cleanup final status for job {JobId} in finally block.", candidate.Id);
            }
            finally
            {
                _activeJobClaims.TryRemove(claimKey, out _);
            }
        }
    }

    private sealed record JobLeaseState(JobStatus Status, DateTime UpdatedAt);

    private async Task<JobLeaseState?> ReadLatestJobStateAsync(BasiDbContext db, Guid jobId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            return await db.TranslationJobs
                .AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => new JobLeaseState(j.Status, j.UpdatedAt))
                .FirstOrDefaultAsync(ct);
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        return await fenceDb.TranslationJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new JobLeaseState(j.Status, j.UpdatedAt))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> TryClaimJobAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime staleThreshold,
        DateTime claimLease,
        CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var rowsClaimed = await db.TranslationJobs
                .Where(j => j.Id == jobId && (j.Status == JobStatus.Queued || (j.Status == JobStatus.Processing && j.UpdatedAt < staleThreshold)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Processing)
                    .SetProperty(j => j.UpdatedAt, claimLease), ct);

            return rowsClaimed > 0;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null) return false;
        if (entity.Status != JobStatus.Queued && !(entity.Status == JobStatus.Processing && entity.UpdatedAt < staleThreshold))
        {
            return false;
        }
        entity.Status = JobStatus.Processing;
        entity.UpdatedAt = claimLease;
        await fenceDb.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> TryFencedPauseWaitingForKeyAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        string errorMessage,
        CancellationToken ct)
    {
        var nextLease = NextLeaseTimestamp(currentLease);
        if (db.Database.IsRelational())
        {
            var rows = await db.TranslationJobs
                .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.WaitingForApiKey)
                    .SetProperty(j => j.ErrorMessage, errorMessage)
                    .SetProperty(j => j.UpdatedAt, nextLease), ct);
            return rows > 0;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null || entity.Status != JobStatus.Processing || entity.UpdatedAt != currentLease) return false;
        entity.Status = JobStatus.WaitingForApiKey;
        entity.ErrorMessage = errorMessage;
        entity.UpdatedAt = nextLease;
        await fenceDb.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> TryFencedFailJobAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        string errorMessage,
        IApiKeyVault apiKeyVault,
        CancellationToken ct)
    {
        var nextLease = NextLeaseTimestamp(currentLease);
        if (db.Database.IsRelational())
        {
            var rows = await db.TranslationJobs
                .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Failed)
                    .SetProperty(j => j.ErrorMessage, errorMessage)
                    .SetProperty(j => j.UpdatedAt, nextLease), ct);
            if (rows > 0) apiKeyVault.RemoveJobKey(jobId);
            return rows > 0;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null || entity.Status != JobStatus.Processing || entity.UpdatedAt != currentLease) return false;
        entity.Status = JobStatus.Failed;
        entity.ErrorMessage = errorMessage;
        entity.UpdatedAt = nextLease;
        await fenceDb.SaveChangesAsync(ct);
        apiKeyVault.RemoveJobKey(jobId);
        return true;
    }

    private async Task<bool> TryFencedSaveOversizedSegmentFailureAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        DateTime nextLease,
        TranslatedSegment failedSeg,
        CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.TranslationJobs
                .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.UpdatedAt, nextLease), ct);

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                DiscardSegmentChanges(db, new[] { failedSeg });
                return false;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null || entity.Status != JobStatus.Processing || entity.UpdatedAt != currentLease)
        {
            DiscardSegmentChanges(db, new[] { failedSeg });
            return false;
        }
        entity.UpdatedAt = nextLease;
        await fenceDb.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> TryMarkBatchTranslatingAndIncrementRequestAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        DateTime nextLease,
        IReadOnlyList<TranslatedSegment> batch,
        CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.TranslationJobs
                .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.UpdatedAt, nextLease)
                    .SetProperty(j => j.RequestCount, j => j.RequestCount + 1), ct);

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            foreach (var seg in batch)
            {
                seg.Status = SegmentStatus.Translating;
                seg.UpdatedAt = nextLease;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null || entity.Status != JobStatus.Processing || entity.UpdatedAt != currentLease) return false;
        entity.UpdatedAt = nextLease;
        entity.RequestCount++;
        await fenceDb.SaveChangesAsync(ct);

        foreach (var seg in batch)
        {
            seg.Status = SegmentStatus.Translating;
            seg.UpdatedAt = nextLease;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> TryCommitBatchWithUsageAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        DateTime nextLease,
        int promptDelta,
        int completionDelta,
        int totalDelta,
        string? actualModel,
        IReadOnlyList<TranslatedSegment> batch,
        Action<IReadOnlyList<TranslatedSegment>, DateTime> applySegments,
        CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.TranslationJobs
                .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.UpdatedAt, nextLease)
                    .SetProperty(j => j.PromptTokens, j => j.PromptTokens + promptDelta)
                    .SetProperty(j => j.CompletionTokens, j => j.CompletionTokens + completionDelta)
                    .SetProperty(j => j.TotalTokens, j => j.TotalTokens + totalDelta)
                    .SetProperty(j => j.ActualModel, actualModel), ct);

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                DiscardSegmentChanges(db, batch);
                return false;
            }

            applySegments(batch, nextLease);
            await db.SaveChangesAsync(ct);

            var completedCount = await db.TranslatedSegments
                .CountAsync(ts => ts.JobId == jobId && ts.Status == SegmentStatus.Completed, ct);

            await db.TranslationJobs
                .Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CompletedSegments, completedCount), ct);

            await tx.CommitAsync(ct);
            return true;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null || entity.Status != JobStatus.Processing || entity.UpdatedAt != currentLease)
        {
            DiscardSegmentChanges(db, batch);
            return false;
        }

        applySegments(batch, nextLease);
        await db.SaveChangesAsync(ct);

        var completed = await fenceDb.TranslatedSegments
            .CountAsync(ts => ts.JobId == jobId && ts.Status == SegmentStatus.Completed, ct);

        entity.PromptTokens += promptDelta;
        entity.CompletionTokens += completionDelta;
        entity.TotalTokens += totalDelta;
        if (!string.IsNullOrWhiteSpace(actualModel)) entity.ActualModel = actualModel;
        entity.CompletedSegments = completed;
        entity.UpdatedAt = nextLease;
        await fenceDb.SaveChangesAsync(ct);
        return true;
    }

    private static void ApplyFailureToBatchSegments(
        IReadOnlyList<TranslatedSegment> batch,
        string sanitizedError,
        DateTime timestamp)
    {
        foreach (var seg in batch)
        {
            seg.Status = SegmentStatus.Failed;
            seg.ErrorMessage = sanitizedError;
            seg.UpdatedAt = timestamp;
        }
    }

    private async Task RevertTranslatingBatchAndSetStatusAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        IReadOnlyList<TranslatedSegment> batch,
        JobStatus targetStatus,
        string errorMessage,
        CancellationToken ct)
    {
        var nextLease = NextLeaseTimestamp(currentLease);
        if (db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.TranslationJobs
                .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, targetStatus)
                    .SetProperty(j => j.ErrorMessage, errorMessage)
                    .SetProperty(j => j.UpdatedAt, nextLease), ct);

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                DiscardSegmentChanges(db, batch);
                return;
            }

            foreach (var seg in batch)
            {
                if (seg.Status == SegmentStatus.Translating)
                {
                    seg.Status = SegmentStatus.Pending;
                    seg.UpdatedAt = nextLease;
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null || entity.Status != JobStatus.Processing || entity.UpdatedAt != currentLease)
        {
            DiscardSegmentChanges(db, batch);
            return;
        }

        entity.Status = targetStatus;
        entity.ErrorMessage = errorMessage;
        entity.UpdatedAt = nextLease;
        await fenceDb.SaveChangesAsync(ct);

        foreach (var seg in batch)
        {
            if (seg.Status == SegmentStatus.Translating)
            {
                seg.Status = SegmentStatus.Pending;
                seg.UpdatedAt = nextLease;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> TryFinalizeJobAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        int totalSegments,
        int finalUncompletedCount,
        string? errorMessage,
        CancellationToken ct)
    {
        var nextLease = NextLeaseTimestamp(currentLease);
        var targetStatus = finalUncompletedCount == 0 ? JobStatus.Completed : JobStatus.Failed;

        if (db.Database.IsRelational())
        {
            var rows = finalUncompletedCount == 0
                ? await db.TranslationJobs
                    .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, JobStatus.Completed)
                        .SetProperty(j => j.CompletedSegments, totalSegments)
                        .SetProperty(j => j.ErrorMessage, (string?)null)
                        .SetProperty(j => j.UpdatedAt, nextLease), ct)
                : await db.TranslationJobs
                    .Where(j => j.Id == jobId && j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, JobStatus.Failed)
                        .SetProperty(j => j.ErrorMessage, errorMessage)
                        .SetProperty(j => j.UpdatedAt, nextLease), ct);

            return rows > 0;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (entity == null || entity.Status != JobStatus.Processing || entity.UpdatedAt != currentLease) return false;
        entity.Status = targetStatus;
        if (finalUncompletedCount == 0)
        {
            entity.CompletedSegments = totalSegments;
            entity.ErrorMessage = null;
        }
        else
        {
            entity.ErrorMessage = errorMessage;
        }
        entity.UpdatedAt = nextLease;
        await fenceDb.SaveChangesAsync(ct);
        return true;
    }

    private async Task HandleCancellationCleanupAsync(
        BasiDbContext db,
        Guid jobId,
        DateTime currentLease,
        IReadOnlyList<TranslatedSegment>? activeBatch,
        IApiKeyVault apiKeyVault)
    {
        var nextLease = NextLeaseTimestamp(currentLease);
        if (db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var rows = await db.TranslationJobs
                .Where(j => j.Id == jobId && (j.Status == JobStatus.Cancelled || (j.Status == JobStatus.Processing && j.UpdatedAt == currentLease)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Cancelled)
                    .SetProperty(j => j.UpdatedAt, nextLease), CancellationToken.None);

            if (rows == 0)
            {
                await tx.RollbackAsync(CancellationToken.None);
                if (activeBatch != null) DiscardSegmentChanges(db, activeBatch);
                return;
            }

            if (activeBatch != null)
            {
                foreach (var seg in activeBatch)
                {
                    if (seg.Status == SegmentStatus.Translating)
                    {
                        seg.Status = SegmentStatus.Pending;
                        seg.UpdatedAt = nextLease;
                    }
                }
                await db.SaveChangesAsync(CancellationToken.None);
            }

            await tx.CommitAsync(CancellationToken.None);
            apiKeyVault.RemoveJobKey(jobId);
            return;
        }

        using var fenceScope = _scopeFactory.CreateScope();
        var fenceDb = fenceScope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var entity = await fenceDb.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
        if (entity != null && (entity.Status == JobStatus.Cancelled || (entity.Status == JobStatus.Processing && entity.UpdatedAt == currentLease)))
        {
            entity.Status = JobStatus.Cancelled;
            entity.UpdatedAt = nextLease;
            await fenceDb.SaveChangesAsync(CancellationToken.None);

            if (activeBatch != null)
            {
                foreach (var seg in activeBatch)
                {
                    if (seg.Status == SegmentStatus.Translating)
                    {
                        seg.Status = SegmentStatus.Pending;
                        seg.UpdatedAt = nextLease;
                    }
                }
                await db.SaveChangesAsync(CancellationToken.None);
            }
            apiKeyVault.RemoveJobKey(jobId);
        }
        else if (activeBatch != null)
        {
            DiscardSegmentChanges(db, activeBatch);
        }
    }

    private static void ApplySuccessToBatchSegments(
        IReadOnlyList<TranslatedSegment> batch,
        TranslationBatchResult batchResult,
        DateTime timestamp)
    {
        foreach (var seg in batch)
        {
            if (batchResult.Translations.TryGetValue(seg.Order, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
                seg.TranslatedText = translated;
                seg.Status = SegmentStatus.Completed;
                seg.ErrorMessage = null;
            }
            else
            {
                seg.Status = SegmentStatus.Failed;
                seg.ErrorMessage = "Segment translation missing from AI response.";
            }
            seg.UpdatedAt = timestamp;
        }
    }

    private static void DiscardSegmentChanges(BasiDbContext db, IReadOnlyList<TranslatedSegment> batch)
    {
        foreach (var seg in batch)
        {
            var entry = db.Entry(seg);
            if (entry.State == EntityState.Modified)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
        }
    }

    private static string SanitizeErrorMessage(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage)) return "An error occurred during translation.";
        if (rawMessage.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            return "Output was truncated due to token limit.";
        if (rawMessage.Contains("content filter", StringComparison.OrdinalIgnoreCase) || rawMessage.Contains("safety", StringComparison.OrdinalIgnoreCase))
            return "Translation was blocked by safety policy.";
        if (rawMessage.Contains("duplicate segment", StringComparison.OrdinalIgnoreCase))
            return "Model returned duplicate segment identifiers.";
        if (rawMessage.Contains("unexpected segment", StringComparison.OrdinalIgnoreCase))
            return "Model returned unexpected segment identifiers.";
        if (rawMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "API rate limit exceeded. Please retry later.";
        if (rawMessage.Contains("API key", StringComparison.OrdinalIgnoreCase))
            return "API key is invalid or unauthorized.";

        return "Translation batch failed. Please retry.";
    }
}
