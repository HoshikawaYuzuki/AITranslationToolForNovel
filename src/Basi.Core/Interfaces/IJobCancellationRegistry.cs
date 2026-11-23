namespace Basi.Core.Interfaces;

public interface IJobCancellationRegistry
{
    IDisposable RegisterJob(Guid jobId, CancellationTokenSource cts);
    bool TryCancelJob(Guid jobId);
}
