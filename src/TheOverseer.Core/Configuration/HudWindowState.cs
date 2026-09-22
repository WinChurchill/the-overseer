using System.Text.Json.Serialization;

namespace TheOverseer.Core.Configuration;

/// <summary>
/// Persisted HUD placement and expansion state. Coordinates are display
/// independent pixels relative to the saved monitor's working-area origin plus
/// the DPI scale in effect when they were saved, so a later DPI change keeps the
/// same physical spot and a removed monitor can be recovered from.
/// </summary>
public sealed record HudWindowState
{
    public const int CurrentVersion = 1;
    public const double MinimumDpiScale = 0.5;
    public const double MaximumDpiScale = 4.0;
    public const double MaximumOffsetDips = 200_000;
    public const int MaximumDeviceNameChars = 128;

    public static HudWindowState Default { get; } = new();

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("isExpanded")]
    public bool IsExpanded { get; init; }

    [JsonPropertyName("monitorDevice")]
    public string MonitorDeviceName { get; init; } = string.Empty;

    [JsonPropertyName("offsetXDips")]
    public double OffsetXDips { get; init; }

    [JsonPropertyName("offsetYDips")]
    public double OffsetYDips { get; init; }

    [JsonPropertyName("dpiScale")]
    public double DpiScale { get; init; } = 1.0;

    [JsonPropertyName("savedAtUtc")]
    public DateTimeOffset? SavedAtUtc { get; init; }

    /// <summary>True when there is no usable saved placement yet.</summary>
    [JsonIgnore]
    public bool IsEmpty => MonitorDeviceName.Length == 0;

    public HudWindowState Normalized() => this with
    {
        Version = CurrentVersion,
        MonitorDeviceName = Bounded(MonitorDeviceName, MaximumDeviceNameChars),
        OffsetXDips = BoundedOffset(OffsetXDips),
        OffsetYDips = BoundedOffset(OffsetYDips),
        DpiScale = double.IsNaN(DpiScale) || double.IsInfinity(DpiScale)
            ? 1.0
            : Math.Clamp(DpiScale, MinimumDpiScale, MaximumDpiScale),
    };

    private static double BoundedOffset(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, -MaximumOffsetDips, MaximumOffsetDips);
    }

    private static string Bounded(string? value, int maximumChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumChars ? trimmed : trimmed[..maximumChars];
    }
}
