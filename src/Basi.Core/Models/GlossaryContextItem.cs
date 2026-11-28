namespace Basi.Core.Models;

/// <summary>
/// Lightweight projections passed from StoryMemory/Api to Translation prompt builders without cross-module coupling.
/// </summary>
public sealed record GlossaryContextItem(
    string SourceTerm,
    string TranslatedTerm,
    string Category,
    IReadOnlyList<string> Aliases,
    string? Description);

public sealed record SceneFactContextItem(
    string FactKey,
    string FactType,
    string Summary,
    string Status);

public sealed record SpeechObservationContextItem(
    string SpeakerName,
    string? AddresseeName,
    string SpeechRegister,
    string? PronounStyle,
    string Observation,
    string Status);
