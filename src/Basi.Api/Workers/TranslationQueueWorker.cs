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
    private const int BatchSize = 5;

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

        // Pick oldest Queued or interrupted Processing job
        var job = await db.TranslationJobs
            .Include(j => j.Novel)
            .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Processing)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (job == null) return false;

        _logger.LogInformation("Processing translation job {JobId} for novel '{NovelTitle}'", job.Id, job.Novel?.Title);

        // 1. Resolve AI Provider and check RAM key
        Basi.Core.Interfaces.IAiProvider provider;
        try
        {
            provider = providerFactory.GetProviderForUser(job.OwnerId, job.Provider);
        }
        catch (AiProviderError ex) when (ex.ErrorCode == AiErrorCode.InvalidApiKey)
        {
            _logger.LogWarning("Job {JobId} paused: API key missing for provider {Provider}.", job.Id, job.Provider);
            job.Status = JobStatus.WaitingForApiKey;
            job.ErrorMessage = ex.Message;
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
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

        // 4. Process segments in batches
        for (var i = 0; i < pendingSegments.Count; i += BatchSize)
        {
            if (ct.IsCancellationRequested) break;

            // Check if job was cancelled concurrently by user
            var currentStatus = await db.TranslationJobs
                .Where(j => j.Id == job.Id)
                .Select(j => j.Status)
                .FirstOrDefaultAsync(ct);

            if (currentStatus == JobStatus.Cancelled)
            {
                _logger.LogInformation("Job {JobId} was cancelled by user.", job.Id);
                return true;
            }

            var batch = pendingSegments.Skip(i).Take(BatchSize).ToList();

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
                var translations = await translationEngine.TranslateBatchAsync(
                    provider,
                    job.Model,
                    sourceLanguage,
                    job.TargetLanguage,
                    batchItems,
                    contextHistory,
                    ct);

                foreach (var seg in batch)
                {
                    if (translations.TryGetValue(seg.Order, out var translated) && !string.IsNullOrWhiteSpace(translated))
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
                // Key was cleared or expired in RAM
                _logger.LogWarning("Job {JobId} paused during processing: API key missing/invalid.", job.Id);
                foreach (var seg in batch) seg.Status = SegmentStatus.Pending;
                job.Status = JobStatus.WaitingForApiKey;
                job.ErrorMessage = ex.Message;
                job.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to translate batch starting at order {Order} for job {JobId}.", batch[0].Order, job.Id);
                foreach (var seg in batch)
                {
                    seg.Status = SegmentStatus.Failed;
                    seg.ErrorMessage = ex.Message;
                    seg.UpdatedAt = DateTime.UtcNow;
                }
            }

            // Update completed count and save progress
            var completedCount = await db.TranslatedSegments
                .CountAsync(ts => ts.JobId == job.Id && ts.Status == SegmentStatus.Completed, ct);
            if (completedCount == 0)
            {
                completedCount = pendingSegments.Count(s => s.Status == SegmentStatus.Completed);
            }
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
}
