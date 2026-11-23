using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.Translation.Providers;

/// <summary>
/// OpenAI Chat Completions API adapter implementing IAiProvider.
/// </summary>
public sealed class OpenAiProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public OpenAiProvider(HttpClient httpClient, string apiKey, string? customEndpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey.Trim() : throw new ArgumentException("API key cannot be empty.", nameof(apiKey));
        _endpoint = !string.IsNullOrWhiteSpace(customEndpoint) ? customEndpoint.Trim() : "https://api.openai.com/v1/chat/completions";
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim() : "gpt-4o-mini";

        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = request.UserPrompt
        });

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.SendAsync(httpRequest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderError(AiErrorCode.NetworkError, $"Network error connecting to OpenAI API: {ex.Message}");
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            throw MapHttpError(httpResponse.StatusCode, errorBody);
        }

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(await httpResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new AiProviderError(AiErrorCode.Unknown, $"Failed to parse OpenAI response JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                throw new AiProviderError(AiErrorCode.Unknown, "OpenAI returned an empty choices array.");
            }

            var firstChoice = choices[0];
            var finishReasonStr = firstChoice.TryGetProperty("finish_reason", out var frEl) ? frEl.GetString() : null;
            var finishReason = finishReasonStr switch
            {
                "stop" => FinishReason.Stop,
                "length" => FinishReason.MaxTokens,
                "content_filter" => FinishReason.ContentFiltered,
                _ => FinishReason.Stop
            };

            if (finishReason == FinishReason.ContentFiltered)
            {
                throw new AiProviderError(AiErrorCode.ContentFiltered, "OpenAI blocked the completion due to content safety policy.");
            }

            var contentText = string.Empty;
            if (firstChoice.TryGetProperty("message", out var msgObj)
                && msgObj.TryGetProperty("content", out var contentEl))
            {
                contentText = contentEl.GetString() ?? string.Empty;
            }

            int promptTokens = 0;
            int completionTokens = 0;
            int totalTokens = 0;

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ctEl)) completionTokens = ctEl.GetInt32();
                if (usage.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
            }

            return new AiResponse(
                Content: contentText,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: totalTokens > 0 ? totalTokens : promptTokens + completionTokens,
                ModelUsed: model,
                FinishReason: finishReason
            );
        }
    }

    private static AiProviderError MapHttpError(HttpStatusCode status, string errorBody)
    {
        return status switch
        {
            HttpStatusCode.TooManyRequests => new AiProviderError(AiErrorCode.RateLimited, $"OpenAI rate limit exceeded: {errorBody}"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiProviderError(AiErrorCode.InvalidApiKey, $"OpenAI API key is invalid or unauthorized: {errorBody}"),
            HttpStatusCode.NotFound => new AiProviderError(AiErrorCode.ModelNotFound, $"OpenAI model or endpoint not found: {errorBody}"),
            HttpStatusCode.BadRequest when errorBody.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
                => new AiProviderError(AiErrorCode.ContentFiltered, $"OpenAI content filter triggered: {errorBody}"),
            _ => new AiProviderError(AiErrorCode.Unknown, $"OpenAI API error (HTTP {(int)status}): {errorBody}")
        };
    }
}
