using KageDesk.Rendering.Sprites;

namespace KageDesk.Rendering.Scenes;

/// <summary>
/// Zero-allocation value-type state bundle for v2.2 interactive & ambient effects,
/// shared symmetrically between HqWallpaperRenderer and TaskbarPetRenderer.
/// </summary>
public readonly record struct SceneEffectsState(
    MixiChillActivity? PinnedMixiActivity = null,
    string? MixiSpeechText = null,
    float MixiPokeElapsedSeconds = -1f,
    bool IsMediaPlaying = false,
    float WakeUpPulseRemainingSeconds = 0f
)
{
    public const float MixiSpeechDurationSeconds = 3.5f;
    public const float MixiStartleDurationSeconds = 0.5f;

    public bool HasActiveMixiSpeech =>
        !string.IsNullOrEmpty(MixiSpeechText) &&
        MixiPokeElapsedSeconds >= 0f &&
        MixiPokeElapsedSeconds < MixiSpeechDurationSeconds;

    public bool IsMixiStartled =>
        MixiPokeElapsedSeconds >= 0f &&
        MixiPokeElapsedSeconds < MixiStartleDurationSeconds;
}
