namespace Basi.Core.Models;

/// <summary>
/// Canonical story memory / glossary entity scoped to a novel, owner, and target language.
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

    /// <summary>NFKC-normalized lowercase key used for deduplication and matching.</summary>
    public string NormalizedSourceTerm { get; set; } = string.Empty;

    /// <summary>Preferred canonical translation in the target language.</summary>
    public string TranslatedTerm { get; set; } = string.Empty;

    public StoryEntityCategory Category { get; set; } = StoryEntityCategory.Other;

    /// <summary>Alternative source spellings or nicknames stored as JSONB in PostgreSQL.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Optional notes on gender, tone, honorifics, or lore context.</summary>
    public string? Description { get; set; }

    public StoryEntityOrigin Origin { get; set; } = StoryEntityOrigin.Manual;

    /// <summary>When true, AI re-extraction preserves canonical translation and metadata untouched.</summary>
    public bool IsLocked { get; set; } = true;

    /// <summary>Zero-based segment order where this term was first encountered, if known.</summary>
    public int? FirstSeenOrder { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Novel? Novel { get; init; }
}
