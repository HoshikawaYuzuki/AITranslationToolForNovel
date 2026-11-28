using System.Collections.Concurrent;

namespace Basi.Module.StoryMemory.Services;

/// <summary>
/// Serializes short post-AI database merge-and-commit sections per (novelId, targetLanguage)
/// without holding locks across external AI calls.
/// </summary>
public sealed class StoryMemoryMergeGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(Guid novelId, string targetLanguage, CancellationToken ct = default)
    {
        var key = $"{novelId:N}:{targetLanguage.Trim().ToLowerInvariant()}";
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose()
        {
            Interlocked.Exchange(ref _sem, null)?.Release();
        }
    }
}
