using Basi.Core.Models;
using Basi.Module.Translation.Providers;

namespace Basi.Tests.Unit;

public class MockAiProviderTests
{
    private static AiRequest CreateRequest() =>
        new("gpt-4", "You are a translator.", "Translate this text.", 1024, 0.7);

    [Fact]
    public async Task Test_Success_ReturnsValidResponse()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.Success
        });

        var response = await provider.CompleteAsync(CreateRequest());

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.Content));
        Assert.True(response.PromptTokens > 0);
        Assert.True(response.CompletionTokens > 0);
        Assert.Equal(response.PromptTokens + response.CompletionTokens, response.TotalTokens);
        Assert.Equal("gpt-4", response.ModelUsed);
        Assert.Equal(FinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task Test_Error_ThrowsAiProviderError()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.Error,
            ErrorCode = AiErrorCode.InvalidApiKey
        });

        var ex = await Assert.ThrowsAsync<AiProviderError>(
            () => provider.CompleteAsync(CreateRequest()));

        Assert.Equal(AiErrorCode.InvalidApiKey, ex.ErrorCode);
    }

    [Fact]
    public async Task Test_Timeout_ThrowsTimeoutException()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.Timeout,
            TimeoutDelay = TimeSpan.FromMilliseconds(50)
        });

        await Assert.ThrowsAsync<TimeoutException>(
            () => provider.CompleteAsync(CreateRequest()));
    }

    [Fact]
    public async Task Test_RateLimited_ThrowsWithCorrectCode()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.RateLimited
        });

        var ex = await Assert.ThrowsAsync<AiProviderError>(
            () => provider.CompleteAsync(CreateRequest()));

        Assert.Equal(AiErrorCode.RateLimited, ex.ErrorCode);
    }

    [Fact]
    public async Task Test_InvalidOutput_ReturnsUnexpectedContent()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.InvalidOutput
        });

        var response = await provider.CompleteAsync(CreateRequest());

        Assert.NotNull(response);
        Assert.Contains("MALFORMED", response.Content);
        Assert.Equal(FinishReason.Error, response.FinishReason);
    }

    [Fact]
    public async Task Test_PartialResponse_MissingSegmentIds()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.PartialResponse
        });

        var response = await provider.CompleteAsync(CreateRequest());

        Assert.NotNull(response);
        // Should contain seg-001 but NOT seg-002 or seg-003
        Assert.Contains("[seg-001]", response.Content);
        Assert.DoesNotContain("[seg-002]", response.Content);
        Assert.DoesNotContain("[seg-003]", response.Content);
        Assert.Equal(FinishReason.MaxTokens, response.FinishReason);
    }

    [Fact]
    public async Task Test_Success_WithContextInUserPrompt_TranslatesOnlyTargetSegmentsAndExcludesContext()
    {
        var provider = new MockAiProvider(new MockAiProviderOptions
        {
            Scenario = MockScenario.Success
        });

        var userPromptWithContext =
            "--- RECENT CONTEXT (FOR CONTINUITY ONLY — DO NOT TRANSLATE AGAIN) ---\n" +
            "[SEG:0] Đoạn dịch cũ 0\n" +
            "[SEG:1] Đoạn dịch cũ 1\n\n" +
            "--- SEGMENTS TO TRANSLATE ---\n" +
            "[SEG:2] Đoạn nguồn 2\n" +
            "[SEG:3] Đoạn nguồn 3";

        var request = new AiRequest(
            Model: "mock-v1",
            SystemPrompt: "Translate segments.",
            UserPrompt: userPromptWithContext,
            MaxTokens: 1024,
            Temperature: 0.7);

        var response = await provider.CompleteAsync(request);

        Assert.NotNull(response);
        // Must contain target segments
        Assert.Contains("[SEG:2]", response.Content);
        Assert.Contains("[SEG:3]", response.Content);
        // Must NOT contain context segments!
        Assert.DoesNotContain("[SEG:0]", response.Content);
        Assert.DoesNotContain("[SEG:1]", response.Content);
    }
}
