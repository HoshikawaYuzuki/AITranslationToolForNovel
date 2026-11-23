using Basi.Api.Data;
using Basi.Core.Models;
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _activeJobClaims = new();

    private const int MaxBatchSize = 5;
    private const int MaxBatchChars = 3000;
    private const int MaxSingleSegmentChars = 12000;
    private const int MaxContextChars = 1500;
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    public TranslationQueueWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TranslationQueueWorker> logger,
        Basi.Core.Interfaces.IJobCancellationRegistry cancellationRegistry)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
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

    private async Task<bool> ProcessNextJobAsync(CancellationToken ct)
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
            .Where(j => j.Status == JobStatus.Queued || (j.Status == JobStatus.Processing && j.UpdatedAt < staleThreshold))
            .OrderBy(j => j.CreatedAt)
            .Select(j => new { j.Id, j.Status })
            .FirstOrDefaultAsync(ct);

        if (candidate == null) return false;

        // In-process guard preventing concurrent execution in the same process
        if (!_activeJobClaims.TryAdd(candidate.Id, 0))
        {
            return false;
        }

        try
        {
            // N02 / F08: Cross-process atomic conditional claim
            // If already claimed by another process (Status == Processing && UpdatedAt >= staleThreshold), 0 rows updated
            if (db.Database.IsRelational())
            {
                var rowsClaimed = await db.TranslationJobs
                    .Where(j => j.Id == candidate.Id && (j.Status == JobStatus.Queued || (j.Status == JobStatus.Processing && j.UpdatedAt < staleThreshold)))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, JobStatus.Processing)
                        .SetProperty(j => j.UpdatedAt, DateTime.UtcNow), ct);

                if (rowsClaimed == 0) return false;
            }
            else
            {
                // InMemory / test environment fallback
                var entity = await db.TranslationJobs.FirstOrDefaultAsync(j => j.Id == candidate.Id, ct);
                if (entity == null) return false;
                if (entity.Status != JobStatus.Queued && !(entity.Status == JobStatus.Processing && entity.UpdatedAt < staleThreshold))
                {
                    return false;
                }
                entity.Status = JobStatus.Processing;
                entity.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            var job = await db.TranslationJobs
                .Include(j => j.Novel)
                .FirstOrDefaultAsync(j => j.Id == candidate.Id, ct);

            if (job == null) return false;

            // F08: Register linked cancellation source for genuine in-flight cancellation
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var jobCancelReg = _cancellationRegistry.RegisterJob(job.Id, jobCts);

            _logger.LogInformation("Processing translation job {JobId} for novel '{NovelTitle}'", job.Id, job.Novel?.Title);

            // 1. Initial key existence check (F04: check Job-bound key first, then user key)
            if (!string.Equals(job.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            {
                var hasKey = apiKeyVault.HasJobKey(job.Id) || apiKeyVault.HasKey(job.OwnerId, job.Provider);
                if (!hasKey)
                {
                    _logger.LogWarning("Job {JobId} paused: API key missing for provider {Provider}.", job.Id, job.Provider);
                    job.Status = JobStatus.WaitingForApiKey;
                    job.ErrorMessage = "API key is missing or invalid. Please provide your API key to resume.";
                    job.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return true;
                }
            }

            // 2. Transition to Processing for non-relational / InMemory DB
            if (!db.Database.IsRelational() && job.Status != JobStatus.Processing)
            {
                job.Status = JobStatus.Processing;
                job.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            // 3. Load uncompleted segments
            var pendingSegments = await db.TranslatedSegments
                .Where(ts => ts.JobId == job.Id && ts.Status != SegmentStatus.Completed)
                .OrderBy(ts => ts.Order)
                .ToListAsync(jobCts.Token);

            if (pendingSegments.Count == 0)
            {
                var totalCount = await db.TranslatedSegments.CountAsync(ts => ts.JobId == job.Id, jobCts.Token);
                if (totalCount > 0)
                {
                    job.CompletedSegments = totalCount;
                    job.Status = JobStatus.Completed;
                    job.ErrorMessage = null;
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = "Job has no segments to translate.";
                }
                job.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return true;
            }

            var sourceLanguage = job.Novel?.SourceLanguage ?? "ja";

            // 4. Process segments in bounded batches (by size and char count)
            var segmentIndex = 0;
            while (segmentIndex < pendingSegments.Count)
            {
                if (ct.IsCancellationRequested || jobCts.IsCancellationRequested) break;

                // In-flight check if job was cancelled concurrently by user
                var currentStatus = await db.TranslationJobs
                    .Where(j => j.Id == job.Id)
                    .Select(j => j.Status)
                    .FirstOrDefaultAsync(ct);

                if (currentStatus == JobStatus.Cancelled || jobCts.IsCancellationRequested)
                {
                    _logger.LogInformation("Job {JobId} was cancelled by user.", job.Id);
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
                            candidateSeg.Status = SegmentStatus.Failed;
                            candidateSeg.ErrorMessage = $"Segment source text exceeds maximum supported length ({MaxSingleSegmentChars} characters).";
                            candidateSeg.UpdatedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync(jobCts.Token);
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
                        foreach (var seg in batch) seg.Status = SegmentStatus.Pending;
                        job.Status = JobStatus.WaitingForApiKey;
                        job.ErrorMessage = "API key was removed or cleared from memory. Please provide your key to resume.";
                        job.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                        return true;
                    }
                }

                // Mark batch as Translating
                foreach (var seg in batch)
                {
                    seg.Status = SegmentStatus.Translating;
                }
                await db.SaveChangesAsync(jobCts.Token);

                // Fetch recent context with budget (F07: capped at MaxContextChars)
                var minOrder = batch[0].Order;
                var contextCandidates = await db.TranslatedSegments
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

                var batchItems = batch.Select(b => new SegmentItem(b.Order, b.SourceText)).ToList();

                try
                {
                    var provider = providerFactory.GetProviderForJob(job.OwnerId, job.Provider, job.Id);

                    var batchResult = await translationEngine.TranslateBatchAsync(
                        provider,
                        job.Model,
                        sourceLanguage,
                        job.TargetLanguage,
                        batchItems,
                        contextHistory,
                        jobCts.Token);

                    // F05: Accumulate usage tokens and persist actual model used
                    job.PromptTokens += batchResult.PromptTokens;
                    job.CompletionTokens += batchResult.CompletionTokens;
                    job.TotalTokens += batchResult.TotalTokens;
                    if (!string.IsNullOrWhiteSpace(batchResult.ModelUsed))
                    {
                        job.Model = batchResult.ModelUsed;
                    }

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
                        seg.UpdatedAt = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Job {JobId} in-flight execution cancelled by user request.", job.Id);
                    job.Status = JobStatus.Cancelled;
                    job.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                    return true;
                }
                catch (AiProviderError ex) when (ex.ErrorCode == AiErrorCode.InvalidApiKey)
                {
                    _logger.LogWarning("Job {JobId} paused during processing: API key missing/invalid.", job.Id);
                    foreach (var seg in batch) seg.Status = SegmentStatus.Pending;
                    job.Status = JobStatus.WaitingForApiKey;
                    job.ErrorMessage = "API key is missing or invalid. Please provide a valid API key to resume.";
                    job.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return true;
                }
                catch (TranslationBatchException ex)
                {
                    // F05: Record usage tokens consumed by failed/truncated request!
                    job.PromptTokens += ex.PromptTokens;
                    job.CompletionTokens += ex.CompletionTokens;
                    job.TotalTokens += ex.TotalTokens;
                    if (!string.IsNullOrWhiteSpace(ex.ModelUsed))
                    {
                        job.Model = ex.ModelUsed;
                    }

                    var sanitizedError = SanitizeErrorMessage(ex.Message);
                    _logger.LogError("Failed to translate batch starting at order {Order} for job {JobId}: {ErrorMessage}", batch[0].Order, job.Id, sanitizedError);

                    foreach (var seg in batch)
                    {
                        seg.Status = SegmentStatus.Failed;
                        seg.ErrorMessage = sanitizedError;
                        seg.UpdatedAt = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    var sanitizedError = SanitizeErrorMessage(ex.Message);
                    _logger.LogError("Failed to translate batch starting at order {Order} for job {JobId}: {ErrorMessage}", batch[0].Order, job.Id, sanitizedError);
                    foreach (var seg in batch)
                    {
                        seg.Status = SegmentStatus.Failed;
                        seg.ErrorMessage = sanitizedError;
                        seg.UpdatedAt = DateTime.UtcNow;
                    }
                }

                // F06: Save segment status first, THEN query and update CompletedSegments
                await db.SaveChangesAsync(jobCts.Token);

                var completedCount = await db.TranslatedSegments
                    .CountAsync(ts => ts.JobId == job.Id && ts.Status == SegmentStatus.Completed, jobCts.Token);

                job.CompletedSegments = completedCount;
                job.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(jobCts.Token);
            }

            // 5. Finalize Job Status
            var finalUncompletedCount = await db.TranslatedSegments
                .CountAsync(ts => ts.JobId == job.Id && ts.Status != SegmentStatus.Completed, ct);

            // Re-check cancellation
            var finalStatus = await db.TranslationJobs
                .Where(j => j.Id == job.Id)
                .Select(j => j.Status)
                .FirstOrDefaultAsync(ct);

            if (finalStatus != JobStatus.Cancelled && !jobCts.IsCancellationRequested)
            {
                if (finalUncompletedCount == 0)
                {
                    job.Status = JobStatus.Completed;
                    job.CompletedSegments = job.TotalSegments;
                    job.ErrorMessage = null;
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = $"{finalUncompletedCount} segment(s) failed translation.";
                }
                job.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            // F04: Purge Job-bound key from RAM upon reaching terminal state
            apiKeyVault.RemoveJobKey(job.Id);

            return true;
        }
        finally
        {
            _activeJobClaims.TryRemove(candidate.Id, out _);
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
