using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// Status text for the Light &amp; voice window. It only reads the selected
/// provider's reported status and the local recording state: it never synthesizes,
/// never sends a request and never plays audio, so opening the panel stays silent.
/// Every unavailable message carries the provider's own bounded code.
/// </summary>
public static class VoiceSetupStatus
{
    /// <summary>Status for a provider that synthesizes or fetches audio on demand.</summary>
    public static string ProviderRow(VoiceSettings settings, VoiceProviderStatus status) => settings.ResolvedProvider switch
    {
        VoiceProviderKind.Tts => !settings.HasUsableEndpoint
            ? "TTS endpoint not configured — add an endpoint or select another provider."
            : status.IsAvailable
                ? "TTS endpoint configured. Preview checks the service; no request is sent on opening this panel."
                : "TTS endpoint unavailable: " + Code(status) + ".",
        VoiceProviderKind.Speech => status.IsAvailable
            ? "Windows speech baseline ready (offline, no download)."
            : "Windows speech baseline unavailable: " + Code(status) + " — assign a WAV or choose another provider.",
        _ => PackRow(settings.PackId, clipAvailable: false),
    };

    /// <summary>Status for the local voice pack, from a bounded local clip probe.</summary>
    public static string PackRow(string packId, bool clipAvailable) => clipAvailable
        ? "Ready from voice pack: " + packId + "."
        : "No usable recording in voice pack " + packId + " — assign a WAV or choose the Windows speech baseline.";

    /// <summary>Machine-level availability of the offline baseline, whatever is selected.</summary>
    public static string Baseline(VoiceProviderStatus baseline) => baseline.IsAvailable
        ? "Windows speech baseline (offline): available."
        : "Windows speech baseline (offline): unavailable — " + Code(baseline) + ".";

    private static string Code(VoiceProviderStatus status) =>
        string.IsNullOrWhiteSpace(status.LastCode) ? "unknown" : status.LastCode;
}
