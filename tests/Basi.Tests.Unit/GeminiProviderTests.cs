using System.Net;
using System.Text;
using System.Text.Json;
using Basi.Core.Models;
using Basi.Module.Translation.Providers;
using Xunit;

namespace Basi.Tests.Unit;

public class GeminiProviderTests
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
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[] { new { text = "Xin chào thế giới" } }
                    },
                    finishReason = "STOP"
                }
            },
            usageMetadata = new
            {
                promptTokenCount = 10,
                candidatesTokenCount = 5,
                totalTokenCount = 15
            }
        });

        var handler = new MockHttpHandler(req =>
        {
            Assert.Contains("gemini-2.5-flash", req.RequestUri!.ToString());
            Assert.DoesNotContain("key=", req.RequestUri.ToString());
            Assert.True(req.Headers.Contains("x-goog-api-key"));
            Assert.Equal("test-api-key", req.Headers.GetValues("x-goog-api-key").First());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var provider = new GeminiProvider(client, "test-api-key");

        var result = await provider.CompleteAsync(new AiRequest("gemini-2.5-flash", "Translate to Vietnamese", "Hello world"));

        Assert.Equal("Xin chào thế giới", result.Content);
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(5, result.CompletionTokens);
        Assert.Equal(15, result.TotalTokens);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_RateLimited_ThrowsRateLimitedError()
    {
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\": {\"message\": \"Resource exhausted\"}}", Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new GeminiProvider(client, "test-api-key");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gemini-2.5-flash", "system", "prompt")));

        Assert.Equal(AiErrorCode.RateLimited, ex.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_InvalidApiKey_ThrowsInvalidApiKeyError()
    {
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\": {\"message\": \"API_KEY_INVALID\"}}", Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new GeminiProvider(client, "test-api-key");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gemini-2.5-flash", "system", "prompt")));

        Assert.Equal(AiErrorCode.InvalidApiKey, ex.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_SafetyBlock_ThrowsContentFilteredError()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    finishReason = "SAFETY"
                }
            }
        });

        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new GeminiProvider(client, "test-api-key");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gemini-2.5-flash", "system", "harmful text")));

        Assert.Equal(AiErrorCode.ContentFiltered, ex.ErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CompleteAsync_UpstreamErrorContainingCanary_DoesNotLeakCanaryInException(HttpStatusCode statusCode)
    {
        const string canarySecret = "CANARY_SECRET_12345_DO_NOT_LEAK";
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent($"{{\"error\": {{\"message\": \"Error with secret: {canarySecret}\"}}}}", Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new GeminiProvider(client, "test-api-key");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gemini-2.5-flash", "system", "prompt")));

        Assert.DoesNotContain(canarySecret, ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_Http200BlockReasonContainingCanary_DoesNotLeakCanaryInException()
    {
        // Audit repro R01: Upstream returns HTTP 200 with promptFeedback.blockReason containing canary
        const string canarySecret = "BASI_AUDIT_FAKE_KEY_NOT_A_SECRET";
        var responseJson = JsonSerializer.Serialize(new
        {
            promptFeedback = new
            {
                blockReason = canarySecret
            }
        });

        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler);
        var provider = new GeminiProvider(client, "test-api-key");

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            provider.CompleteAsync(new AiRequest("gemini-2.5-flash", "system", "prompt")));

        Assert.Equal(AiErrorCode.ContentFiltered, ex.ErrorCode);
        Assert.DoesNotContain(canarySecret, ex.Message);
    }
}
