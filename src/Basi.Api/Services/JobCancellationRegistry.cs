using System.Collections.Concurrent;
using Basi.Core.Interfaces;

namespace Basi.Api.Services;

public sealed class JobCancellationRegistry : IJobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();

    public IDisposable RegisterJob(Guid jobId, CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
        _activeJobs[jobId] = cts;
        return new UnregisterDisposable(this, jobId);
    }

    public bool TryCancelJob(Guid jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var cts))
        {
            try
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // Already completed/disposed
            }
        }

        return false;
    }

    private void Unregister(Guid jobId)
    {
        _activeJobs.TryRemove(jobId, out _);
    }

    private sealed class UnregisterDisposable : IDisposable
    {
        private readonly JobCancellationRegistry _registry;
        private readonly Guid _jobId;
        private bool _disposed;

        public UnregisterDisposable(JobCancellationRegistry registry, Guid jobId)
        {
            _registry = registry;
            _jobId = jobId;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _registry.Unregister(_jobId);
            }
        }
    }
}
