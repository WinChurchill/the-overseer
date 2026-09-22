using System.Text.Json.Serialization;

namespace TheOverseer.Core.Configuration;

public enum VoiceProviderKind
{
    Pack,
    Tts,
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

    public static VoiceSettings Default { get; } = new();

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

    [JsonIgnore]
    public VoiceProviderKind ResolvedProvider =>
        string.Equals(Provider, "tts", StringComparison.OrdinalIgnoreCase) ? VoiceProviderKind.Tts : VoiceProviderKind.Pack;

    [JsonIgnore]
    public string ProviderName => ResolvedProvider == VoiceProviderKind.Tts ? "tts" : "pack";

    [JsonIgnore]
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    [JsonIgnore]
    public bool HasUsableEndpoint => Endpoint is not null;

    public VoiceSettings Normalized() => this with
    {
        Provider = ProviderName,
        PackId = NormalizePackId(PackId),
        Endpoint = NormalizeEndpoint(Endpoint),
        TimeoutSeconds = NormalizeTimeout(TimeoutSeconds),
    };

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
