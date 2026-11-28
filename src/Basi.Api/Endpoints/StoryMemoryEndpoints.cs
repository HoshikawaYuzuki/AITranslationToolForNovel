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
    private const int MaxMergeRetryAttempts = 3;

    public static IEndpointRouteBuilder MapStoryMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        MapEntityEndpoints(app);
        MapSceneFactEndpoints(app);
        MapSpeechObservationEndpoints(app);
        MapExtractionAndConsistencyEndpoints(app);
        return app;
    }

    private static void MapEntityEndpoints(IEndpointRouteBuilder app)
    {
        // GET /api/novels/{novelId:guid}/story-memory
        app.MapGet("/api/novels/{novelId:guid}/story-memory", async (
            HttpContext ctx,
            Guid novelId,
            string? targetLanguage,
            string? category,
            string? status,
            string? search,
            int? startOrder,
            int? endOrder,
            Guid? evidenceSegmentId,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            var (user, err) = await EnsureNovelOwnedAsync(ctx, novelId, db, userManager, ct);
            if (err is not null) return err;

            if (!IsValidOrderRange(startOrder, endOrder, out var rangeErr))
                return Results.BadRequest(new { error = rangeErr });

            var query = db.StoryEntities
                .AsNoTracking()
                .Where(e => e.NovelId == novelId && e.OwnerId == user!.Id);

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

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!StoryEntityNormalizer.TryParseFactStatus(status, out var parsedStatus))
                    return Results.BadRequest(new { error = $"Invalid status '{status}'." });
                query = query.Where(e => e.Status == parsedStatus);
            }

            if (evidenceSegmentId.HasValue && evidenceSegmentId.Value != Guid.Empty)
            {
                query = query.Where(e => e.EvidenceSegmentId == evidenceSegmentId.Value);
            }

            if (startOrder.HasValue || endOrder.HasValue)
            {
                query = query.Where(e =>
                    e.EvidenceSegmentOrder.HasValue
                    && (!startOrder.HasValue || e.EvidenceSegmentOrder.Value >= startOrder.Value)
                    && (!endOrder.HasValue || e.EvidenceSegmentOrder.Value <= endOrder.Value));
            }

            var entities = await query
                .OrderBy(e => e.Category)
                .ThenBy(e => e.EvidenceSegmentOrder ?? int.MaxValue)
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

            var (user, err) = await EnsureNovelOwnedAsync(ctx, novelId, db, userManager, ct);
            if (err is not null) return err;

            var validation = ValidateEntityRequest(req);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var (evSeg, evErr) = await ValidateManualEvidenceAsync(db, novelId, req.EvidenceSegmentId, validation.EvidenceQuote, ct);
            if (evErr != null)
                return Results.BadRequest(new { error = evErr });

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
                OwnerId = user!.Id,
                NovelId = novelId,
                TargetLanguage = validation.TargetLanguage,
                SourceTerm = validation.SourceTerm,
                NormalizedSourceTerm = validation.NormalizedSourceKey,
                TranslatedTerm = validation.TranslatedTerm,
                Category = validation.Category,
                Aliases = validation.Aliases,
                Description = validation.Description,
                Origin = StoryEntityOrigin.Manual,
                Status = validation.Status,
                ConflictDetails = validation.Status == FactStatus.Conflicted ? validation.ConflictDetails : null,
                IsLocked = req.IsLocked ?? true,
                EvidenceSegmentId = evSeg?.Id,
                EvidenceSegmentStableId = evSeg?.StableId,
                EvidenceSegmentOrder = evSeg?.Order,
                EvidenceQuote = validation.EvidenceQuote,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.StoryEntities.Add(entity);
            var conflictResult = await SaveWithConflictHandlingAsync(
                db,
                "A story memory entry for this source term and target language already exists.",
                ct);
            if (conflictResult != null) return conflictResult;

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

            if (req.ExpectedVersion.HasValue && req.ExpectedVersion.Value != entity.Version)
                return Results.Conflict(new { error = "Story memory entry was modified concurrently.", currentVersion = entity.Version });

            var validation = ValidateEntityRequest(req, entity.Status);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var (evSeg, evErr) = await ValidateManualEvidenceAsync(db, novelId, req.EvidenceSegmentId, validation.EvidenceQuote, ct);
            if (evErr != null)
                return Results.BadRequest(new { error = evErr });

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
            entity.Status = validation.Status;
            entity.ConflictDetails = validation.Status == FactStatus.Conflicted ? validation.ConflictDetails : null;
            entity.IsLocked = req.IsLocked ?? entity.IsLocked;
            if (req.EvidenceSegmentId.HasValue)
            {
                entity.EvidenceSegmentId = evSeg?.Id;
                entity.EvidenceSegmentStableId = evSeg?.StableId;
                entity.EvidenceSegmentOrder = evSeg?.Order;
                entity.EvidenceQuote = validation.EvidenceQuote;
            }
            else if (req.ClearEvidence == true)
            {
                entity.EvidenceSegmentId = null;
                entity.EvidenceSegmentStableId = null;
                entity.EvidenceSegmentOrder = null;
                entity.EvidenceQuote = null;
            }
            entity.Version++;
            entity.UpdatedAt = DateTime.UtcNow;

            var conflictResult = await SaveWithConflictHandlingAsync(
                db,
                "Story memory entry conflict or concurrent modification detected.",
                ct);
            if (conflictResult != null) return conflictResult;

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
    }

    private static void MapSceneFactEndpoints(IEndpointRouteBuilder app)
    {
        // GET /api/novels/{novelId:guid}/scene-facts
        app.MapGet("/api/novels/{novelId:guid}/scene-facts", async (
            HttpContext ctx,
            Guid novelId,
            string? targetLanguage,
            string? factType,
            string? status,
            int? startOrder,
            int? endOrder,
            Guid? evidenceSegmentId,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            var (user, err) = await EnsureNovelOwnedAsync(ctx, novelId, db, userManager, ct);
            if (err is not null) return err;

            if (!IsValidOrderRange(startOrder, endOrder, out var rangeErr))
                return Results.BadRequest(new { error = rangeErr });

            var query = db.SceneFacts
                .AsNoTracking()
                .Where(f => f.NovelId == novelId && f.OwnerId == user!.Id);

            if (!string.IsNullOrWhiteSpace(targetLanguage))
            {
                var normLang = targetLanguage.Trim();
                if (!IsValidLanguageTag(normLang))
                    return Results.BadRequest(new { error = "Invalid targetLanguage format." });
                query = query.Where(f => f.TargetLanguage.ToLower() == normLang.ToLower());
            }

            if (!string.IsNullOrWhiteSpace(factType))
            {
                if (!StoryEntityNormalizer.TryParseSceneFactType(factType, out var parsedType))
                    return Results.BadRequest(new { error = $"Invalid factType '{factType}'." });
                query = query.Where(f => f.FactType == parsedType);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!StoryEntityNormalizer.TryParseFactStatus(status, out var parsedStatus))
                    return Results.BadRequest(new { error = $"Invalid status '{status}'." });
                query = query.Where(f => f.Status == parsedStatus);
            }

            if (evidenceSegmentId.HasValue && evidenceSegmentId.Value != Guid.Empty)
            {
                query = query.Where(f => f.EvidenceSegmentId == evidenceSegmentId.Value);
            }

            if (startOrder.HasValue || endOrder.HasValue)
            {
                query = query.Where(f =>
                    f.EvidenceSegmentOrder.HasValue
                    && (!startOrder.HasValue || f.EvidenceSegmentOrder.Value >= startOrder.Value)
                    && (!endOrder.HasValue || f.EvidenceSegmentOrder.Value <= endOrder.Value));
            }

            var facts = await query
                .OrderBy(f => f.EvidenceSegmentOrder ?? int.MaxValue)
                .ThenBy(f => f.FactKey)
                .ToListAsync(ct);

            return Results.Ok(facts.Select(ToDto));
        }).RequireAuthorization();

        // POST /api/novels/{novelId:guid}/scene-facts
        app.MapPost("/api/novels/{novelId:guid}/scene-facts", async (
            HttpContext ctx,
            Guid novelId,
            UpsertSceneFactRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var (user, err) = await EnsureNovelOwnedAsync(ctx, novelId, db, userManager, ct);
            if (err is not null) return err;

            var validation = ValidateSceneFactRequest(req);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var (evSeg, evErr) = await ValidateManualEvidenceAsync(db, novelId, req.EvidenceSegmentId, validation.EvidenceQuote, ct);
            if (evErr != null)
                return Results.BadRequest(new { error = evErr });

            var dup = await db.SceneFacts.AnyAsync(
                f => f.NovelId == novelId
                    && f.TargetLanguage.ToLower() == validation.TargetLanguage.ToLower()
                    && f.NormalizedFactKey == validation.NormalizedFactKey,
                ct);
            if (dup)
                return Results.Conflict(new { error = "A scene fact with this key and target language already exists." });

            var now = DateTime.UtcNow;
            var fact = new SceneFact
            {
                Id = Guid.NewGuid(),
                OwnerId = user!.Id,
                NovelId = novelId,
                TargetLanguage = validation.TargetLanguage,
                FactKey = validation.FactKey,
                NormalizedFactKey = validation.NormalizedFactKey,
                FactType = validation.FactType,
                Summary = validation.Summary,
                Origin = StoryEntityOrigin.Manual,
                Status = validation.Status,
                IsLocked = req.IsLocked ?? true,
                EvidenceSegmentId = evSeg?.Id,
                EvidenceSegmentStableId = evSeg?.StableId,
                EvidenceSegmentOrder = evSeg?.Order,
                EvidenceQuote = validation.EvidenceQuote,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.SceneFacts.Add(fact);
            var conflict = await SaveWithConflictHandlingAsync(db, "A scene fact with this key and target language already exists.", ct);
            if (conflict != null) return conflict;

            return Results.Created($"/api/novels/{novelId}/scene-facts/{fact.Id}", ToDto(fact));
        }).RequireAuthorization();

        // PUT /api/novels/{novelId:guid}/scene-facts/{factId:guid}
        app.MapPut("/api/novels/{novelId:guid}/scene-facts/{factId:guid}", async (
            HttpContext ctx,
            Guid novelId,
            Guid factId,
            UpsertSceneFactRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var fact = await db.SceneFacts
                .FirstOrDefaultAsync(f => f.Id == factId && f.NovelId == novelId && f.OwnerId == user.Id, ct);
            if (fact is null) return Results.NotFound(new { error = "Scene fact not found or access denied." });

            if (req.ExpectedVersion.HasValue && req.ExpectedVersion.Value != fact.Version)
                return Results.Conflict(new { error = "Scene fact was modified concurrently.", currentVersion = fact.Version });

            var validation = ValidateSceneFactRequest(req, fact.Status);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var (evSeg, evErr) = await ValidateManualEvidenceAsync(db, novelId, req.EvidenceSegmentId, validation.EvidenceQuote, ct);
            if (evErr != null)
                return Results.BadRequest(new { error = evErr });

            var dup = await db.SceneFacts.AnyAsync(
                f => f.Id != factId
                    && f.NovelId == novelId
                    && f.TargetLanguage.ToLower() == validation.TargetLanguage.ToLower()
                    && f.NormalizedFactKey == validation.NormalizedFactKey,
                ct);
            if (dup)
                return Results.Conflict(new { error = "Another scene fact with this key and target language already exists." });

            fact.TargetLanguage = validation.TargetLanguage;
            fact.FactKey = validation.FactKey;
            fact.NormalizedFactKey = validation.NormalizedFactKey;
            fact.FactType = validation.FactType;
            fact.Summary = validation.Summary;
            fact.Status = validation.Status;
            if (validation.Status != FactStatus.Conflicted) fact.ConflictDetails = null;
            fact.IsLocked = req.IsLocked ?? fact.IsLocked;
            if (req.EvidenceSegmentId.HasValue)
            {
                fact.EvidenceSegmentId = evSeg?.Id;
                fact.EvidenceSegmentStableId = evSeg?.StableId;
                fact.EvidenceSegmentOrder = evSeg?.Order;
                fact.EvidenceQuote = validation.EvidenceQuote;
            }
            else if (req.ClearEvidence == true)
            {
                fact.EvidenceSegmentId = null;
                fact.EvidenceSegmentStableId = null;
                fact.EvidenceSegmentOrder = null;
                fact.EvidenceQuote = null;
            }
            fact.Version++;
            fact.UpdatedAt = DateTime.UtcNow;

            var conflict = await SaveWithConflictHandlingAsync(db, "Scene fact conflict or concurrent modification detected.", ct);
            if (conflict != null) return conflict;

            return Results.Ok(ToDto(fact));
        }).RequireAuthorization();

        // DELETE /api/novels/{novelId:guid}/scene-facts/{factId:guid}
        app.MapDelete("/api/novels/{novelId:guid}/scene-facts/{factId:guid}", async (
            HttpContext ctx,
            Guid novelId,
            Guid factId,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var fact = await db.SceneFacts
                .FirstOrDefaultAsync(f => f.Id == factId && f.NovelId == novelId && f.OwnerId == user.Id, ct);
            if (fact is null) return Results.NotFound(new { error = "Scene fact not found or access denied." });

            db.SceneFacts.Remove(fact);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { deleted = true, id = factId });
        }).RequireAuthorization();
    }

    private static void MapSpeechObservationEndpoints(IEndpointRouteBuilder app)
    {
        // GET /api/novels/{novelId:guid}/speech-observations
        app.MapGet("/api/novels/{novelId:guid}/speech-observations", async (
            HttpContext ctx,
            Guid novelId,
            string? targetLanguage,
            string? status,
            int? startOrder,
            int? endOrder,
            Guid? evidenceSegmentId,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            var (user, err) = await EnsureNovelOwnedAsync(ctx, novelId, db, userManager, ct);
            if (err is not null) return err;

            if (!IsValidOrderRange(startOrder, endOrder, out var rangeErr))
                return Results.BadRequest(new { error = rangeErr });

            var query = db.SpeechObservations
                .AsNoTracking()
                .Where(o => o.NovelId == novelId && o.OwnerId == user!.Id);

            if (!string.IsNullOrWhiteSpace(targetLanguage))
            {
                var normLang = targetLanguage.Trim();
                if (!IsValidLanguageTag(normLang))
                    return Results.BadRequest(new { error = "Invalid targetLanguage format." });
                query = query.Where(o => o.TargetLanguage.ToLower() == normLang.ToLower());
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!StoryEntityNormalizer.TryParseFactStatus(status, out var parsedStatus))
                    return Results.BadRequest(new { error = $"Invalid status '{status}'." });
                query = query.Where(o => o.Status == parsedStatus);
            }

            if (evidenceSegmentId.HasValue && evidenceSegmentId.Value != Guid.Empty)
            {
                query = query.Where(o => o.EvidenceSegmentId == evidenceSegmentId.Value);
            }

            if (startOrder.HasValue || endOrder.HasValue)
            {
                query = query.Where(o =>
                    o.EvidenceSegmentOrder.HasValue
                    && (!startOrder.HasValue || o.EvidenceSegmentOrder.Value >= startOrder.Value)
                    && (!endOrder.HasValue || o.EvidenceSegmentOrder.Value <= endOrder.Value));
            }

            var observations = await query
                .OrderBy(o => o.SpeakerName)
                .ThenBy(o => o.AddresseeName ?? string.Empty)
                .ToListAsync(ct);

            return Results.Ok(observations.Select(ToDto));
        }).RequireAuthorization();

        // POST /api/novels/{novelId:guid}/speech-observations
        app.MapPost("/api/novels/{novelId:guid}/speech-observations", async (
            HttpContext ctx,
            Guid novelId,
            UpsertSpeechObservationRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var (user, err) = await EnsureNovelOwnedAsync(ctx, novelId, db, userManager, ct);
            if (err is not null) return err;

            var validation = ValidateSpeechRequest(req);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var (evSeg, evErr) = await ValidateManualEvidenceAsync(db, novelId, req.EvidenceSegmentId, validation.EvidenceQuote, ct);
            if (evErr != null)
                return Results.BadRequest(new { error = evErr });

            var dup = await db.SpeechObservations.AnyAsync(
                o => o.NovelId == novelId
                    && o.TargetLanguage.ToLower() == validation.TargetLanguage.ToLower()
                    && o.NormalizedSpeakerName == validation.NormalizedSpeakerName
                    && o.NormalizedAddresseeName == validation.NormalizedAddresseeName,
                ct);
            if (dup)
                return Results.Conflict(new { error = "A speech observation for this speaker/addressee pair already exists." });

            var now = DateTime.UtcNow;
            var obs = new SpeechObservation
            {
                Id = Guid.NewGuid(),
                OwnerId = user!.Id,
                NovelId = novelId,
                TargetLanguage = validation.TargetLanguage,
                SpeakerName = validation.SpeakerName,
                NormalizedSpeakerName = validation.NormalizedSpeakerName,
                AddresseeName = validation.AddresseeName,
                NormalizedAddresseeName = validation.NormalizedAddresseeName,
                SpeechRegister = validation.SpeechRegister,
                PronounStyle = validation.PronounStyle,
                Observation = validation.Observation,
                Origin = StoryEntityOrigin.Manual,
                Status = validation.Status,
                IsLocked = req.IsLocked ?? true,
                EvidenceSegmentId = evSeg?.Id,
                EvidenceSegmentStableId = evSeg?.StableId,
                EvidenceSegmentOrder = evSeg?.Order,
                EvidenceQuote = validation.EvidenceQuote,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.SpeechObservations.Add(obs);
            var conflict = await SaveWithConflictHandlingAsync(db, "A speech observation for this speaker/addressee pair already exists.", ct);
            if (conflict != null) return conflict;

            return Results.Created($"/api/novels/{novelId}/speech-observations/{obs.Id}", ToDto(obs));
        }).RequireAuthorization();

        // PUT /api/novels/{novelId:guid}/speech-observations/{obsId:guid}
        app.MapPut("/api/novels/{novelId:guid}/speech-observations/{obsId:guid}", async (
            HttpContext ctx,
            Guid novelId,
            Guid obsId,
            UpsertSpeechObservationRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var obs = await db.SpeechObservations
                .FirstOrDefaultAsync(o => o.Id == obsId && o.NovelId == novelId && o.OwnerId == user.Id, ct);
            if (obs is null) return Results.NotFound(new { error = "Speech observation not found or access denied." });

            if (req.ExpectedVersion.HasValue && req.ExpectedVersion.Value != obs.Version)
                return Results.Conflict(new { error = "Speech observation was modified concurrently.", currentVersion = obs.Version });

            var validation = ValidateSpeechRequest(req, obs.Status);
            if (validation.Error != null)
                return Results.BadRequest(new { error = validation.Error });

            var (evSeg, evErr) = await ValidateManualEvidenceAsync(db, novelId, req.EvidenceSegmentId, validation.EvidenceQuote, ct);
            if (evErr != null)
                return Results.BadRequest(new { error = evErr });

            var dup = await db.SpeechObservations.AnyAsync(
                o => o.Id != obsId
                    && o.NovelId == novelId
                    && o.TargetLanguage.ToLower() == validation.TargetLanguage.ToLower()
                    && o.NormalizedSpeakerName == validation.NormalizedSpeakerName
                    && o.NormalizedAddresseeName == validation.NormalizedAddresseeName,
                ct);
            if (dup)
                return Results.Conflict(new { error = "Another speech observation for this speaker/addressee pair already exists." });

            obs.TargetLanguage = validation.TargetLanguage;
            obs.SpeakerName = validation.SpeakerName;
            obs.NormalizedSpeakerName = validation.NormalizedSpeakerName;
            obs.AddresseeName = validation.AddresseeName;
            obs.NormalizedAddresseeName = validation.NormalizedAddresseeName;
            obs.SpeechRegister = validation.SpeechRegister;
            obs.PronounStyle = validation.PronounStyle;
            obs.Observation = validation.Observation;
            obs.Status = validation.Status;
            if (validation.Status != FactStatus.Conflicted) obs.ConflictDetails = null;
            obs.IsLocked = req.IsLocked ?? obs.IsLocked;
            if (req.EvidenceSegmentId.HasValue)
            {
                obs.EvidenceSegmentId = evSeg?.Id;
                obs.EvidenceSegmentStableId = evSeg?.StableId;
                obs.EvidenceSegmentOrder = evSeg?.Order;
                obs.EvidenceQuote = validation.EvidenceQuote;
            }
            else if (req.ClearEvidence == true)
            {
                obs.EvidenceSegmentId = null;
                obs.EvidenceSegmentStableId = null;
                obs.EvidenceSegmentOrder = null;
                obs.EvidenceQuote = null;
            }
            obs.Version++;
            obs.UpdatedAt = DateTime.UtcNow;

            var conflict = await SaveWithConflictHandlingAsync(db, "Speech observation conflict or concurrent modification detected.", ct);
            if (conflict != null) return conflict;

            return Results.Ok(ToDto(obs));
        }).RequireAuthorization();

        // DELETE /api/novels/{novelId:guid}/speech-observations/{obsId:guid}
        app.MapDelete("/api/novels/{novelId:guid}/speech-observations/{obsId:guid}", async (
            HttpContext ctx,
            Guid novelId,
            Guid obsId,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery))
                return Results.BadRequest(new { error = "Invalid antiforgery token." });

            var user = await ResolveOwnerAsync(ctx, userManager);
            if (user is null) return Results.Unauthorized();

            var obs = await db.SpeechObservations
                .FirstOrDefaultAsync(o => o.Id == obsId && o.NovelId == novelId && o.OwnerId == user.Id, ct);
            if (obs is null) return Results.NotFound(new { error = "Speech observation not found or access denied." });

            db.SpeechObservations.Remove(obs);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { deleted = true, id = obsId });
        }).RequireAuthorization();
    }

    private static void MapExtractionAndConsistencyEndpoints(IEndpointRouteBuilder app)
    {
        // POST /api/novels/{novelId:guid}/story-memory/extract
        app.MapPost("/api/novels/{novelId:guid}/story-memory/extract", async (
            HttpContext ctx,
            Guid novelId,
            ExtractStoryMemoryRequest req,
            BasiDbContext db,
            UserManager<ApplicationUser> userManager,
            IAiProviderFactory providerFactory,
            IStoryMemoryExtractor extractor,
            StoryMemoryMergeGate mergeGate,
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
                .AsNoTracking()
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

            List<SourceSegmentInput> window;
            if (req.StartOrder.HasValue && req.EndOrder.HasValue)
            {
                if (orderedSegments.Count > StoryMemoryExtractor.MaxExtractionSegments)
                    return Results.BadRequest(new { error = $"Explicit extraction range exceeds maximum {StoryMemoryExtractor.MaxExtractionSegments} segments per request." });

                var totalExplicitChars = orderedSegments.Sum(s => s.Content.Length);
                if (totalExplicitChars > StoryMemoryExtractor.MaxExtractionChars)
                    return Results.BadRequest(new { error = $"Explicit extraction range exceeds maximum {StoryMemoryExtractor.MaxExtractionChars} characters per request." });

                window = orderedSegments.Select(s => new SourceSegmentInput(s.Id, s.StableId, s.Order, s.Content)).ToList();
            }
            else
            {
                window = new List<SourceSegmentInput>();
                var accChars = 0;
                foreach (var seg in orderedSegments)
                {
                    if (window.Count >= StoryMemoryExtractor.MaxExtractionSegments) break;
                    if (accChars + seg.Content.Length > StoryMemoryExtractor.MaxExtractionChars) break;
                    window.Add(new SourceSegmentInput(seg.Id, seg.StableId, seg.Order, seg.Content));
                    accChars += seg.Content.Length;
                }
                if (window.Count == 0)
                    return Results.BadRequest(new { error = $"First segment exceeds maximum single extraction character budget ({StoryMemoryExtractor.MaxExtractionChars})." });
            }

            IAiProvider provider;
            try
            {
                provider = !string.IsNullOrWhiteSpace(req.ApiKey)
                    ? providerFactory.CreateProvider(normProvider, req.ApiKey.Trim())
                    : providerFactory.GetProviderForUser(user.Id, normProvider);
            }
            catch (AiProviderError ex) when (ex.ErrorCode == AiErrorCode.InvalidApiKey)
            {
                return Results.BadRequest(new { error = $"API key missing or invalid for provider '{normProvider}'. Provide an ephemeral key or save one in your RAM vault." });
            }

            // B004-03: Read hints WITHOUT tracking across slow AI I/O
            var hintEntities = await db.StoryEntities
                .AsNoTracking()
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
                    hintEntities,
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

            // B004-03: Acquire short per-(novelId, targetLang) merge gate + retry loop + fresh DB re-read + Version token
            MergeCounts counts = default;
            List<object> sortedGlossary = new();
            List<object> sortedSceneFacts = new();
            List<object> sortedSpeechObs = new();

            using (await mergeGate.AcquireAsync(novelId, targetLang, ct))
            {
                var committed = false;
                for (var attempt = 0; attempt < MaxMergeRetryAttempts && !committed; attempt++)
                {
                    db.ChangeTracker.Clear();
                    var tx = db.Database.IsRelational()
                        ? await db.Database.BeginTransactionAsync(ct)
                        : null;

                    try
                    {
                        var now = DateTime.UtcNow;
                        var existingEntities = await db.StoryEntities
                            .Where(e => e.NovelId == novelId && e.OwnerId == user.Id && e.TargetLanguage.ToLower() == targetLang.ToLower())
                            .ToListAsync(ct);
                        var existingFacts = await db.SceneFacts
                            .Where(f => f.NovelId == novelId && f.OwnerId == user.Id && f.TargetLanguage.ToLower() == targetLang.ToLower())
                            .ToListAsync(ct);
                        var existingSpeech = await db.SpeechObservations
                            .Where(o => o.NovelId == novelId && o.OwnerId == user.Id && o.TargetLanguage.ToLower() == targetLang.ToLower())
                            .ToListAsync(ct);

                        counts = MergeExtractedResults(
                            db,
                            user.Id,
                            novelId,
                            targetLang,
                            now,
                            extraction,
                            existingEntities,
                            existingFacts,
                            existingSpeech);

                        await db.SaveChangesAsync(ct);
                        if (tx != null) await tx.CommitAsync(ct);

                        sortedGlossary = existingEntities
                            .OrderBy(e => e.Category)
                            .ThenBy(e => e.EvidenceSegmentOrder ?? int.MaxValue)
                            .ThenBy(e => e.SourceTerm)
                            .Select(ToDto)
                            .ToList();
                        sortedSceneFacts = existingFacts
                            .OrderBy(f => f.EvidenceSegmentOrder ?? int.MaxValue)
                            .ThenBy(f => f.FactKey)
                            .Select(ToDto)
                            .ToList();
                        sortedSpeechObs = existingSpeech
                            .OrderBy(o => o.SpeakerName)
                            .ThenBy(o => o.AddresseeName ?? string.Empty)
                            .Select(ToDto)
                            .ToList();
                        committed = true;
                    }
                    catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || IsUniqueConstraintViolation(ex))
                    {
                        if (tx != null) await tx.RollbackAsync(ct);
                        db.ChangeTracker.Clear();
                        if (attempt == MaxMergeRetryAttempts - 1)
                        {
                            return Results.Conflict(new { error = "Concurrent modification prevented saving story memory extraction. Please retry." });
                        }
                    }
                    finally
                    {
                        if (tx != null) await tx.DisposeAsync();
                    }
                }
            }

            return Results.Ok(new
            {
                created = counts.Created,
                updated = counts.Updated,
                skippedLocked = counts.SkippedLocked,
                conflicted = counts.Conflicted,
                extractedCount = extraction.Entities.Count + extraction.SceneFacts.Count + extraction.SpeechObservations.Count,
                promptTokens = extraction.PromptTokens,
                completionTokens = extraction.CompletionTokens,
                totalTokens = extraction.TotalTokens,
                modelUsed = extraction.ModelUsed,
                glossary = sortedGlossary,
                sceneFacts = sortedSceneFacts,
                speechObservations = sortedSpeechObs
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
    }

    private readonly record struct MergeCounts(int Created, int Updated, int SkippedLocked, int Conflicted);

    private static MergeCounts MergeExtractedResults(
        BasiDbContext db,
        Guid ownerId,
        Guid novelId,
        string targetLang,
        DateTime now,
        StoryMemoryExtractionResult extraction,
        List<StoryEntity> existingEntities,
        List<SceneFact> existingFacts,
        List<SpeechObservation> existingSpeech)
    {
        int created = 0, updated = 0, skippedLocked = 0, conflicted = 0;

        var entityByKey = existingEntities.ToDictionary(e => e.NormalizedSourceTerm, StringComparer.Ordinal);
        foreach (var cand in extraction.Entities)
        {
            if (entityByKey.TryGetValue(cand.NormalizedSourceTerm, out var existing))
            {
                if (IsProtectedFromAiOverwrite(existing.IsLocked, existing.Origin, existing.Status))
                {
                    skippedLocked++;
                    continue;
                }

                var translatedKey = StoryEntityNormalizer.NormalizeKey(existing.TranslatedTerm);
                var candTranslatedKey = StoryEntityNormalizer.NormalizeKey(cand.TranslatedTerm);
                if (!StoryEntityNormalizer.TryValidateAliases(existing.SourceTerm, existing.Aliases.Concat(cand.Aliases), out var mergedAliases, out _))
                {
                    mergedAliases = existing.Aliases;
                }

                if (translatedKey != candTranslatedKey)
                {
                    existing.Status = FactStatus.Conflicted;
                    existing.ConflictDetails = StoryEntityNormalizer.BuildConflictDetails(
                        "translation",
                        cand.TranslatedTerm,
                        existing.TranslatedTerm,
                        cand.EvidenceSegmentOrder);
                    existing.Aliases = mergedAliases;
                    existing.Version++;
                    existing.UpdatedAt = now;
                    conflicted++;
                    updated++;
                }
                else
                {
                    existing.Category = cand.Category;
                    existing.Aliases = mergedAliases;
                    existing.Description ??= cand.Description;
                    if (ShouldApplyEvidenceBundle(existing.EvidenceSegmentId, existing.EvidenceQuote, cand.EvidenceQuote))
                    {
                        existing.EvidenceSegmentId = cand.EvidenceSegmentId;
                        existing.EvidenceSegmentStableId = cand.EvidenceSegmentStableId;
                        existing.EvidenceSegmentOrder = cand.EvidenceSegmentOrder;
                        existing.EvidenceQuote = cand.EvidenceQuote;
                        if (existing.Origin == StoryEntityOrigin.AiExtracted && existing.Status == FactStatus.Unknown)
                        {
                            existing.Status = FactStatus.Provisional;
                        }
                    }
                    existing.Version++;
                    existing.UpdatedAt = now;
                    updated++;
                }
            }
            else
            {
                var entity = new StoryEntity
                {
                    Id = Guid.NewGuid(),
                    OwnerId = ownerId,
                    NovelId = novelId,
                    TargetLanguage = targetLang,
                    SourceTerm = cand.SourceTerm,
                    NormalizedSourceTerm = cand.NormalizedSourceTerm,
                    TranslatedTerm = cand.TranslatedTerm,
                    Category = cand.Category,
                    Aliases = cand.Aliases,
                    Description = cand.Description,
                    Origin = StoryEntityOrigin.AiExtracted,
                    Status = FactStatus.Provisional, // Never auto-Confirmed
                    IsLocked = false,
                    EvidenceSegmentId = cand.EvidenceSegmentId,
                    EvidenceSegmentStableId = cand.EvidenceSegmentStableId,
                    EvidenceSegmentOrder = cand.EvidenceSegmentOrder,
                    EvidenceQuote = cand.EvidenceQuote,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.StoryEntities.Add(entity);
                entityByKey[cand.NormalizedSourceTerm] = entity;
                existingEntities.Add(entity);
                created++;
            }
        }

        var factByKey = existingFacts.ToDictionary(f => f.NormalizedFactKey, StringComparer.Ordinal);
        foreach (var cand in extraction.SceneFacts)
        {
            if (factByKey.TryGetValue(cand.NormalizedFactKey, out var existing))
            {
                if (IsProtectedFromAiOverwrite(existing.IsLocked, existing.Origin, existing.Status))
                {
                    skippedLocked++;
                    continue;
                }

                if (existing.FactType != cand.FactType ||
                    StoryEntityNormalizer.NormalizeKey(existing.Summary) != StoryEntityNormalizer.NormalizeKey(cand.Summary))
                {
                    existing.Status = FactStatus.Conflicted;
                    existing.ConflictDetails = StoryEntityNormalizer.BuildConflictDetails(
                        "scene fact",
                        cand.Summary,
                        existing.Summary,
                        cand.EvidenceSegmentOrder);
                    existing.Version++;
                    existing.UpdatedAt = now;
                    conflicted++;
                    updated++;
                }
                else
                {
                    if (ShouldApplyEvidenceBundle(existing.EvidenceSegmentId, existing.EvidenceQuote, cand.EvidenceQuote))
                    {
                        existing.EvidenceSegmentId = cand.EvidenceSegmentId;
                        existing.EvidenceSegmentStableId = cand.EvidenceSegmentStableId;
                        existing.EvidenceSegmentOrder = cand.EvidenceSegmentOrder;
                        existing.EvidenceQuote = cand.EvidenceQuote;
                        if (existing.Origin == StoryEntityOrigin.AiExtracted && existing.Status == FactStatus.Unknown)
                        {
                            existing.Status = FactStatus.Provisional;
                        }
                    }
                    existing.Version++;
                    existing.UpdatedAt = now;
                    updated++;
                }
            }
            else
            {
                var fact = new SceneFact
                {
                    Id = Guid.NewGuid(),
                    OwnerId = ownerId,
                    NovelId = novelId,
                    TargetLanguage = targetLang,
                    FactKey = cand.FactKey,
                    NormalizedFactKey = cand.NormalizedFactKey,
                    FactType = cand.FactType,
                    Summary = cand.Summary,
                    Origin = StoryEntityOrigin.AiExtracted,
                    Status = FactStatus.Provisional,
                    IsLocked = false,
                    EvidenceSegmentId = cand.EvidenceSegmentId,
                    EvidenceSegmentStableId = cand.EvidenceSegmentStableId,
                    EvidenceSegmentOrder = cand.EvidenceSegmentOrder,
                    EvidenceQuote = cand.EvidenceQuote,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.SceneFacts.Add(fact);
                factByKey[cand.NormalizedFactKey] = fact;
                existingFacts.Add(fact);
                created++;
            }
        }

        var speechByKey = existingSpeech.ToDictionary(o => (o.NormalizedSpeakerName, o.NormalizedAddresseeName));
        foreach (var cand in extraction.SpeechObservations)
        {
            var key = (cand.NormalizedSpeakerName, cand.NormalizedAddresseeName);
            if (speechByKey.TryGetValue(key, out var existing))
            {
                if (IsProtectedFromAiOverwrite(existing.IsLocked, existing.Origin, existing.Status))
                {
                    skippedLocked++;
                    continue;
                }

                var hasPronounConflict = !string.IsNullOrEmpty(existing.PronounStyle)
                    && !string.IsNullOrEmpty(cand.PronounStyle)
                    && StoryEntityNormalizer.NormalizeKey(existing.PronounStyle) != StoryEntityNormalizer.NormalizeKey(cand.PronounStyle);
                if (StoryEntityNormalizer.NormalizeKey(existing.SpeechRegister) != StoryEntityNormalizer.NormalizeKey(cand.SpeechRegister)
                    || StoryEntityNormalizer.NormalizeKey(existing.Observation) != StoryEntityNormalizer.NormalizeKey(cand.Observation)
                    || hasPronounConflict)
                {
                    existing.Status = FactStatus.Conflicted;
                    existing.ConflictDetails = StoryEntityNormalizer.BuildConflictDetails(
                        "speech observation",
                        $"[{cand.SpeechRegister}] {cand.Observation}",
                        $"[{existing.SpeechRegister}] {existing.Observation}",
                        cand.EvidenceSegmentOrder);
                    existing.Version++;
                    existing.UpdatedAt = now;
                    conflicted++;
                    updated++;
                }
                else
                {
                    existing.PronounStyle ??= cand.PronounStyle;
                    if (ShouldApplyEvidenceBundle(existing.EvidenceSegmentId, existing.EvidenceQuote, cand.EvidenceQuote))
                    {
                        existing.EvidenceSegmentId = cand.EvidenceSegmentId;
                        existing.EvidenceSegmentStableId = cand.EvidenceSegmentStableId;
                        existing.EvidenceSegmentOrder = cand.EvidenceSegmentOrder;
                        existing.EvidenceQuote = cand.EvidenceQuote;
                        if (existing.Origin == StoryEntityOrigin.AiExtracted && existing.Status == FactStatus.Unknown)
                        {
                            existing.Status = FactStatus.Provisional;
                        }
                    }
                    existing.Version++;
                    existing.UpdatedAt = now;
                    updated++;
                }
            }
            else
            {
                var obs = new SpeechObservation
                {
                    Id = Guid.NewGuid(),
                    OwnerId = ownerId,
                    NovelId = novelId,
                    TargetLanguage = targetLang,
                    SpeakerName = cand.SpeakerName,
                    NormalizedSpeakerName = cand.NormalizedSpeakerName,
                    AddresseeName = cand.AddresseeName,
                    NormalizedAddresseeName = cand.NormalizedAddresseeName,
                    SpeechRegister = cand.SpeechRegister,
                    PronounStyle = cand.PronounStyle,
                    Observation = cand.Observation,
                    Origin = StoryEntityOrigin.AiExtracted,
                    Status = FactStatus.Provisional,
                    IsLocked = false,
                    EvidenceSegmentId = cand.EvidenceSegmentId,
                    EvidenceSegmentStableId = cand.EvidenceSegmentStableId,
                    EvidenceSegmentOrder = cand.EvidenceSegmentOrder,
                    EvidenceQuote = cand.EvidenceQuote,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.SpeechObservations.Add(obs);
                speechByKey[key] = obs;
                existingSpeech.Add(obs);
                created++;
            }
        }

        return new MergeCounts(created, updated, skippedLocked, conflicted);
    }

    private static bool ShouldApplyEvidenceBundle(Guid? existingSegId, string? existingQuote, string? candQuote) =>
        !existingSegId.HasValue || (string.IsNullOrWhiteSpace(existingQuote) && !string.IsNullOrWhiteSpace(candQuote));

    private static bool IsValidOrderRange(int? startOrder, int? endOrder, out string? error)
    {
        if (startOrder < 0 || endOrder < 0 || (startOrder.HasValue && endOrder.HasValue && endOrder < startOrder))
        {
            error = "Invalid segment order range.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool IsProtectedFromAiOverwrite(bool isLocked, StoryEntityOrigin origin, FactStatus status) =>
        isLocked || origin == StoryEntityOrigin.Manual || status == FactStatus.Confirmed;

    private static async Task<(ApplicationUser? User, IResult? ErrorResult)> EnsureNovelOwnedAsync(
        HttpContext ctx,
        Guid novelId,
        BasiDbContext db,
        UserManager<ApplicationUser> userManager,
        CancellationToken ct)
    {
        var user = await ResolveOwnerAsync(ctx, userManager);
        if (user is null) return (null, Results.Unauthorized());

        var novelOwned = await db.Novels.AnyAsync(n => n.Id == novelId && n.OwnerId == user.Id, ct);
        if (!novelOwned) return (null, Results.NotFound(new { error = "Novel not found or access denied." }));

        return (user, null);
    }

    private static async Task<(Segment? Segment, string? Error)> ValidateManualEvidenceAsync(
        BasiDbContext db,
        Guid novelId,
        Guid? evidenceSegmentId,
        string? evidenceQuote,
        CancellationToken ct)
    {
        if (!evidenceSegmentId.HasValue || evidenceSegmentId.Value == Guid.Empty)
        {
            return string.IsNullOrWhiteSpace(evidenceQuote)
                ? (null, null)
                : (null, "EvidenceSegmentId is required when EvidenceQuote is provided.");
        }

        var segment = await db.Segments
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == evidenceSegmentId.Value && s.NovelId == novelId, ct);
        if (segment is null)
        {
            return (null, "EvidenceSegmentId does not exist or does not belong to this novel.");
        }

        if (!string.IsNullOrWhiteSpace(evidenceQuote) && !StoryEntityNormalizer.MatchesNormalizedQuote(segment.Content, evidenceQuote))
        {
            return (null, "EvidenceQuote does not match the referenced segment content.");
        }

        return (segment, null);
    }

    private static async Task<IResult?> SaveWithConflictHandlingAsync(BasiDbContext db, string conflictMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || IsUniqueConstraintViolation(ex))
        {
            return Results.Conflict(new { error = conflictMessage });
        }
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

    private static EntityValidationResult ValidateEntityRequest(UpsertStoryEntityRequest req, FactStatus? defaultStatus = null)
    {
        if (string.IsNullOrWhiteSpace(req.TargetLanguage) || !IsValidLanguageTag(req.TargetLanguage.Trim()))
            return EntityValidationResult.Fail("Invalid TargetLanguage format. Must be a valid BCP-47 tag (e.g. vi, en, ja).");

        if (!StoryEntityNormalizer.TryValidateTerm(req.SourceTerm, "SourceTerm", out var sourceTerm, out var normSourceKey, out var srcErr))
            return EntityValidationResult.Fail(srcErr!);

        if (!StoryEntityNormalizer.TryValidateTerm(req.TranslatedTerm, "TranslatedTerm", out var translatedTerm, out _, out var transErr))
            return EntityValidationResult.Fail(transErr!);

        if (!StoryEntityNormalizer.TryParseCategory(req.Category, out var category))
            return EntityValidationResult.Fail($"Invalid Category '{req.Category}'.");

        if (!StoryEntityNormalizer.TryValidateAliases(sourceTerm, req.Aliases, out var aliases, out var aliasErr))
            return EntityValidationResult.Fail(aliasErr!);

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.Description, "Description", StoryEntityNormalizer.MaxDescriptionLength, out var description, out var descErr))
            return EntityValidationResult.Fail(descErr!);

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.EvidenceQuote, "EvidenceQuote", StoryEntityNormalizer.MaxEvidenceQuoteLength, out var quote, out var qErr))
            return EntityValidationResult.Fail(qErr!);

        FactStatus status;
        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!StoryEntityNormalizer.TryParseFactStatus(req.Status, out status))
                return EntityValidationResult.Fail($"Invalid Status '{req.Status}'.");
        }
        else
        {
            status = defaultStatus ?? FactStatus.Provisional;
        }

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.ConflictDetails, "ConflictDetails", StoryEntityNormalizer.MaxDescriptionLength, out var conflictDetails, out var cErr))
            return EntityValidationResult.Fail(cErr!);

        return new EntityValidationResult(
            null,
            req.TargetLanguage.Trim(),
            sourceTerm,
            normSourceKey,
            translatedTerm,
            category,
            aliases,
            description,
            status,
            quote,
            conflictDetails);
    }

    private static SceneFactValidationResult ValidateSceneFactRequest(UpsertSceneFactRequest req, FactStatus? defaultStatus = null)
    {
        if (string.IsNullOrWhiteSpace(req.TargetLanguage) || !IsValidLanguageTag(req.TargetLanguage.Trim()))
            return SceneFactValidationResult.Fail("Invalid TargetLanguage format.");

        if (!StoryEntityNormalizer.TryValidateTerm(req.FactKey, "FactKey", out var factKey, out var normFactKey, out var kErr))
            return SceneFactValidationResult.Fail(kErr!);

        if (!StoryEntityNormalizer.TryParseSceneFactType(req.FactType, out var factType))
            return SceneFactValidationResult.Fail($"Invalid FactType '{req.FactType}'.");

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.Summary, "Summary", StoryEntityNormalizer.MaxDescriptionLength, out var summary, out var sErr) || string.IsNullOrEmpty(summary))
            return SceneFactValidationResult.Fail(sErr ?? "Summary is required.");

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.EvidenceQuote, "EvidenceQuote", StoryEntityNormalizer.MaxEvidenceQuoteLength, out var quote, out var qErr))
            return SceneFactValidationResult.Fail(qErr!);

        FactStatus status;
        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!StoryEntityNormalizer.TryParseFactStatus(req.Status, out status))
                return SceneFactValidationResult.Fail($"Invalid Status '{req.Status}'.");
        }
        else
        {
            status = defaultStatus ?? FactStatus.Provisional;
        }

        return new SceneFactValidationResult(null, req.TargetLanguage.Trim(), factKey, normFactKey, factType, summary, status, quote);
    }

    private static SpeechValidationResult ValidateSpeechRequest(UpsertSpeechObservationRequest req, FactStatus? defaultStatus = null)
    {
        if (string.IsNullOrWhiteSpace(req.TargetLanguage) || !IsValidLanguageTag(req.TargetLanguage.Trim()))
            return SpeechValidationResult.Fail("Invalid TargetLanguage format.");

        if (!StoryEntityNormalizer.TryValidateTerm(req.SpeakerName, "SpeakerName", out var speakerName, out var normSpeaker, out var spErr))
            return SpeechValidationResult.Fail(spErr!);

        string? addresseeName = null;
        var normAddressee = string.Empty;
        if (!string.IsNullOrWhiteSpace(req.AddresseeName))
        {
            if (!StoryEntityNormalizer.TryValidateTerm(req.AddresseeName, "AddresseeName", out var validAddressee, out var validNormAddr, out var addrErr))
                return SpeechValidationResult.Fail(addrErr!);
            addresseeName = validAddressee;
            normAddressee = validNormAddr;
        }

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.SpeechRegister, "SpeechRegister", StoryEntityNormalizer.MaxSpeechRegisterLength, out var register, out var regErr) || string.IsNullOrEmpty(register))
            return SpeechValidationResult.Fail(regErr ?? "SpeechRegister is required.");

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.PronounStyle, "PronounStyle", StoryEntityNormalizer.MaxTermLength, out var pronounStyle, out var proErr))
            return SpeechValidationResult.Fail(proErr!);

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.Observation, "Observation", StoryEntityNormalizer.MaxDescriptionLength, out var observation, out var obsErr) || string.IsNullOrEmpty(observation))
            return SpeechValidationResult.Fail(obsErr ?? "Observation is required.");

        if (!StoryEntityNormalizer.TryValidateOptionalText(req.EvidenceQuote, "EvidenceQuote", StoryEntityNormalizer.MaxEvidenceQuoteLength, out var quote, out var qErr))
            return SpeechValidationResult.Fail(qErr!);

        FactStatus status;
        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!StoryEntityNormalizer.TryParseFactStatus(req.Status, out status))
                return SpeechValidationResult.Fail($"Invalid Status '{req.Status}'.");
        }
        else
        {
            status = defaultStatus ?? FactStatus.Provisional;
        }

        return new SpeechValidationResult(null, req.TargetLanguage.Trim(), speakerName, normSpeaker, addresseeName, normAddressee, register, pronounStyle, observation, status, quote);
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
        status = e.Status.ToString(),
        conflictDetails = e.ConflictDetails,
        isLocked = e.IsLocked,
        evidenceSegmentId = e.EvidenceSegmentId,
        evidenceSegmentStableId = e.EvidenceSegmentStableId,
        evidenceSegmentOrder = e.EvidenceSegmentOrder,
        evidenceQuote = e.EvidenceQuote,
        version = e.Version,
        createdAt = e.CreatedAt,
        updatedAt = e.UpdatedAt
    };

    private static object ToDto(SceneFact f) => new
    {
        id = f.Id,
        novelId = f.NovelId,
        targetLanguage = f.TargetLanguage,
        factKey = f.FactKey,
        factType = f.FactType.ToString(),
        summary = f.Summary,
        origin = f.Origin.ToString(),
        status = f.Status.ToString(),
        conflictDetails = f.ConflictDetails,
        isLocked = f.IsLocked,
        evidenceSegmentId = f.EvidenceSegmentId,
        evidenceSegmentStableId = f.EvidenceSegmentStableId,
        evidenceSegmentOrder = f.EvidenceSegmentOrder,
        evidenceQuote = f.EvidenceQuote,
        version = f.Version,
        createdAt = f.CreatedAt,
        updatedAt = f.UpdatedAt
    };

    private static object ToDto(SpeechObservation o) => new
    {
        id = o.Id,
        novelId = o.NovelId,
        targetLanguage = o.TargetLanguage,
        speakerName = o.SpeakerName,
        addresseeName = o.AddresseeName,
        speechRegister = o.SpeechRegister,
        pronounStyle = o.PronounStyle,
        observation = o.Observation,
        origin = o.Origin.ToString(),
        status = o.Status.ToString(),
        conflictDetails = o.ConflictDetails,
        isLocked = o.IsLocked,
        evidenceSegmentId = o.EvidenceSegmentId,
        evidenceSegmentStableId = o.EvidenceSegmentStableId,
        evidenceSegmentOrder = o.EvidenceSegmentOrder,
        evidenceQuote = o.EvidenceQuote,
        version = o.Version,
        createdAt = o.CreatedAt,
        updatedAt = o.UpdatedAt
    };

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static bool IsValidLanguageTag(string tag) => tag.Length <= 32 && LanguageTagRegex().IsMatch(tag);

    private static string SanitizeExtractionError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Story memory extraction failed.";
        if (raw.Contains("truncated", StringComparison.OrdinalIgnoreCase)) return "Extraction output was truncated due to token limit.";
        if (raw.Contains("content filter", StringComparison.OrdinalIgnoreCase) || raw.Contains("safety", StringComparison.OrdinalIgnoreCase)) return "Extraction was blocked by safety policy.";
        if (raw.Contains("schema", StringComparison.OrdinalIgnoreCase) || raw.Contains("evidence", StringComparison.OrdinalIgnoreCase) || raw.Contains("JSON", StringComparison.OrdinalIgnoreCase))
            return "Model returned invalid story memory schema or ungrounded evidence.";
        if (raw.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return "API rate limit exceeded. Please retry later.";
        return "Story memory extraction failed. Please retry.";
    }

    [GeneratedRegex(@"^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$")]
    private static partial Regex LanguageTagRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.\/:]+$")]
    private static partial Regex ModelNameRegex();

    private sealed record EntityValidationResult(
        string? Error,
        string TargetLanguage,
        string SourceTerm,
        string NormalizedSourceKey,
        string TranslatedTerm,
        StoryEntityCategory Category,
        List<string> Aliases,
        string? Description,
        FactStatus Status,
        string? EvidenceQuote,
        string? ConflictDetails)
    {
        public static EntityValidationResult Fail(string error) =>
            new(error, string.Empty, string.Empty, string.Empty, string.Empty, StoryEntityCategory.Other, new List<string>(), null, FactStatus.Provisional, null, null);
    }

    private sealed record SceneFactValidationResult(
        string? Error,
        string TargetLanguage,
        string FactKey,
        string NormalizedFactKey,
        SceneFactType FactType,
        string Summary,
        FactStatus Status,
        string? EvidenceQuote)
    {
        public static SceneFactValidationResult Fail(string error) =>
            new(error, string.Empty, string.Empty, string.Empty, SceneFactType.Other, string.Empty, FactStatus.Provisional, null);
    }

    private sealed record SpeechValidationResult(
        string? Error,
        string TargetLanguage,
        string SpeakerName,
        string NormalizedSpeakerName,
        string? AddresseeName,
        string NormalizedAddresseeName,
        string SpeechRegister,
        string? PronounStyle,
        string Observation,
        FactStatus Status,
        string? EvidenceQuote)
    {
        public static SpeechValidationResult Fail(string error) =>
            new(error, string.Empty, string.Empty, string.Empty, null, string.Empty, string.Empty, null, string.Empty, FactStatus.Provisional, null);
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
    string? Status = null,
    Guid? EvidenceSegmentId = null,
    string? EvidenceQuote = null,
    string? ConflictDetails = null,
    int? ExpectedVersion = null,
    bool? ClearEvidence = null);

public sealed record UpsertSceneFactRequest(
    string TargetLanguage,
    string FactKey,
    string Summary,
    string? FactType = null,
    bool? IsLocked = null,
    string? Status = null,
    Guid? EvidenceSegmentId = null,
    string? EvidenceQuote = null,
    int? ExpectedVersion = null,
    bool? ClearEvidence = null);

public sealed record UpsertSpeechObservationRequest(
    string TargetLanguage,
    string SpeakerName,
    string SpeechRegister,
    string Observation,
    string? AddresseeName = null,
    string? PronounStyle = null,
    bool? IsLocked = null,
    string? Status = null,
    Guid? EvidenceSegmentId = null,
    string? EvidenceQuote = null,
    int? ExpectedVersion = null,
    bool? ClearEvidence = null);

public sealed record ExtractStoryMemoryRequest(
    string TargetLanguage,
    string Provider,
    string? Model = null,
    int? StartOrder = null,
    int? EndOrder = null,
    string? ApiKey = null);
