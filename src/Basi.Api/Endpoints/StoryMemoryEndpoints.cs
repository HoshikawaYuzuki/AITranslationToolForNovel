using System.Security.Claims;
using System.Text.RegularExpressions;
using Basi.Api.Data;
using Basi.Core.Interfaces;
using Basi.Core.Models;
using Basi.Module.Identity.Models;
using Basi.Module.StoryMemory.Services;
using Basi.Module.Translation.Providers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Basi.Api.Endpoints;

public static partial class StoryMemoryEndpoints
{
    public static IEndpointRouteBuilder MapStoryMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/novels/{novelId:guid}/story-memory
        app.MapGet("/api/novels/{novelId:guid}/story-memory", async (
            HttpContext ctx,
            Guid novelId,
            string? targetLanguage,
            string? category,
            string? search,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var novelOwned = await db.Novels.AnyAsync(n => n.Id == novelId && n.OwnerId == user.Id, ct);
            if (!novelOwned) return Results.NotFound(new { error = "Novel not found or access denied." });

            var query = db.StoryEntities
                .AsNoTracking()
                .Where(e => e.NovelId == novelId && e.OwnerId == user.Id);

            if (!string.IsNullOrWhiteSpace(targetLanguage))
            {
                var normLang = targetLanguage.Trim();
                if (!IsValidLanguageTag(normLang))
                    return Results.BadRequest(new { error = "Invalid targetLanguage format." });
                query = query.Where(e => e.TargetLanguage.ToLower() == normLang.ToLower());
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                if (!StoryEntityNormalizer.TryParseCategory(category, out var parsedCategory))
                    return Results.BadRequest(new { error = $"Invalid category '{category}'." });
                query = query.Where(e => e.Category == parsedCategory);
            }

            var entities = await query
                .OrderBy(e => e.Category)
                .ThenBy(e => e.FirstSeenOrder ?? int.MaxValue)
                .ThenBy(e => e.SourceTerm)
                .ToListAsync(ct);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var qKey = StoryEntityNormalizer.NormalizeKey(search);
                entities = entities.Where(e =>
                        StoryEntityNormalizer.NormalizeKey(e.SourceTerm).Contains(qKey, StringComparison.Ordinal)
                        || StoryEntityNormalizer.NormalizeKey(e.TranslatedTerm).Contains(qKey, StringComparison.Ordinal)
                        || (e.Description != null && StoryEntityNormalizer.NormalizeKey(e.Description).Contains(qKey, StringComparison.Ordinal))
                        || e.Aliases.Any(a => StoryEntityNormalizer.NormalizeKey(a).Contains(qKey, StringComparison.Ordinal)))
                    .ToList();
            }

            return Results.Ok(entities.Select(ToDto));
        }).RequireAuthorization();

        // POST /api/novels/{novelId:guid}/story-memory
        app.MapPost("/api/novels/{novelId:guid}/story-memory", async (
            HttpContext ctx,
            Guid novelId,
            UpsertStoryEntityRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var novelOwned = await db.Novels.AnyAsync(n => n.Id == novelId && n.OwnerId == user.Id, ct);
            if (!novelOwned) return Results.NotFound(new { error = "Novel not found or access denied." });

            var validation = ValidateUpsertRequest(req);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var duplicateExists = await db.StoryEntities.AnyAsync(
                e => e.NovelId == novelId
                    && e.TargetLanguage.ToLower() == validation.TargetLanguage.ToLower()
                    && e.NormalizedSourceTerm == validation.NormalizedSourceKey,
                ct);
            if (duplicateExists)
                return Results.Conflict(new { error = "A story memory entry for this source term and target language already exists." });

            var now = DateTime.UtcNow;
            var entity = new StoryEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = user.Id,
                NovelId = novelId,
                TargetLanguage = validation.TargetLanguage,
                SourceTerm = validation.SourceTerm,
                NormalizedSourceTerm = validation.NormalizedSourceKey,
                TranslatedTerm = validation.TranslatedTerm,
                Category = validation.Category,
                Aliases = validation.Aliases,
                Description = validation.Description,
                Origin = StoryEntityOrigin.Manual,
                IsLocked = req.IsLocked ?? true,
                FirstSeenOrder = req.FirstSeenOrder is >= 0 ? req.FirstSeenOrder : null,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.StoryEntities.Add(entity);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                return Results.Conflict(new { error = "A story memory entry for this source term and target language already exists." });
            }

            return Results.Created($"/api/novels/{novelId}/story-memory/{entity.Id}", ToDto(entity));
        }).RequireAuthorization();

        // PUT /api/novels/{novelId:guid}/story-memory/{entryId:guid}
        app.MapPut("/api/novels/{novelId:guid}/story-memory/{entryId:guid}", async (
            HttpContext ctx,
            Guid novelId,
            Guid entryId,
            UpsertStoryEntityRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var entity = await db.StoryEntities
                .FirstOrDefaultAsync(e => e.Id == entryId && e.NovelId == novelId && e.OwnerId == user.Id, ct);
            if (entity is null) return Results.NotFound(new { error = "Story memory entry not found or access denied." });

            var validation = ValidateUpsertRequest(req);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var duplicateExists = await db.StoryEntities.AnyAsync(
                e => e.Id != entryId
                    && e.NovelId == novelId
                    && e.TargetLanguage.ToLower() == validation.TargetLanguage.ToLower()
                    && e.NormalizedSourceTerm == validation.NormalizedSourceKey,
                ct);
            if (duplicateExists)
                return Results.Conflict(new { error = "Another story memory entry with this source term and target language already exists." });

            entity.TargetLanguage = validation.TargetLanguage;
            entity.SourceTerm = validation.SourceTerm;
            entity.NormalizedSourceTerm = validation.NormalizedSourceKey;
            entity.TranslatedTerm = validation.TranslatedTerm;
            entity.Category = validation.Category;
            entity.Aliases = validation.Aliases;
            entity.Description = validation.Description;
            entity.IsLocked = req.IsLocked ?? entity.IsLocked;
            if (req.FirstSeenOrder is >= 0)
            {
                entity.FirstSeenOrder = req.FirstSeenOrder;
            }
            entity.UpdatedAt = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                return Results.Conflict(new { error = "Another story memory entry with this source term and target language already exists." });
            }

            return Results.Ok(ToDto(entity));
        }).RequireAuthorization();

        // DELETE /api/novels/{novelId:guid}/story-memory/{entryId:guid}
        app.MapDelete("/api/novels/{novelId:guid}/story-memory/{entryId:guid}", async (
            HttpContext ctx,
            Guid novelId,
            Guid entryId,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var entity = await db.StoryEntities
                .FirstOrDefaultAsync(e => e.Id == entryId && e.NovelId == novelId && e.OwnerId == user.Id, ct);
            if (entity is null) return Results.NotFound(new { error = "Story memory entry not found or access denied." });

            db.StoryEntities.Remove(entity);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { deleted = true, id = entryId });
        }).RequireAuthorization();

        // POST /api/novels/{novelId:guid}/story-memory/extract
        app.MapPost("/api/novels/{novelId:guid}/story-memory/extract", async (
            HttpContext ctx,
            Guid novelId,
            ExtractStoryMemoryRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAiProviderFactory providerFactory,
            IStoryMemoryExtractor extractor,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.TargetLanguage) || !IsValidLanguageTag(req.TargetLanguage.Trim()))
                return Results.BadRequest(new { error = "Invalid TargetLanguage format. Must be a valid BCP-47 tag (e.g. vi, en, ja)." });
            var targetLang = req.TargetLanguage.Trim();

            if (string.IsNullOrWhiteSpace(req.Provider))
                return Results.BadRequest(new { error = "Provider is required." });
            var normProvider = req.Provider.Trim().ToLowerInvariant();
            if (normProvider is not ("gemini" or "openai" or "mock"))
                return Results.BadRequest(new { error = "Provider must be 'gemini', 'openai', or 'mock'." });

            var model = string.IsNullOrWhiteSpace(req.Model)
                ? (normProvider == "gemini" ? "gemini-2.5-flash" : normProvider == "openai" ? "gpt-4o-mini" : "mock-v1")
                : req.Model.Trim();
            if (model.Length > 128 || !ModelNameRegex().IsMatch(model))
                return Results.BadRequest(new { error = "Invalid model name format." });

            if (req.StartOrder < 0 || req.EndOrder < 0 || (req.StartOrder.HasValue && req.EndOrder.HasValue && req.EndOrder < req.StartOrder))
                return Results.BadRequest(new { error = "Invalid segment range." });

            var novel = await db.Novels
                .Include(n => n.Segments)
                .FirstOrDefaultAsync(n => n.Id == novelId && n.OwnerId == user.Id, ct);
            if (novel is null) return Results.NotFound(new { error = "Novel not found or access denied." });

            var orderedSegments = novel.Segments
                .OrderBy(s => s.Order)
                .Where(s => (!req.StartOrder.HasValue || s.Order >= req.StartOrder.Value)
                         && (!req.EndOrder.HasValue || s.Order <= req.EndOrder.Value))
                .ToList();
            if (orderedSegments.Count == 0)
                return Results.BadRequest(new { error = "No segments matched the specified extraction range." });

            List<(int Order, string Content)> window;
            if (req.StartOrder.HasValue && req.EndOrder.HasValue)
            {
                if (orderedSegments.Count > StoryMemoryExtractor.MaxExtractionSegments)
                    return Results.BadRequest(new { error = $"Explicit extraction range exceeds maximum {StoryMemoryExtractor.MaxExtractionSegments} segments per request." });

                var totalExplicitChars = orderedSegments.Sum(s => s.Content.Length);
                if (totalExplicitChars > StoryMemoryExtractor.MaxExtractionChars)
                    return Results.BadRequest(new { error = $"Explicit extraction range exceeds maximum {StoryMemoryExtractor.MaxExtractionChars} characters per request." });

                window = orderedSegments.Select(s => (s.Order, s.Content)).ToList();
            }
            else
            {
                window = new List<(int Order, string Content)>();
                var accChars = 0;
                foreach (var seg in orderedSegments)
                {
                    if (window.Count >= StoryMemoryExtractor.MaxExtractionSegments) break;
                    if (accChars + seg.Content.Length > StoryMemoryExtractor.MaxExtractionChars) break;
                    window.Add((seg.Order, seg.Content));
                    accChars += seg.Content.Length;
                }
                if (window.Count == 0)
                    return Results.BadRequest(new { error = $"First segment exceeds maximum single extraction character budget ({StoryMemoryExtractor.MaxExtractionChars})." });
            }

            IAiProvider provider;
            try
            {
                // ADR-004: ephemeral apiKey stays strictly in RAM for this call, otherwise user vault key
                provider = !string.IsNullOrWhiteSpace(req.ApiKey)
                    ? providerFactory.CreateProvider(normProvider, req.ApiKey.Trim())
                    : providerFactory.GetProviderForUser(user.Id, normProvider);
            }
            catch (AiProviderError ex) when (ex.ErrorCode == AiErrorCode.InvalidApiKey)
            {
                return Results.BadRequest(new { error = $"API key missing or invalid for provider '{normProvider}'. Provide an ephemeral key or save one in your RAM vault." });
            }

            var existingEntities = await db.StoryEntities
                .Where(e => e.NovelId == novelId && e.OwnerId == user.Id && e.TargetLanguage.ToLower() == targetLang.ToLower())
                .ToListAsync(ct);

            StoryMemoryExtractionResult extraction;
            try
            {
                extraction = await extractor.ExtractAsync(
                    provider,
                    model,
                    novel.SourceLanguage,
                    targetLang,
                    window,
                    existingEntities,
                    ct);
            }
            catch (AiProviderError ex) when (ex.ErrorCode == AiErrorCode.InvalidApiKey)
            {
                return Results.BadRequest(new { error = $"Provider rejected the API key for '{normProvider}'." });
            }
            catch (StoryMemoryExtractionException ex)
            {
                var logger = loggerFactory.CreateLogger("StoryMemoryExtraction");
                var safeMsg = SanitizeExtractionError(ex.Message);
                logger.LogWarning("Story memory extraction failed for novel {NovelId}: {Error}", novelId, safeMsg);
                return Results.Json(
                    new
                    {
                        error = safeMsg,
                        promptTokens = ex.PromptTokens,
                        completionTokens = ex.CompletionTokens,
                        totalTokens = ex.TotalTokens,
                        modelUsed = ex.ModelUsed
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var byNormKey = existingEntities.ToDictionary(e => e.NormalizedSourceTerm, StringComparer.Ordinal);
            var created = 0;
            var updated = 0;
            var skippedLocked = 0;
            var now = DateTime.UtcNow;
            var defaultOrder = window[0].Order;

            foreach (var cand in extraction.Candidates)
            {
                var normKey = StoryEntityNormalizer.NormalizeKey(cand.SourceTerm);
                if (string.IsNullOrEmpty(normKey)) continue;

                if (byNormKey.TryGetValue(normKey, out var existing))
                {
                    if (existing.IsLocked || existing.Origin == StoryEntityOrigin.Manual)
                    {
                        skippedLocked++;
                        continue;
                    }

                    existing.TranslatedTerm = cand.TranslatedTerm;
                    existing.Category = cand.Category;
                    existing.Aliases = StoryEntityNormalizer.NormalizeAliases(existing.SourceTerm, existing.Aliases.Concat(cand.Aliases));
                    existing.Description ??= cand.Description;
                    existing.FirstSeenOrder ??= cand.FirstSeenOrder ?? defaultOrder;
                    existing.UpdatedAt = now;
                    updated++;
                }
                else
                {
                    var entity = new StoryEntity
                    {
                        Id = Guid.NewGuid(),
                        OwnerId = user.Id,
                        NovelId = novelId,
                        TargetLanguage = targetLang,
                        SourceTerm = cand.SourceTerm,
                        NormalizedSourceTerm = normKey,
                        TranslatedTerm = cand.TranslatedTerm,
                        Category = cand.Category,
                        Aliases = cand.Aliases,
                        Description = cand.Description,
                        Origin = StoryEntityOrigin.AiExtracted,
                        IsLocked = false,
                        FirstSeenOrder = cand.FirstSeenOrder ?? defaultOrder,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    db.StoryEntities.Add(entity);
                    byNormKey[normKey] = entity;
                    existingEntities.Add(entity);
                    created++;
                }
            }

            await db.SaveChangesAsync(ct);

            var sortedGlossary = existingEntities
                .OrderBy(e => e.Category)
                .ThenBy(e => e.FirstSeenOrder ?? int.MaxValue)
                .ThenBy(e => e.SourceTerm)
                .Select(ToDto)
                .ToList();

            return Results.Ok(new
            {
                created,
                updated,
                skippedLocked,
                extractedCount = extraction.Candidates.Count,
                promptTokens = extraction.PromptTokens,
                completionTokens = extraction.CompletionTokens,
                totalTokens = extraction.TotalTokens,
                modelUsed = extraction.ModelUsed,
                glossary = sortedGlossary
            });
        }).RequireAuthorization();

        // GET /api/translations/{jobId:guid}/consistency
        app.MapGet("/api/translations/{jobId:guid}/consistency", async (
            HttpContext ctx,
            Guid jobId,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IStoryMemorySelector selector,
            CancellationToken ct) =>
        {
            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var job = await db.TranslationJobs
                .AsNoTracking()
                .Include(j => j.TranslatedSegments)
                .FirstOrDefaultAsync(j => j.Id == jobId && j.OwnerId == user.Id, ct);
            if (job is null) return Results.NotFound(new { error = "Translation job not found or access denied." });

            var glossary = await db.StoryEntities
                .AsNoTracking()
                .Where(e => e.NovelId == job.NovelId
                         && e.OwnerId == user.Id
                         && e.TargetLanguage.ToLower() == job.TargetLanguage.ToLower())
                .ToListAsync(ct);

            var completedSegments = job.TranslatedSegments
                .Where(ts => ts.Status == SegmentStatus.Completed && !string.IsNullOrWhiteSpace(ts.TranslatedText))
                .OrderBy(ts => ts.Order)
                .Select(ts => (ts.Order, ts.SourceText, ts.TranslatedText))
                .ToList();

            var issues = selector.CheckConsistency(glossary, completedSegments);

            return Results.Ok(new
            {
                jobId = job.Id,
                novelId = job.NovelId,
                targetLanguage = job.TargetLanguage,
                checkedSegments = completedSegments.Count,
                glossaryCount = glossary.Count,
                issueCount = issues.Count,
                issues
            });
        }).RequireAuthorization();

        return app;
    }

    private static async Task<ApplicationUser?> ResolveOwnerAsync(HttpContext ctx, UserManager<ApplicationUser> userManager)
    {
        if (ctx.User.Identity?.IsAuthenticated != true) return null;
        var googleId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (googleId is null) return null;
        return await userManager.FindByLoginAsync("Google", googleId);
    }

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext ctx, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static UpsertValidationResult ValidateUpsertRequest(UpsertStoryEntityRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetLanguage) || !IsValidLanguageTag(req.TargetLanguage.Trim()))
            return UpsertValidationResult.Fail("Invalid TargetLanguage format. Must be a valid BCP-47 tag (e.g. vi, en, ja).");

        var sourceTerm = StoryEntityNormalizer.NormalizeDisplayTerm(req.SourceTerm);
        if (string.IsNullOrWhiteSpace(sourceTerm))
            return UpsertValidationResult.Fail("SourceTerm is required.");

        var translatedTerm = StoryEntityNormalizer.NormalizeDisplayTerm(req.TranslatedTerm);
        if (string.IsNullOrWhiteSpace(translatedTerm))
            return UpsertValidationResult.Fail("TranslatedTerm is required.");

        if (!StoryEntityNormalizer.TryParseCategory(req.Category, out var category))
            return UpsertValidationResult.Fail($"Invalid Category '{req.Category}'.");

        var normalizedKey = StoryEntityNormalizer.NormalizeKey(sourceTerm);
        var aliases = StoryEntityNormalizer.NormalizeAliases(sourceTerm, req.Aliases);
        var description = StoryEntityNormalizer.NormalizeDescription(req.Description);

        return new UpsertValidationResult(
            null,
            req.TargetLanguage.Trim(),
            sourceTerm,
            normalizedKey,
            translatedTerm,
            category,
            aliases,
            description);
    }

    private static object ToDto(StoryEntity e) => new
    {
        id = e.Id,
        novelId = e.NovelId,
        targetLanguage = e.TargetLanguage,
        sourceTerm = e.SourceTerm,
        translatedTerm = e.TranslatedTerm,
        category = e.Category.ToString(),
        aliases = e.Aliases,
        description = e.Description,
        origin = e.Origin.ToString(),
        isLocked = e.IsLocked,
        firstSeenOrder = e.FirstSeenOrder,
        createdAt = e.CreatedAt,
        updatedAt = e.UpdatedAt
    };

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static bool IsValidLanguageTag(string tag) => tag.Length <= 32 && LanguageTagRegex().IsMatch(tag);

    private static string SanitizeExtractionError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Story memory extraction failed.";
        if (raw.Contains("truncated", StringComparison.OrdinalIgnoreCase)) return "Extraction output was truncated due to token limit.";
        if (raw.Contains("content filter", StringComparison.OrdinalIgnoreCase) || raw.Contains("safety", StringComparison.OrdinalIgnoreCase)) return "Extraction was blocked by safety policy.";
        if (raw.Contains("invalid story memory JSON", StringComparison.OrdinalIgnoreCase)) return "Model returned invalid story memory JSON.";
        if (raw.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return "API rate limit exceeded. Please retry later.";
        return "Story memory extraction failed. Please retry.";
    }

    [GeneratedRegex(@"^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$")]
    private static partial Regex LanguageTagRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.\/:]+$")]
    private static partial Regex ModelNameRegex();

    private sealed record UpsertValidationResult(
        string? Error,
        string TargetLanguage,
        string SourceTerm,
        string NormalizedSourceKey,
        string TranslatedTerm,
        StoryEntityCategory Category,
        List<string> Aliases,
        string? Description)
    {
        public static UpsertValidationResult Fail(string error) =>
            new(error, string.Empty, string.Empty, string.Empty, string.Empty, StoryEntityCategory.Other, new List<string>(), null);
    }
}

public sealed record UpsertStoryEntityRequest(
    string TargetLanguage,
    string SourceTerm,
    string TranslatedTerm,
    string? Category = null,
    IReadOnlyList<string>? Aliases = null,
    string? Description = null,
    bool? IsLocked = null,
    int? FirstSeenOrder = null);

public sealed record ExtractStoryMemoryRequest(
    string TargetLanguage,
    string Provider,
    string? Model = null,
    int? StartOrder = null,
    int? EndOrder = null,
    string? ApiKey = null);
