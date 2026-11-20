namespace Basi.Core.Models;

public sealed class Segment
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid NovelId { get; init; }

    /// <summary>
    /// Stable, human-readable identifier that survives re-uploads.
    /// Format: "{novelId:N}:{order:D6}" — e.g. "a1b2c3d4e5f6...000001".
    /// </summary>
    public string StableId { get; init; } = string.Empty;

    /// <summary>Zero-based position within the novel.</summary>
    public int Order { get; init; }

    /// <summary>Trimmed paragraph text.</summary>
    public string Content { get; set; } = string.Empty;

    public Novel? Novel { get; init; }
}
