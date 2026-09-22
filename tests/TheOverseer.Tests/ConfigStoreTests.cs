using TheOverseer.Core.Configuration;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public void MissingConfigUsesDefaults()
    {
        var result = MonitorConfigStore.Load(ConfigPath);
        Assert.Equal(ConfigLoadStatus.Missing, result.Status);
        Assert.Equal(64, result.Config.Limits.MaxTrackedFiles);
    }

    [Fact]
    public void MalformedConfigIsPreservedAndRecovered()
    {
        File.WriteAllText(ConfigPath, "{ this is not json ");
        var result = MonitorConfigStore.Load(ConfigPath);

        Assert.Equal(ConfigLoadStatus.Recovered, result.Status);
        Assert.Equal("config-malformed", result.ReasonCode);
        Assert.Equal(64, result.Config.Limits.MaxTrackedFiles);
        Assert.NotEmpty(Directory.GetFiles(_directory, "config.json.corrupt-*"));
    }

    [Fact]
    public void SaveAndReloadRoundTrips()
    {
        MonitorConfigStore.Save(ConfigPath, new MonitorConfig
        {
            CodexHome = _directory,
            AdditionalRoots = new[] { Path.Combine(_directory, "second-root"), "  " },
            Limits = new MonitorLimits { MaxTrackedFiles = 8, StaleLeaseMinutes = 3 },
            Logging = new LoggingSettings { Enabled = false, Level = "warning" },
        });

        var result = MonitorConfigStore.Load(ConfigPath);
        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(_directory, result.Config.CodexHome);
        Assert.Single(result.Config.AdditionalRoots);
        Assert.Equal(8, result.Config.Limits.MaxTrackedFiles);
        Assert.False(result.Config.Logging.Enabled);
    }

    [Fact]
    public void ConfigurationLimitsAreClampedIntoTheBoundedRange()
    {
        var options = MonitorConfigStore.ToOptions(new MonitorConfig
        {
            Limits = new MonitorLimits
            {
                MaxTrackedFiles = 100_000,
                PerFileBytesPerPass = 1,
                GlobalBytesPerPass = int.MaxValue,
                DiscoveryEntriesPerSlice = -5,
                MaxTitleCacheEntries = 1,
                StaleLeaseMinutes = 100_000,
            },
        });

        Assert.Equal(1024, options.MaxTrackedFiles);
        Assert.Equal(4096, options.PerFileBytesPerPass);
        Assert.Equal(64 * 1024 * 1024, options.GlobalBytesPerPass);
        Assert.Equal(64, options.DiscoveryEntriesPerSlice);
        Assert.Equal(32, options.MaxTitleCacheEntries);
        Assert.Equal(TimeSpan.FromHours(24), options.StaleLease);

        // Bounds survive an extreme file.
        var clamped = (MonitorOptions.Default with { MaxTrackedFiles = -1, GlobalBytesPerPass = -1 }).Clamped();
        Assert.Equal(1, clamped.MaxTrackedFiles);
        Assert.Equal(64 * 1024, clamped.GlobalBytesPerPass);
    }

    [Fact]
    public void ProfileCreationHonorsCodexHomeAndAdditionalRoots()
    {
        var profiles = MonitorProfiles.Create(_directory, new[] { Path.Combine(_directory, "extra") });
        Assert.Equal(2, profiles.Count);
        Assert.Equal("codex", profiles[0].Id);
        Assert.Equal(Path.Combine(_directory, "sessions"), profiles[0].SessionsRoot);
        Assert.Equal(Path.Combine(_directory, "extra", "session_index.jsonl"), profiles[1].SessionIndexPath);
        Assert.Equal("extra", profiles[1].Label);
    }

    [Fact]
    public void CodexHomeResolutionPrefersExplicitThenEnvironment()
    {
        var explicitHome = MonitorProfiles.ResolveCodexHome(_directory);
        Assert.Equal(Path.GetFullPath(_directory), explicitHome);

        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", _directory);
            Assert.Equal(Path.GetFullPath(_directory), MonitorProfiles.ResolveCodexHome(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }
}
