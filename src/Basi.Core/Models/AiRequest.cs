namespace Basi.Core.Models;

public record AiRequest(
    string Model,
    string SystemPrompt,
    string UserPrompt,
    int MaxTokens = 2048,
    double Temperature = 0.7
);
