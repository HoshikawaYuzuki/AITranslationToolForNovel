namespace Basi.Core.Models;

public sealed class Novel
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The user who owns this novel (references ApplicationUser.Id).</summary>
    public Guid OwnerId { get; init; }

    public string Title { get; set; } = string.Empty;

    /// <summary>BCP-47 language tag, e.g. "ja", "zh-CN", "ko".</summary>
    public string SourceLanguage { get; set; } = string.Empty;

    public DateTime UploadedAt { get; init; } = DateTime.UtcNow;

    public ICollection<Segment> Segments { get; init; } = new List<Segment>();
}
