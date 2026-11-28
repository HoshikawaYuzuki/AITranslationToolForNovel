namespace Basi.Core.Models;

/// <summary>
/// Character speech register, honorifics, and pronoun observation grounded in a source segment.
/// </summary>
public sealed class SpeechObservation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OwnerId { get; init; }
    public Guid NovelId { get; init; }
    public string TargetLanguage { get; set; } = string.Empty;

    public string SpeakerName { get; set; } = string.Empty;
    public string NormalizedSpeakerName { get; set; } = string.Empty;
    public string? AddresseeName { get; set; }
    public string NormalizedAddresseeName { get; set; } = string.Empty;

    public string SpeechRegister { get; set; } = string.Empty;
    public string? PronounStyle { get; set; }
    public string Observation { get; set; } = string.Empty;

    public StoryEntityOrigin Origin { get; set; } = StoryEntityOrigin.Manual;
    public FactStatus Status { get; set; } = FactStatus.Provisional;
    public string? ConflictDetails { get; set; }
    public bool IsLocked { get; set; }

    public Guid? EvidenceSegmentId { get; set; }
    public string? EvidenceSegmentStableId { get; set; }
    public int? EvidenceSegmentOrder { get; set; }
    public string? EvidenceQuote { get; set; }

    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Novel? Novel { get; init; }
    public Segment? EvidenceSegment { get; init; }
}
