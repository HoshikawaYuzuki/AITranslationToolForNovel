namespace Basi.Core.Models;

/// <summary>
/// Canonical story memory / glossary entity scoped to a novel, owner, target language, and verified segment evidence.
/// </summary>
public sealed class StoryEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Owner ID referencing ApplicationUser.Id.</summary>
    public Guid OwnerId { get; init; }

    /// <summary>Novel ID referencing Novel.Id.</summary>
    public Guid NovelId { get; init; }

    /// <summary>BCP-47 target language tag (e.g. "vi", "en").</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>Canonical term in source language.</summary>
    public string SourceTerm { get; set; } = string.Empty;

    /// <summary>NFKC-normalized lowercase key used for deduplication and matching (max 256).</summary>
    public string NormalizedSourceTerm { get; set; } = string.Empty;

    /// <summary>Preferred canonical translation in the target language.</summary>
    public string TranslatedTerm { get; set; } = string.Empty;

    public StoryEntityCategory Category { get; set; } = StoryEntityCategory.Other;

    /// <summary>Alternative source spellings or nicknames stored as JSONB in PostgreSQL.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Optional notes on gender, tone, honorifics, or lore context.</summary>
    public string? Description { get; set; }

    public StoryEntityOrigin Origin { get; set; } = StoryEntityOrigin.Manual;

    /// <summary>Fact verification status; AI extraction always initializes as Provisional (never Confirmed).</summary>
    public FactStatus Status { get; set; } = FactStatus.Provisional;

    /// <summary>Optional conflict explanation when Status is Conflicted.</summary>
    public string? ConflictDetails { get; set; }

    /// <summary>When true, AI re-extraction preserves canonical translation and metadata untouched.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Evidence segment ID (required for AI-extracted entities, optional for manual entries).</summary>
    public Guid? EvidenceSegmentId { get; set; }

    /// <summary>Evidence segment stable identifier snapshot ("{novelId:N}:{order:D6}").</summary>
    public string? EvidenceSegmentStableId { get; set; }

    /// <summary>Zero-based order of the evidence segment, if linked to a source segment.</summary>
    public int? EvidenceSegmentOrder { get; set; }

    /// <summary>Optional verbatim quote snippet from the evidence segment.</summary>
    public string? EvidenceQuote { get; set; }

    /// <summary>Optimistic concurrency version incremented on each write.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Novel? Novel { get; init; }

    public Segment? EvidenceSegment { get; init; }
}
