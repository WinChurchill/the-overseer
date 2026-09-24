using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class VoiceSettingsTests : IDisposable
{
    private readonly SyntheticCodexHome _home = new();

    public void Dispose() => _home.Dispose();

    private string ConfigPath => Path.Combine(_home.Root, "config.json");

    [Fact]
    public void VoiceDefaultsAreMutedAndLocal()
    {
        var settings = VoiceSettings.Default;
        Assert.True(settings.Muted);
        Assert.Equal(VoiceProviderKind.Pack, settings.ResolvedProvider);
        Assert.Equal("pack", settings.ProviderName);
        Assert.Equal(VoiceSettings.DefaultPackId, settings.PackId);
        Assert.Null(settings.Endpoint);
        Assert.Equal(VoiceSettings.DefaultTimeoutSeconds, settings.TimeoutSeconds);
        Assert.False(settings.HasUsableEndpoint);
    }

    [Theory]
    [InlineData("tts", "tts")]
    [InlineData("TTS", "tts")]
    [InlineData("pack", "pack")]
    [InlineData("other", "pack")]
    [InlineData("", "pack")]
    public void ProviderIsNormalized(string configured, string expected)
    {
        var settings = new VoiceSettings { Provider = configured }.Normalized();
        Assert.Equal(expected, settings.ProviderName);
    }

    [Theory]
    [InlineData("../escape", "example")]
    [InlineData("a/b", "example")]
    [InlineData("", "example")]
    [InlineData("example", "example")]
    [InlineData("my-pack_2", "my-pack_2")]
    public void PackIdIsNormalizedToASafeSegment(string configured, string expected)
    {
        Assert.Equal(expected, new VoiceSettings { PackId = configured }.Normalized().PackId);
    }

    [Theory]
    [InlineData("https://tts.example.com/speak", true)]
    [InlineData("http://127.0.0.1:8123/tts", true)]
    [InlineData("http://localhost:8123/tts", true)]
    [InlineData("http://tts.example.com/speak", false)]
    [InlineData("ftp://tts.example.com/speak", false)]
    [InlineData("ws://127.0.0.1:8123", false)]
    [InlineData("https://user:pass@tts.example.com/x", false)]
    [InlineData("https://tts.example.com/x#frag", false)]
    [InlineData("not a uri", false)]
    [InlineData("", false)]
    public void EndpointValidationIsStrict(string configured, bool accepted)
    {
        var settings = new VoiceSettings { Endpoint = configured }.Normalized();
        Assert.Equal(accepted, settings.Endpoint is not null);
    }

    [Fact]
    public void OversizedEndpointIsRejected()
    {
        var endpoint = "https://tts.example.com/" + new string('x', VoiceSettings.MaximumEndpointChars);
        Assert.Null(new VoiceSettings { Endpoint = endpoint }.Normalized().Endpoint);
    }

    [Theory]
    [InlineData(0.1, 1.0)]
    [InlineData(30, 15.0)]
    [InlineData(double.NaN, VoiceSettings.DefaultTimeoutSeconds)]
    [InlineData(double.PositiveInfinity, VoiceSettings.DefaultTimeoutSeconds)]
    [InlineData(5, 5.0)]
    public void TimeoutIsClampedAndFinite(double configured, double expected)
    {
        Assert.Equal(expected, new VoiceSettings { TimeoutSeconds = configured }.Normalized().TimeoutSeconds);
    }

    [Fact]
    public void VoiceSettingsRoundTripAndPreserveEveryOtherField()
    {
        var config = new MonitorConfig
        {
            CodexHome = "C:\\codex",
            AdditionalRoots = new[] { "D:\\other" },
            Limits = new MonitorLimits { MaxTrackedFiles = 24 },
            Logging = new LoggingSettings { Enabled = false, Level = "warning" },
            RuntimeActivitySourceEnabled = false,
            Hud = new HudSettings { Theme = "light", Opacity = 0.8, AlwaysOnTop = false, StartWithWindows = true },
            Voice = new VoiceSettings { Muted = false, Provider = "tts", PackId = "example", Endpoint = "https://tts.example.com/speak", TimeoutSeconds = 3 },
        };

        MonitorConfigStore.Save(ConfigPath, config);
        var loaded = MonitorConfigStore.Load(ConfigPath);

        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
        Assert.Equal("C:\\codex", loaded.Config.CodexHome);
        Assert.Equal(24, loaded.Config.Limits.MaxTrackedFiles);
        Assert.False(loaded.Config.Logging.Enabled);
        Assert.False(loaded.Config.RuntimeActivitySourceEnabled);
        Assert.Equal("light", loaded.Config.Hud.ThemeName);
        Assert.Equal(0.8, loaded.Config.Hud.Opacity);
        Assert.False(loaded.Config.Voice.Muted);
        Assert.Equal("tts", loaded.Config.Voice.ProviderName);
        Assert.Equal("https://tts.example.com/speak", loaded.Config.Voice.Endpoint);
        Assert.Equal(3, loaded.Config.Voice.TimeoutSeconds);
    }

    [Fact]
    public void ExistingConfigWithoutVoiceStillLoadsMuted()
    {
        File.WriteAllText(ConfigPath, """
            { "codexHome": "C:\\codex", "hud": { "theme": "dark" } }
            """);

        var loaded = MonitorConfigStore.Load(ConfigPath);
        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
        Assert.True(loaded.Config.Voice.Muted);
        Assert.Equal("pack", loaded.Config.Voice.ProviderName);
        Assert.Null(loaded.Config.Voice.Endpoint);
    }

    [Fact]
    public void HidingVoiceSettingsToNullFallsBackToDefaults()
    {
        File.WriteAllText(ConfigPath, """
            { "voice": { "provider": "tts", "packId": "../bad", "endpoint": "http://evil.example.com/x", "timeoutSeconds": 99 } }
            """);

        var loaded = MonitorConfigStore.Load(ConfigPath);
        Assert.Equal("tts", loaded.Config.Voice.ProviderName);
        Assert.Equal("example", loaded.Config.Voice.PackId);
        Assert.Null(loaded.Config.Voice.Endpoint);
        Assert.Equal(15, loaded.Config.Voice.TimeoutSeconds);
        Assert.True(loaded.Config.Voice.Muted);
    }

    [Fact]
    public void TheStoredSerializationDefaultStaysPackSoExistingFilesKeepTheirMeaning()
    {
        // VoiceSettings.Default is what an existing config.json without a "voice"
        // or "provider" member deserializes to; only a directory with no config file
        // at all may take the new offline baseline default.
        Assert.Equal(VoiceProviderKind.Pack, VoiceSettings.Default.ResolvedProvider);
        Assert.True(VoiceSettings.Default.Muted);

        Assert.Equal(VoiceProviderKind.Speech, VoiceSettings.BaselineDefault.ResolvedProvider);
        Assert.Equal("speech", VoiceSettings.BaselineDefault.ProviderName);
        Assert.True(VoiceSettings.BaselineDefault.Muted);
    }

    [Fact]
    public void ABrandNewDataDirectorySelectsTheOfflineBaselineAndStaysMuted()
    {
        var missingPath = Path.Combine(_home.Root, "brand-new", "config.json");
        Assert.False(File.Exists(missingPath));

        var loaded = MonitorConfigStore.Load(missingPath);
        Assert.Equal(ConfigLoadStatus.Missing, loaded.Status);

        var effective = VoiceSettings.Effective(loaded.Status, loaded.Config.Voice);

        Assert.Equal(VoiceProviderKind.Speech, effective.ResolvedProvider);
        Assert.Equal("speech", effective.ProviderName);
        Assert.True(effective.Muted);
        Assert.Equal(VoiceSettings.DefaultPackId, effective.PackId);
        Assert.Null(effective.Endpoint);
        Assert.Empty(effective.Clips);
        Assert.All(VoiceEventCatalog.All, item => Assert.Equal(item.DefaultEnabled, effective.IsEnabled(item.Kind)));
    }

    [Fact]
    public void AnExistingConfigWithoutAProviderKeyKeepsThePackBehaviour()
    {
        File.WriteAllText(ConfigPath, """
            { "codexHome": "C:\\codex" }
            """);

        var loaded = MonitorConfigStore.Load(ConfigPath);
        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
        Assert.Equal("pack", loaded.Config.Voice.ProviderName);

        var effective = VoiceSettings.Effective(loaded.Status, loaded.Config.Voice);

        Assert.Equal(VoiceProviderKind.Pack, effective.ResolvedProvider);
        Assert.Equal("pack", effective.ProviderName);
        Assert.True(effective.Muted);
    }

    [Fact]
    public void ExplicitStoredVoicePreferencesSurviveTheBaselineDefault()
    {
        File.WriteAllText(ConfigPath, """
            {
              "voice": {
                "muted": false,
                "provider": "tts",
                "packId": "my-pack_2",
                "endpoint": "https://tts.example.com/speak",
                "timeoutSeconds": 3,
                "events": { "system_online": true, "task_complete": false },
                "clips": { "task_error": "error.wav" }
              }
            }
            """);

        var loaded = MonitorConfigStore.Load(ConfigPath);
        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);

        var effective = VoiceSettings.Effective(loaded.Status, loaded.Config.Voice);

        Assert.False(effective.Muted);
        Assert.Equal(VoiceProviderKind.Tts, effective.ResolvedProvider);
        Assert.Equal("my-pack_2", effective.PackId);
        Assert.Equal("https://tts.example.com/speak", effective.Endpoint);
        Assert.Equal(3, effective.TimeoutSeconds);
        Assert.True(effective.IsEnabled(VoiceEventKind.SystemOnline));
        Assert.False(effective.IsEnabled(VoiceEventKind.TaskCompleted));
        Assert.Equal("error.wav", effective.Clips["task_error"]);
    }
}
