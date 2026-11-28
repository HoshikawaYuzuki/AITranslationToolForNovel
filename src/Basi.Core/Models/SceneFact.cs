namespace Basi.Core.Models;

/// <summary>
/// Scene-level continuity observation grounded in a source segment.
/// </summary>
public sealed class SceneFact
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OwnerId { get; init; }
    public Guid NovelId { get; init; }
    public string TargetLanguage { get; set; } = string.Empty;

    public string FactKey { get; set; } = string.Empty;
    public string NormalizedFactKey { get; set; } = string.Empty;
    public SceneFactType FactType { get; set; } = SceneFactType.Other;
    public string Summary { get; set; } = string.Empty;

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
