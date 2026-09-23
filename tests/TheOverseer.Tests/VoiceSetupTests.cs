using System.Windows;
using System.Windows.Controls;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class VoiceSetupTests
{
    [Theory]
    [InlineData("right", 12, 0)]
    [InlineData("left", -12, 0)]
    [InlineData("top", 0, -12)]
    [InlineData("bottom", 0, 12)]
    public void LightSurfaceDragResizesWithBounds(string position, double x, double y)
    {
        StaTestHost.Invoke(() =>
        {
            var vm = new HudViewModel(HudSettings.Default with { LightPosition = position });
            var window = new HudWindow { DataContext = vm };
            try
            {
                var grip = (System.Windows.Controls.Primitives.Thumb)window.FindName("LightResizeGrip");
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(x, y) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
                Assert.Equal(44, vm.LightSize);
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(x * 100, y * 100) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
                Assert.Equal(128, vm.LightSize);
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(-x * 100, -y * 100) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
                Assert.Equal(6, vm.LightSize);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RecordingWrapperPreservesTtsFallbackDiagnostics()
    {
        using var home = new SyntheticCodexHome();
        var fallback = new FallbackVoiceProvider(new FakeVoiceProvider(available: false), new FakeVoiceProvider(), true);
        using var provider = new AssignedVoiceProvider(home.Root, () => VoiceSettings.Default, fallback);
        var candidate = new VoiceCandidate(VoiceEventKind.TaskCompleted, new TaskKey("codex", "test"), 1, "Task 1", "turn", DateTimeOffset.UtcNow, "test", false, 1, "");
        Assert.NotNull(await provider.TryCreateAsync(candidate, CancellationToken.None));
        Assert.Equal(1, provider.Status.FallbackCount);
    }

    [Fact]
    public async Task ImportedClipIsCopiedValidatedAndOverridesProviderWithoutNetwork()
    {
        using var home = new SyntheticCodexHome();
        var source = Path.Combine(home.Root, "source.wav");
        var bytes = VoicePackValidator.CreateTone(400, .05);
        await File.WriteAllBytesAsync(source, bytes);
        var name = await VoiceRecordingStore.ImportAsync(home.Root, source, CancellationToken.None);
        File.Delete(source);
        var settings = VoiceSettings.Default with { Clips = new Dictionary<string, string> { ["input_required"] = name } };
        var fallback = new FakeVoiceProvider();
        using var provider = new AssignedVoiceProvider(home.Root, () => settings, fallback);
        var candidate = new VoiceCandidate(VoiceEventKind.InputRequired, new TaskKey("codex", "test"), 1, "Task 1", "turn", DateTimeOffset.UtcNow, "test", false, 1, "");
        var clip = await provider.TryCreateAsync(candidate, CancellationToken.None);
        Assert.NotNull(clip);
        Assert.Equal(bytes, clip.Wav);
        Assert.Equal(0, fallback.Calls);
        File.WriteAllText(Path.Combine(home.Root, "voiceclips", name), "not audio");
        Assert.Null(await provider.TryCreateAsync(candidate, CancellationToken.None));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task InvalidOrOversizedImportCreatesNoRecording()
    {
        using var home = new SyntheticCodexHome();
        var source = Path.Combine(home.Root, "bad.wav");
        await File.WriteAllTextAsync(source, "not audio");
        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceRecordingStore.ImportAsync(home.Root, source, CancellationToken.None));
        await File.WriteAllBytesAsync(source, new byte[VoicePackValidator.MaximumClipBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceRecordingStore.ImportAsync(home.Root, source, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(home.Root, "voiceclips")));
    }

    [Fact]
    public void SettingsRoundTripKeepsEventSwitchesClipsAndLightPreferences()
    {
        using var home = new SyntheticCodexHome();
        var path = Path.Combine(home.Root, "config.json");
        MonitorConfigStore.Save(path, MonitorConfig.Default with
        {
            Hud = HudSettings.Default with { LightPosition = "RIGHT", LightSize = 999 },
            Voice = VoiceSettings.Default with
            {
                Events = new Dictionary<string, bool> { ["system_online"] = true, ["task_complete"] = false },
                Clips = new Dictionary<string, string> { ["system_online"] = "hello.wav", ["task_complete"] = "../escape.wav" },
            },
        });
        var loaded = MonitorConfigStore.Load(path).Config;
        Assert.Equal("right", loaded.Hud.LightPosition);
        Assert.Equal(128, loaded.Hud.LightSize);
        Assert.True(loaded.Voice.IsEnabled(VoiceEventKind.SystemOnline));
        Assert.False(loaded.Voice.IsEnabled(VoiceEventKind.TaskCompleted));
        Assert.Single(loaded.Voice.Clips);
        Assert.Equal("hello.wav", loaded.Voice.Clips["system_online"]);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("left")]
    [InlineData("right")]
    public void LightUsesSeparateSpaceAndSettingsListContainsNineRows(string position)
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var vm = new HudViewModel(HudSettings.Default with { LightPosition = position, LightSize = 18 });
            var window = new HudWindow { DataContext = vm, Width = 280 + vm.LightExtraWidth, Height = 72 + vm.LightExtraHeight };
            window.ShowActivated = false;
            try
            {
                window.Show(); window.UpdateLayout();
                var light = (Border)window.FindName("StatusLight");
                Assert.Equal(Visibility.Visible, light.Visibility);
                // Layout rounds DIPs to the monitor's physical pixel grid.
                Assert.InRange(position is "left" or "right" ? light.ActualWidth : light.ActualHeight, 17, 19);
                var header = (Grid)window.FindName("DragRegion");
                Assert.True(header.ActualHeight > 40);
                var rows = VoiceEventCatalog.All.Select(item => new VoiceSetupRow(item, item.DefaultEnabled)).ToArray();
                var settings = new NotificationSettingsWindow(vm, rows);
                Assert.Equal(9, ((ItemsControl)settings.FindName("Rows")).Items.Count);
                settings.Close();
            }
            finally { window.Close(); }
        });
    }
}
