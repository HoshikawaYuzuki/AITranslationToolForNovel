using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.Translation.Providers;

/// <summary>
/// Google Gemini API adapter implementing IAiProvider.
/// </summary>
public sealed class GeminiProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GeminiProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey.Trim() : throw new ArgumentException("API key cannot be empty.", nameof(apiKey));
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim() : "gemini-2.5-flash";
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";

        var payload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = request.UserPrompt }
                    }
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = request.Temperature,
                ["maxOutputTokens"] = request.MaxTokens
            }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject { ["text"] = request.SystemPrompt }
                }
            };
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);

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
            throw new AiProviderError(AiErrorCode.NetworkError, "Network error connecting to Gemini API.");
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
            throw new AiProviderError(AiErrorCode.Unknown, "Failed to parse Gemini response JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Usage metadata parsed first so tokens are preserved even on content filter or error responses
            int promptTokens = 0;
            int completionTokens = 0;
            int totalTokens = 0;

            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var pt)) promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("candidatesTokenCount", out var ctEl)) completionTokens = ctEl.GetInt32();
                if (usage.TryGetProperty("totalTokenCount", out var tt)) totalTokens = tt.GetInt32();
            }
            if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0))
            {
                totalTokens = promptTokens + completionTokens;
            }

            // Check promptFeedback for block reason
            if (root.TryGetProperty("promptFeedback", out var promptFeedback)
                && promptFeedback.TryGetProperty("blockReason", out var blockReasonEl))
            {
                var blockReason = blockReasonEl.GetString();
                if (!string.IsNullOrEmpty(blockReason))
                {
                    throw new AiProviderError(
                        AiErrorCode.ContentFiltered,
                        "Gemini blocked prompt due to content safety policy.",
                        promptTokens: promptTokens,
                        completionTokens: completionTokens,
                        totalTokens: totalTokens,
                        modelUsed: model);
                }
            }

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                throw new AiProviderError(
                    AiErrorCode.ContentFiltered,
                    "Gemini returned no candidates in response (possible safety block).",
                    promptTokens: promptTokens,
                    completionTokens: completionTokens,
                    totalTokens: totalTokens,
                    modelUsed: model);
            }

            var firstCandidate = candidates[0];
            var finishReasonStr = firstCandidate.TryGetProperty("finishReason", out var frEl) ? frEl.GetString() : null;
            var finishReason = finishReasonStr switch
            {
                "STOP" => FinishReason.Stop,
                "MAX_TOKENS" => FinishReason.MaxTokens,
                "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" => FinishReason.ContentFiltered,
                _ => FinishReason.Error
            };

            if (finishReason == FinishReason.ContentFiltered)
            {
                throw new AiProviderError(
                    AiErrorCode.ContentFiltered,
                    "Gemini candidate blocked by content filter.",
                    promptTokens: promptTokens,
                    completionTokens: completionTokens,
                    totalTokens: totalTokens,
                    modelUsed: model);
            }

            if (finishReason == FinishReason.Error)
            {
                throw new AiProviderError(
                    AiErrorCode.Unknown,
                    "Gemini candidate did not complete successfully.",
                    promptTokens: promptTokens,
                    completionTokens: completionTokens,
                    totalTokens: totalTokens,
                    modelUsed: model);
            }

            var contentText = string.Empty;
            if (firstCandidate.TryGetProperty("content", out var contentObj)
                && contentObj.TryGetProperty("parts", out var parts)
                && parts.GetArrayLength() > 0)
            {
                var textParts = new List<string>();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textEl))
                    {
                        textParts.Add(textEl.GetString() ?? string.Empty);
                    }
                }
                contentText = string.Join("\n", textParts);
            }

            return new AiResponse(
                Content: contentText,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: totalTokens,
                ModelUsed: model,
                FinishReason: finishReason
            );
        }
    }

    private static AiProviderError MapHttpError(HttpStatusCode status, string errorBody, string model)
    {
        return status switch
        {
            HttpStatusCode.TooManyRequests => new AiProviderError(AiErrorCode.RateLimited, "Gemini rate limit exceeded. Please retry later."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiProviderError(AiErrorCode.InvalidApiKey, "Gemini API key is invalid or lacks permission."),
            HttpStatusCode.NotFound => new AiProviderError(AiErrorCode.ModelNotFound, $"Gemini model '{model}' not found."),
            HttpStatusCode.BadRequest when errorBody.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
                                       || errorBody.Contains("API key not valid", StringComparison.OrdinalIgnoreCase)
                => new AiProviderError(AiErrorCode.InvalidApiKey, "Gemini API key is invalid."),
            HttpStatusCode.BadRequest when errorBody.Contains("SAFETY", StringComparison.OrdinalIgnoreCase)
                                       || errorBody.Contains("HARM_CATEGORY", StringComparison.OrdinalIgnoreCase)
                => new AiProviderError(AiErrorCode.ContentFiltered, "Gemini blocked the prompt due to safety policy."),
            _ => new AiProviderError(AiErrorCode.Unknown, $"Gemini API request failed with HTTP {(int)status}.")
        };
    }
}
