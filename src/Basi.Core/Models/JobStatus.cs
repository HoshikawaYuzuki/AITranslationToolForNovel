namespace Basi.Core.Models;

/// <summary>
/// Lifecycle states for translation jobs, as specified in CONTRACTS.md.
/// </summary>
public enum JobStatus
{
    Created = 0,
    Queued = 1,
    Processing = 2,
    WaitingForApiKey = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}
