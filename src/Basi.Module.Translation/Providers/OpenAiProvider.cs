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
        catch (HttpRequestException)
        {
            throw new AiProviderError(AiErrorCode.NetworkError, "Network error connecting to OpenAI API.");
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            throw MapHttpError(httpResponse.StatusCode, errorBody, model);
        }

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(await httpResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new AiProviderError(AiErrorCode.Unknown, "Failed to parse OpenAI response JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Usage metadata parsed first so tokens are preserved even on content filter or error responses
            int promptTokens = 0;
            int completionTokens = 0;
            int totalTokens = 0;

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ctEl)) completionTokens = ctEl.GetInt32();
                if (usage.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
            }
            if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0))
            {
                totalTokens = promptTokens + completionTokens;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                throw new AiProviderError(
                    AiErrorCode.Unknown,
                    "OpenAI returned an empty choices array.",
                    promptTokens: promptTokens,
                    completionTokens: completionTokens,
                    totalTokens: totalTokens,
                    modelUsed: model);
            }

            var firstChoice = choices[0];
            var finishReasonStr = firstChoice.TryGetProperty("finish_reason", out var frEl) ? frEl.GetString() : null;
            var finishReason = finishReasonStr switch
            {
                "stop" => FinishReason.Stop,
                "length" => FinishReason.MaxTokens,
                "content_filter" => FinishReason.ContentFiltered,
                _ => FinishReason.Error
            };

            var actualModel = doc.RootElement.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
            var effectiveModel = !string.IsNullOrWhiteSpace(actualModel) ? actualModel : model;

            if (finishReason == FinishReason.ContentFiltered)
            {
                throw new AiProviderError(
                    AiErrorCode.ContentFiltered,
                    "OpenAI blocked the completion due to content safety policy.",
                    promptTokens: promptTokens,
                    completionTokens: completionTokens,
                    totalTokens: totalTokens,
                    modelUsed: effectiveModel);
            }

            if (finishReason == FinishReason.Error)
            {
                throw new AiProviderError(
                    AiErrorCode.Unknown,
                    "OpenAI completion did not finish successfully.",
                    promptTokens: promptTokens,
                    completionTokens: completionTokens,
                    totalTokens: totalTokens,
                    modelUsed: effectiveModel);
            }

            var contentText = string.Empty;
            if (firstChoice.TryGetProperty("message", out var msgObj)
                && msgObj.TryGetProperty("content", out var contentEl))
            {
                contentText = contentEl.GetString() ?? string.Empty;
            }

            return new AiResponse(
                Content: contentText,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: totalTokens,
                ModelUsed: effectiveModel,
                FinishReason: finishReason
            );
        }
    }

    private static AiProviderError MapHttpError(HttpStatusCode status, string errorBody, string model)
    {
        return status switch
        {
            HttpStatusCode.TooManyRequests => new AiProviderError(AiErrorCode.RateLimited, "OpenAI rate limit exceeded. Please retry later."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiProviderError(AiErrorCode.InvalidApiKey, "OpenAI API key is invalid or unauthorized."),
            HttpStatusCode.NotFound => new AiProviderError(AiErrorCode.ModelNotFound, $"OpenAI model '{model}' not found."),
            HttpStatusCode.BadRequest when errorBody.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
                => new AiProviderError(AiErrorCode.ContentFiltered, "OpenAI content filter triggered."),
            _ => new AiProviderError(AiErrorCode.Unknown, $"OpenAI API request failed with HTTP {(int)status}.")
        };
    }
}
