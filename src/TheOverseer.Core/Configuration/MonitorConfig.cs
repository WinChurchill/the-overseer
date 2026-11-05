using System.Text.Json.Serialization;

namespace TheOverseer.Core.Configuration;

/// <summary>
/// Minimal user configuration. Unknown JSON members are ignored and every
/// limit is clamped, so a corrupt or hostile file cannot remove a bound.
/// </summary>
public sealed record MonitorConfig
{
    public static MonitorConfig Default { get; } = new();

    [JsonPropertyName("codexHome")]
    public string? CodexHome { get; init; }

    [JsonPropertyName("additionalRoots")]
    public IReadOnlyList<string> AdditionalRoots { get; init; } = Array.Empty<string>();

    [JsonPropertyName("limits")]
    public MonitorLimits Limits { get; init; } = new();

    [JsonPropertyName("logging")]
    public LoggingSettings Logging { get; init; } = new();

    [JsonPropertyName("runtimeActivitySource")]
    public bool RuntimeActivitySourceEnabled { get; init; } = true;

    /// <summary>
    /// Additive HUD preferences. Optional: an existing monitor-only config keeps
    /// loading and UI updates preserve every monitor field.
    /// </summary>
    [JsonPropertyName("hud")]
    public HudSettings Hud { get; init; } = new();

    /// <summary>
    /// Additive voice preferences. Optional: an existing config keeps loading and
    /// voice stays muted with the local pack provider until the user opts in.
    /// </summary>
    [JsonPropertyName("voice")]
    public VoiceSettings Voice { get; init; } = new();

    /// <summary>
    /// Additive project settings: root mappings, path aliases and cache bounds.
    /// Absent settings leave every task resolvable by repository/workspace evidence
    /// alone, and roots are never written to logs or exports.
    /// </summary>
    [JsonPropertyName("project")]
    public ProjectSettings Project { get; init; } = new();

    /// <summary>
    /// Discovered harness bridge sources to leave out, as "harness/sourceInstance".
    /// A source that was never installed is absent by default; this only disables
    /// one that is present, and a disabled source is excluded from monitored totals.
    /// </summary>
    [JsonPropertyName("disabledHarnessSources")]
    public IReadOnlyList<string> DisabledHarnessSources { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Extra bridge roots to scan, as { harness, path }. Only directories the user
    /// explicitly names are read, and discovery stays bounded (see
    /// <see cref="MaximumHarnessSources"/>).
    /// </summary>
    [JsonPropertyName("harnessRoots")]
    public IReadOnlyList<HarnessRootSetting> HarnessRoots { get; init; } = Array.Empty<HarnessRootSetting>();

    /// <summary>
    /// Most bridge source instances to monitor at once. Clamped: a hostile or
    /// accidental value can never raise the aggregate resource bound.
    /// </summary>
    [JsonPropertyName("maximumHarnessSources")]
    public int MaximumHarnessSources { get; init; } = 8;

    /// <summary>Read status metadata from open Claude Projects overviews. Opt-in.</summary>
    [JsonPropertyName("claudeProjectsEnabled")]
    public bool ClaudeProjectsEnabled { get; init; }
}

public sealed record MonitorLimits
{
    [JsonPropertyName("maxTrackedFiles")]
    public int MaxTrackedFiles { get; init; } = 64;

    [JsonPropertyName("perFileBytesPerPass")]
    public int PerFileBytesPerPass { get; init; } = 256 * 1024;

    [JsonPropertyName("globalBytesPerPass")]
    public int GlobalBytesPerPass { get; init; } = 4 * 1024 * 1024;

    [JsonPropertyName("staleLeaseMinutes")]
    public int StaleLeaseMinutes { get; init; } = 15;

    [JsonPropertyName("discoveryEntriesPerSlice")]
    public int DiscoveryEntriesPerSlice { get; init; } = 2048;

    [JsonPropertyName("maxTitleCacheEntries")]
    public int MaxTitleCacheEntries { get; init; } = 512;
}

public sealed record LoggingSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("maxBytes")]
    public int MaxBytes { get; init; } = 1024 * 1024;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "info";
}

public enum ConfigLoadStatus
{
    Loaded,
    Missing,
    Recovered,
    Invalid,
    Failed,
}

public sealed record ConfigLoadResult(MonitorConfig Config, ConfigLoadStatus Status, string ReasonCode);
