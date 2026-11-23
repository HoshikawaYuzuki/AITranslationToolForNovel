using System.Net;
using System.Text;
using System.Text.Json;
using Basi.Core.Models;
using Basi.Module.Translation.Providers;
using Xunit;

namespace Basi.Tests.Unit;

public class OpenAiProviderTests
{
    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task CompleteAsync_SuccessfulResponse_ReturnsTranslatedTextAndUsage()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "Bản dịch tiếng Việt" },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = 12,
                completion_tokens = 8,
                total_tokens = 20
            }
        });

        var handler = new MockHttpHandler(req =>
        {
            Assert.Equal("Bearer sk-test-openai", req.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, "sk-test-openai");

        var result = await provider.CompleteAsync(new AiRequest("gpt-4o-mini", "system prompt", "Source text"));

        Assert.Equal("Bản dịch tiếng Việt", result.Content);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(8, result.CompletionTokens);
        Assert.Equal(20, result.TotalTokens);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_RateLimited_ThrowsRateLimitedError()
    {
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\": {\"message\": \"Rate limit reached\"}}", Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, "sk-test-openai");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gpt-4o-mini", "system", "prompt")));

        Assert.Equal(AiErrorCode.RateLimited, ex.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_InvalidApiKey_ThrowsInvalidApiKeyError()
    {
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\": {\"message\": \"Incorrect API key provided\"}}", Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, "sk-test-openai");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gpt-4o-mini", "system", "prompt")));

        Assert.Equal(AiErrorCode.InvalidApiKey, ex.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_ContentFiltered_ThrowsContentFilteredError()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "" },
                    finish_reason = "content_filter"
                }
            }
        });

        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, "sk-test-openai");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gpt-4o-mini", "system", "harmful prompt")));

        Assert.Equal(AiErrorCode.ContentFiltered, ex.ErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CompleteAsync_UpstreamErrorContainingCanary_DoesNotLeakCanaryInException(HttpStatusCode statusCode)
    {
        const string canarySecret = "CANARY_SECRET_OPENAI_67890_NO_LEAK";
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent($"{{\"error\": {{\"message\": \"Error with secret: {canarySecret}\"}}}}", Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, "sk-test-openai");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gpt-4o-mini", "system", "prompt")));

        Assert.DoesNotContain(canarySecret, ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_ContentFilteredWithUsage_PreservesTokensInAiProviderError()
    {
        // Audit repro M02: Upstream returns HTTP 200 with finish_reason=content_filter and usage metadata
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "" },
                    finish_reason = "content_filter"
                }
            },
            usage = new
            {
                prompt_tokens = 9,
                completion_tokens = 6,
                total_tokens = 15
            }
        });

        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, "sk-test-openai");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gpt-4o-mini", "system", "harmful prompt")));

        Assert.Equal(AiErrorCode.ContentFiltered, ex.ErrorCode);
        Assert.Equal(9, ex.PromptTokens);
        Assert.Equal(6, ex.CompletionTokens);
        Assert.Equal(15, ex.TotalTokens);
        Assert.Equal("gpt-4o-mini", ex.ModelUsed);
    }
}
