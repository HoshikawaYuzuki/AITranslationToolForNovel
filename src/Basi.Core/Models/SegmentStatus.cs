namespace Basi.Core.Models;

/// <summary>
/// Status of an individual segment within a translation job.
/// </summary>
public enum SegmentStatus
{
    Pending = 0,
    Translating = 1,
    Completed = 2,
    Failed = 3
}
