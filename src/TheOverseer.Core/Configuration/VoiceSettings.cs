using System.Text.Json.Serialization;

namespace TheOverseer.Core.Configuration;

public enum VoiceProviderKind
{
    Pack,
    Tts,

    /// <summary>Offline Windows speech synthesis of the fixed catalog scripts.</summary>
    Speech,
}

/// <summary>
/// Normalized voice configuration. Voice starts muted by design: notifications
/// are opt-in, and no network provider is ever used unless the user selects TTS
/// and configures an endpoint.
/// </summary>
public sealed record VoiceSettings
{
    public const double MinimumTimeoutSeconds = 1;
    public const double MaximumTimeoutSeconds = 15;
    public const double DefaultTimeoutSeconds = 5;
    public const int MaximumPackIdChars = 32;
    public const int MaximumEndpointChars = 512;
    public const string DefaultPackId = "example";
    public const string BundledPackId = "kokoro-en";

    public static VoiceSettings Default { get; } = new();

    /// <summary>
    /// Default for a data directory that has no configuration file at all: the
    /// included Kokoro pack is selected and voice is still muted. It is
    /// only ever applied when no stored file exists, so a user's explicit pack,
    /// TTS, mute, endpoint, event and clip preferences are never reset.
    /// </summary>
    public static VoiceSettings BaselineDefault { get; } = Default with { Provider = PackProviderName, PackId = BundledPackId };

    public const string PackProviderName = "pack";
    public const string SpeechProviderName = "speech";
    public const string TtsProviderName = "tts";

    [JsonPropertyName("muted")]
    public bool Muted { get; init; } = true;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "pack";

    [JsonPropertyName("packId")]
    public string PackId { get; init; } = DefaultPackId;

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    [JsonPropertyName("timeoutSeconds")]
    public double TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    [JsonPropertyName("events")]
    public IReadOnlyDictionary<string, bool> Events { get; init; } = new Dictionary<string, bool>();

    [JsonPropertyName("clips")]
    public IReadOnlyDictionary<string, string> Clips { get; init; } = new Dictionary<string, string>();

    public bool IsEnabled(TheOverseer.Core.Voice.VoiceEventKind kind)
    {
        var entry = TheOverseer.Core.Voice.VoiceEventCatalog.For(kind);
        return Events is not null && Events.TryGetValue(entry.Code, out var enabled) ? enabled : entry.DefaultEnabled;
    }

    [JsonIgnore]
    public VoiceProviderKind ResolvedProvider =>
        string.Equals(Provider, TtsProviderName, StringComparison.OrdinalIgnoreCase) ? VoiceProviderKind.Tts
        : string.Equals(Provider, SpeechProviderName, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(Provider, "baseline", StringComparison.OrdinalIgnoreCase) ? VoiceProviderKind.Speech
        : VoiceProviderKind.Pack;

    [JsonIgnore]
    public string ProviderName => ResolvedProvider switch
    {
        VoiceProviderKind.Tts => TtsProviderName,
        VoiceProviderKind.Speech => SpeechProviderName,
        _ => PackProviderName,
    };

    [JsonIgnore]
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    [JsonIgnore]
    public bool HasUsableEndpoint => Endpoint is not null;

    /// <summary>
    /// Effective voice settings for one loaded configuration. Only a completely
    /// missing configuration file takes the agreed baseline default; an existing
    /// file is used exactly as written, so an absent "provider" key continues to
    /// mean the current pack behaviour instead of silently resetting a choice.
    /// </summary>
    public static VoiceSettings Effective(ConfigLoadStatus configStatus, VoiceSettings? configured) =>
        (configStatus == ConfigLoadStatus.Missing ? BaselineDefault : configured ?? Default).Normalized();

    public VoiceSettings Normalized() => this with
    {
        Provider = ProviderName,
        PackId = NormalizePackId(PackId),
        Endpoint = NormalizeEndpoint(Endpoint),
        TimeoutSeconds = NormalizeTimeout(TimeoutSeconds),
        Events = TheOverseer.Core.Voice.VoiceEventCatalog.All.ToDictionary(item => item.Code, item => IsEnabled(item.Kind)),
        Clips = (Clips ?? new Dictionary<string, string>())
            .Where(pair => TheOverseer.Core.Voice.VoiceEventCatalog.All.Any(item => item.Code == pair.Key) &&
                IsSafeClipName(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value),
    };

    public static bool IsSafeClipName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 100 &&
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static double NormalizeTimeout(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultTimeoutSeconds;
        }

        return Math.Clamp(value, MinimumTimeoutSeconds, MaximumTimeoutSeconds);
    }

    /// <summary>Pack ids are single safe path segments; anything else falls back.</summary>
    public static string NormalizePackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultPackId;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaximumPackIdChars)
        {
            return DefaultPackId;
        }

        foreach (var character in trimmed)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
            if (!allowed)
            {
                return DefaultPackId;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Only absolute https, or http on loopback, is accepted. Userinfo,
    /// fragments, other schemes and oversized values become null (no endpoint).
    /// </summary>
    public static string? NormalizeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaximumEndpointChars)
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri.ToString();
        }

        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return uri.ToString();
        }

        return null;
    }
}
