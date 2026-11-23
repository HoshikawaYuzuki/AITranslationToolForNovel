namespace Basi.Core.Models;

public sealed class TranslatedSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Parent translation job ID.</summary>
    public Guid JobId { get; init; }

    /// <summary>Original novel segment ID.</summary>
    public Guid SegmentId { get; init; }

    /// <summary>Zero-based index within the novel/job.</summary>
    public int Order { get; init; }

    /// <summary>Source paragraph text.</summary>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>Translated text result.</summary>
    public string? TranslatedText { get; set; }

    /// <summary>Status of this segment.</summary>
    public SegmentStatus Status { get; set; } = SegmentStatus.Pending;

    /// <summary>Error message if segment translation failed.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public TranslationJob? Job { get; init; }

    public Segment? Segment { get; init; }
}
