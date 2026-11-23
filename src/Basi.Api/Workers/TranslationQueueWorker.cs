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
    private const int MaxBatchSize = 5;
    private const int MaxBatchChars = 3000;

    public TranslationQueueWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TranslationQueueWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        // Pick oldest Queued or interrupted Processing job
        var job = await db.TranslationJobs
            .Include(j => j.Novel)
            .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Processing)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (job == null) return false;

        _logger.LogInformation("Processing translation job {JobId} for novel '{NovelTitle}'", job.Id, job.Novel?.Title);

        // 1. Initial key existence check for non-mock providers
        if (!string.Equals(job.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            if (!apiKeyVault.HasKey(job.OwnerId, job.Provider))
            {
                _logger.LogWarning("Job {JobId} paused: API key missing for provider {Provider}.", job.Id, job.Provider);
                job.Status = JobStatus.WaitingForApiKey;
                job.ErrorMessage = "API key is missing or invalid. Please provide your API key to resume.";
                job.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return true;
            }
        }

        // 2. Transition to Processing
        job.Status = JobStatus.Processing;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // 3. Load uncompleted segments
        var pendingSegments = await db.TranslatedSegments
            .Where(ts => ts.JobId == job.Id && ts.Status != SegmentStatus.Completed)
            .OrderBy(ts => ts.Order)
            .ToListAsync(ct);

        if (pendingSegments.Count == 0)
        {
            var totalCount = await db.TranslatedSegments.CountAsync(ts => ts.JobId == job.Id, ct);
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
            if (ct.IsCancellationRequested) break;

            // In-flight check if job was cancelled concurrently by user
            var currentStatus = await db.TranslationJobs
                .Where(j => j.Id == job.Id)
                .Select(j => j.Status)
                .FirstOrDefaultAsync(ct);

            if (currentStatus == JobStatus.Cancelled)
            {
                _logger.LogInformation("Job {JobId} was cancelled by user.", job.Id);
                return true;
            }

            // Construct bounded batch (bounded by MaxBatchSize and MaxBatchChars)
            var batch = new List<TranslatedSegment>();
            var accumulatedChars = 0;
            while (segmentIndex < pendingSegments.Count && batch.Count < MaxBatchSize)
            {
                var candidate = pendingSegments[segmentIndex];
                var candidateChars = candidate.SourceText.Length;
                if (batch.Count > 0 && accumulatedChars + candidateChars > MaxBatchChars)
                {
                    break;
                }
                batch.Add(candidate);
                accumulatedChars += candidateChars;
                segmentIndex++;
            }

            // Verify API key is still valid and present in RAM vault before executing batch (F04)
            if (!string.Equals(job.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            {
                if (!apiKeyVault.HasKey(job.OwnerId, job.Provider))
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
            await db.SaveChangesAsync(ct);

            // Fetch recent context (up to 3 completed prior segments)
            var minOrder = batch[0].Order;
            var contextHistory = await db.TranslatedSegments
                .Where(ts => ts.JobId == job.Id && ts.Status == SegmentStatus.Completed && ts.Order < minOrder)
                .OrderByDescending(ts => ts.Order)
                .Take(3)
                .OrderBy(ts => ts.Order)
                .Select(ts => new ContextSegmentItem(ts.Order, ts.SourceText, ts.TranslatedText ?? string.Empty))
                .ToListAsync(ct);

            var batchItems = batch.Select(b => new SegmentItem(b.Order, b.SourceText)).ToList();

            try
            {
                var provider = providerFactory.GetProviderForUser(job.OwnerId, job.Provider);

                var batchResult = await translationEngine.TranslateBatchAsync(
                    provider,
                    job.Model,
                    sourceLanguage,
                    job.TargetLanguage,
                    batchItems,
                    contextHistory,
                    ct);

                // F05: Accumulate usage tokens
                job.PromptTokens += batchResult.PromptTokens;
                job.CompletionTokens += batchResult.CompletionTokens;
                job.TotalTokens += batchResult.TotalTokens;

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to translate batch starting at order {Order} for job {JobId}.", batch[0].Order, job.Id);
                var sanitizedError = SanitizeErrorMessage(ex.Message);
                foreach (var seg in batch)
                {
                    seg.Status = SegmentStatus.Failed;
                    seg.ErrorMessage = sanitizedError;
                    seg.UpdatedAt = DateTime.UtcNow;
                }
            }

            // F06: Save segment status first, THEN query and update CompletedSegments
            await db.SaveChangesAsync(ct);

            var completedCount = await db.TranslatedSegments
                .CountAsync(ts => ts.JobId == job.Id && ts.Status == SegmentStatus.Completed, ct);

            job.CompletedSegments = completedCount;
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // 5. Finalize Job Status
        var finalUncompletedCount = await db.TranslatedSegments
            .CountAsync(ts => ts.JobId == job.Id && ts.Status != SegmentStatus.Completed, ct);

        // Re-check cancellation
        var finalStatus = await db.TranslationJobs
            .Where(j => j.Id == job.Id)
            .Select(j => j.Status)
            .FirstOrDefaultAsync(ct);

        if (finalStatus != JobStatus.Cancelled)
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

        return true;
    }

    private static string SanitizeErrorMessage(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage)) return "An error occurred during translation.";
        if (rawMessage.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            return "Output was truncated due to token limit.";
        if (rawMessage.Contains("content filter", StringComparison.OrdinalIgnoreCase))
            return "Translation was blocked by safety policy.";
        if (rawMessage.Contains("duplicate segment", StringComparison.OrdinalIgnoreCase))
            return "Model returned duplicate segment identifiers.";
        if (rawMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "API rate limit exceeded. Please retry later.";
        if (rawMessage.Contains("API key", StringComparison.OrdinalIgnoreCase))
            return "API key is invalid or unauthorized.";

        return "Translation batch failed. Please retry.";
    }
}
