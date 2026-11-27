namespace Basi.Core.Models;

/// <summary>
/// Lightweight projection passed from StoryMemory/Api to Translation prompt builders without cross-module coupling.
/// </summary>
public sealed record GlossaryContextItem(
    string SourceTerm,
    string TranslatedTerm,
    string Category,
    IReadOnlyList<string> Aliases,
    string? Description);
