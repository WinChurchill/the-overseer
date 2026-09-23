using System.Text.Json.Serialization;

namespace TheOverseer.Core.Configuration;

public enum HudTheme
{
    Dark,
    Light,
}

/// <summary>
/// Additive HUD preferences stored with the monitor settings. Every value is
/// validated and clamped, so a hand-edited file cannot produce an unusable
/// window. Light/Dark are the only accepted themes.
/// </summary>
public sealed record HudSettings
{
    public const double MinimumOpacity = 0.65;
    public const double MaximumOpacity = 1.0;
    public const double DefaultOpacity = 0.96;

    public static HudSettings Default { get; } = new();

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "dark";

    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; init; } = true;

    [JsonPropertyName("opacity")]
    public double Opacity { get; init; } = DefaultOpacity;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; init; }

    [JsonPropertyName("taskView")]
    public string TaskView { get; init; } = "now";

    [JsonPropertyName("recentHours")]
    public int RecentHours { get; init; } = 2;

    public static string NormalizeTaskView(string? value) => value?.ToLowerInvariant() switch
    {
        "recent" => "recent",
        "session" => "session",
        _ => "now",
    };

    [JsonIgnore]
    public HudTheme ResolvedTheme => string.Equals(Theme, "light", StringComparison.OrdinalIgnoreCase)
        ? HudTheme.Light
        : HudTheme.Dark;

    [JsonIgnore]
    public string ThemeName => ResolvedTheme == HudTheme.Light ? "light" : "dark";

    public HudSettings Normalized() => this with
    {
        Theme = ThemeName,
        Opacity = NormalizeOpacity(Opacity),
        TaskView = NormalizeTaskView(TaskView),
        RecentHours = Math.Clamp(RecentHours, 1, 24),
    };

    /// <summary>Finite, clamped, readable opacity. Never NaN, infinity or off-range.</summary>
    public static double NormalizeOpacity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultOpacity;
        }

        return Math.Clamp(value, MinimumOpacity, MaximumOpacity);
    }

    public static HudTheme ParseTheme(string? value) =>
        string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) ? HudTheme.Light : HudTheme.Dark;
}
