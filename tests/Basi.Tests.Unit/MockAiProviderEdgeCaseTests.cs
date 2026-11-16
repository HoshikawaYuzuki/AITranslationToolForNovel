using Basi.Core.Models;
using Basi.Module.Translation.Providers;

namespace Basi.Tests.Unit;

public class MockAiProviderEdgeCaseTests
{
    [Fact]
    public async Task CompleteAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.Success
        });

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.CompleteAsync(null!));
    }

    [Fact]
    public async Task CompleteAsync_WhenCancellationTokenCancelled_ThrowsOperationCanceledException()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.Success
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CompleteAsync(new AiRequest("gpt-4", "sys", "user", 100, 0.5), cts.Token));
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyPrompts_CalculatesTokensSafely()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.Success
        });

        var response = await provider.CompleteAsync(new AiRequest("gpt-4", "", "", 100, 0.5));

        Assert.NotNull(response);
        Assert.True(response.PromptTokens >= 0);
        Assert.Equal(response.PromptTokens + response.CompletionTokens, response.TotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_WithInvalidScenarioEnum_ThrowsArgumentOutOfRangeException()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = (MockScenario)999
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.CompleteAsync(new AiRequest("gpt-4", "sys", "user", 100, 0.5)));
    }
}
