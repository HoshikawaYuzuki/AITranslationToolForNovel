using Basi.Api.Services;
using Xunit;

namespace Basi.Tests.Unit;

public class JobCancellationRegistryTests
{
    private readonly JobCancellationRegistry _registry = new();

    [Fact]
    public void RegisterJob_And_TryCancelJob_CancelsCtsSuccessfully()
    {
        var jobId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        using var registration = _registry.RegisterJob(jobId, cts);

        Assert.False(cts.IsCancellationRequested);

        var cancelled = _registry.TryCancelJob(jobId);

        Assert.True(cancelled);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void TryCancelJob_UnregisteredJob_ReturnsFalse()
    {
        var unregisteredJobId = Guid.NewGuid();
        var result = _registry.TryCancelJob(unregisteredJobId);

        Assert.False(result);
    }

    [Fact]
    public void DisposingRegistration_RemovesJobFromRegistry()
    {
        var jobId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var registration = _registry.RegisterJob(jobId, cts);
        registration.Dispose();

        var cancelled = _registry.TryCancelJob(jobId);

        Assert.False(cancelled);
        Assert.False(cts.IsCancellationRequested);
    }
}
