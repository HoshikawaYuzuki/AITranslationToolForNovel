namespace Basi.Core.Models;

public sealed class TranslationJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Owner ID referencing ApplicationUser.Id.</summary>
    public Guid OwnerId { get; init; }

    /// <summary>Novel ID referencing Novel.Id.</summary>
    public Guid NovelId { get; init; }

    /// <summary>Target BCP-47 language tag, e.g. "vi", "en".</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>Target AI provider: "Gemini", "OpenAI", "Mock".</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>AI model identifier, e.g. "gemini-2.5-flash", "gpt-4o-mini".</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Actual AI model version returned in provider responses.</summary>
    public string? ActualModel { get; set; }

    /// <summary>Number of AI provider batch requests executed for this job.</summary>
    public int RequestCount { get; set; }

    /// <summary>Current lifecycle status.</summary>
    public JobStatus Status { get; set; } = JobStatus.Created;

    /// <summary>Total segments in the job.</summary>
    public int TotalSegments { get; set; }

    /// <summary>Successfully completed segments count.</summary>
    public int CompletedSegments { get; set; }

    /// <summary>Total prompt / input tokens consumed by the job.</summary>
    public int PromptTokens { get; set; }

    /// <summary>Total completion / output tokens consumed by the job.</summary>
    public int CompletionTokens { get; set; }

    /// <summary>Total tokens consumed across all batches in this job.</summary>
    public int TotalTokens { get; set; }

    /// <summary>Error details if the job failed.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Novel? Novel { get; init; }

    public ICollection<TranslatedSegment> TranslatedSegments { get; init; } = new List<TranslatedSegment>();
}
