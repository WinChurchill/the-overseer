using System.Text.Json;
using TheOverseer.Core.Configuration;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class HudSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));

    public HudSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ConfigPath => Path.Combine(_directory, "config.json");

    private string StatePath => Path.Combine(_directory, "state.json");

    [Fact]
    public void ExistingMonitorConfigLoadsWithHudDefaults()
    {
        File.WriteAllText(ConfigPath, """
            {
              "codexHome": "C:\\codex",
              "limits": { "maxTrackedFiles": 12 },
              "logging": { "enabled": false, "level": "warning" }
            }
            """);

        var result = MonitorConfigStore.Load(ConfigPath);

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal("C:\\codex", result.Config.CodexHome);
        Assert.Equal(12, result.Config.Limits.MaxTrackedFiles);
        Assert.False(result.Config.Logging.Enabled);
        Assert.Equal("dark", result.Config.Hud.ThemeName);
        Assert.True(result.Config.Hud.AlwaysOnTop);
        Assert.Equal(HudSettings.DefaultOpacity, result.Config.Hud.Opacity);
        Assert.False(result.Config.Hud.StartWithWindows);
    }

    [Fact]
    public void HudChangesPreserveMonitorFields()
    {
        var original = new MonitorConfig
        {
            CodexHome = "C:\\codex",
            AdditionalRoots = new[] { "D:\\other" },
            Limits = new MonitorLimits { MaxTrackedFiles = 24, StaleLeaseMinutes = 7 },
            Logging = new LoggingSettings { Enabled = true, Level = "debug", MaxBytes = 2048 },
            RuntimeActivitySourceEnabled = false,
        };
        MonitorConfigStore.Save(ConfigPath, original);

        var loaded = MonitorConfigStore.Load(ConfigPath).Config;
        var updated = loaded with { Hud = new HudSettings { Theme = "light", Opacity = 0.8, AlwaysOnTop = false, StartWithWindows = true } };
        MonitorConfigStore.Save(ConfigPath, updated);

        var reread = MonitorConfigStore.Load(ConfigPath).Config;
        Assert.Equal("C:\\codex", reread.CodexHome);
        Assert.Equal(new[] { "D:\\other" }, reread.AdditionalRoots);
        Assert.Equal(24, reread.Limits.MaxTrackedFiles);
        Assert.Equal(7, reread.Limits.StaleLeaseMinutes);
        Assert.Equal("debug", reread.Logging.Level);
        Assert.False(reread.RuntimeActivitySourceEnabled);
        Assert.Equal("light", reread.Hud.ThemeName);
        Assert.Equal(0.8, reread.Hud.Opacity);
        Assert.False(reread.Hud.AlwaysOnTop);
        Assert.True(reread.Hud.StartWithWindows);
    }

    [Theory]
    [InlineData("solarized", "dark")]
    [InlineData("LIGHT", "light")]
    [InlineData("", "dark")]
    public void UnknownThemesFallBackToASupportedTheme(string configured, string expected)
    {
        var normalized = new HudSettings { Theme = configured }.Normalized();
        Assert.Equal(expected, normalized.ThemeName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.1)]
    [InlineData(4.0)]
    public void OpacityIsAlwaysFiniteAndReadable(double value)
    {
        var normalized = new HudSettings { Opacity = value }.Normalized();
        Assert.InRange(normalized.Opacity, HudSettings.MinimumOpacity, HudSettings.MaximumOpacity);
        Assert.False(double.IsNaN(normalized.Opacity));
        Assert.False(double.IsInfinity(normalized.Opacity));
    }

    [Fact]
    public void MalformedConfigIsRecoveredAndPreserved()
    {
        File.WriteAllText(ConfigPath, "{ \"hud\": { \"opacity\": ");
        var result = MonitorConfigStore.Load(ConfigPath);

        Assert.Equal(ConfigLoadStatus.Recovered, result.Status);
        Assert.Equal("dark", result.Config.Hud.ThemeName);
        Assert.NotEmpty(Directory.GetFiles(_directory, "config.json.corrupt-*"));
    }

    [Fact]
    public void OversizeConfigIsRejectedWithoutCrashing()
    {
        File.WriteAllText(ConfigPath, new string('x', 200 * 1024));
        var result = MonitorConfigStore.Load(ConfigPath);
        Assert.Equal(ConfigLoadStatus.Failed, result.Status);
        Assert.Equal("config-too-large", result.ReasonCode);
        Assert.Equal("dark", result.Config.Hud.ThemeName);
    }

    [Fact]
    public void StateRoundTripsAndNormalizes()
    {
        var state = new HudWindowState
        {
            MonitorDeviceName = @"\\.\DISPLAY1",
            OffsetXDips = 120.5,
            OffsetYDips = -8,
            DpiScale = 1.25,
            IsExpanded = true,
            SavedAtUtc = DateTimeOffset.UnixEpoch,
        };

        Assert.True(HudStateStore.TrySave(StatePath, state));
        var loaded = HudStateStore.Load(StatePath);

        Assert.Equal(HudStateLoadStatus.Loaded, loaded.Status);
        Assert.Equal(@"\\.\DISPLAY1", loaded.State.MonitorDeviceName);
        Assert.Equal(120.5, loaded.State.OffsetXDips);
        Assert.Equal(-8, loaded.State.OffsetYDips);
        Assert.Equal(1.25, loaded.State.DpiScale);
        Assert.True(loaded.State.IsExpanded);
    }

    [Fact]
    public void CorruptAndOversizeStateFallBackToDefaults()
    {
        File.WriteAllText(StatePath, "{ not json");
        var corrupt = HudStateStore.Load(StatePath);
        Assert.Equal(HudStateLoadStatus.Recovered, corrupt.Status);
        Assert.Equal(HudWindowState.Default, corrupt.State);

        File.WriteAllText(StatePath, new string('y', 32 * 1024));
        var oversize = HudStateStore.Load(StatePath);
        Assert.Equal(HudStateLoadStatus.Failed, oversize.Status);
        Assert.Equal("state-too-large", oversize.ReasonCode);

        var missing = HudStateStore.Load(Path.Combine(_directory, "absent.json"));
        Assert.Equal(HudStateLoadStatus.Missing, missing.Status);
    }

    [Fact]
    public void UnknownStateVersionKeepsOnlyExpansion()
    {
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new
        {
            version = 99,
            isExpanded = true,
            monitorDevice = @"\\.\DISPLAY1",
            offsetXDips = 10.0,
            offsetYDips = 10.0,
            dpiScale = 1.0,
        }));

        var loaded = HudStateStore.Load(StatePath);
        Assert.Equal(HudStateLoadStatus.Recovered, loaded.Status);
        Assert.Equal("state-version", loaded.ReasonCode);
        Assert.True(loaded.State.IsExpanded);
        Assert.Equal(string.Empty, loaded.State.MonitorDeviceName);
    }

    [Fact]
    public void StateSaveFailureIsReportedNotThrown()
    {
        var blocked = Path.Combine(_directory, "blocked");
        Directory.CreateDirectory(blocked);
        var path = Path.Combine(blocked, "state.json");

        // A directory occupies the target path, so the atomic move must fail
        // and be reported instead of throwing at the caller.
        Directory.CreateDirectory(path);
        Assert.False(HudStateStore.TrySave(path, HudWindowState.Default));
    }
}
