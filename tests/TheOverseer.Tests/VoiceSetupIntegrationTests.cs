using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TheOverseer.App.Voice;
using TheOverseer.App.Voice.Speech;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class VoiceSetupIntegrationTests
{
    private static (NotificationCoordinator Coordinator, FakeVoicePlayer Player, ManualTimeProvider Clock) Create(VoiceSettings settings)
    {
        var clock = new ManualTimeProvider(VoiceTestData.Start);
        var player = new FakeVoicePlayer();
        NotificationCoordinator? coordinator = null;
        var manager = new VoiceManager(new FakeVoiceProvider(), player,
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero }, clock,
            candidate => coordinator!.IsStillRelevant(candidate), settings.Muted);
        coordinator = new NotificationCoordinator(new ActivityHistory(), new VoiceEventPolicy(), manager, clock, settings);
        coordinator.Start();
        return (coordinator, player, clock);
    }

    private static async Task WaitForCount(NotificationCoordinator coordinator, int count)
    {
        for (var i = 0; i < 100 && coordinator.Status.SpokenCount < count; i++) await Task.Delay(10);
        Assert.Equal(count, coordinator.Status.SpokenCount);
    }

    [Fact]
    public async Task DisabledEventAndPreviewDoNotCreateBacklogs()
    {
        var settings = new VoiceSettings { Muted = false, Events = new Dictionary<string, bool> { ["permission_required"] = false } };
        var (coordinator, player, clock) = Create(settings);
        await using var owned = coordinator;
        void Request(string turn) => coordinator.OnSnapshot(VoiceTestData.Snapshot(clock.GetUtcNow(),
            new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval, turnId: turn) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, turnId: turn, observedAt: clock.GetUtcNow()) }));
        Request("one");
        coordinator.UpdateSettings(settings with { Events = new Dictionary<string, bool>() });
        coordinator.Pump();
        await Task.Delay(40);
        Assert.Empty(player.Played);
        coordinator.SetPreviewing(true);
        clock.Advance(TimeSpan.FromSeconds(11));
        Request("two");
        coordinator.SetPreviewing(false);
        coordinator.Pump();
        await Task.Delay(40);
        Assert.Empty(player.Played);
        clock.Advance(TimeSpan.FromSeconds(11));
        Request("three");
        await WaitForCount(coordinator, 1);
    }

    [Fact]
    public async Task TerminalMonitorFailureCanAnnounceLossButNeverOldTaskEvents()
    {
        var (coordinator, _, clock) = Create(new VoiceSettings { Muted = false });
        await using var owned = coordinator;
        coordinator.OnSnapshot(VoiceTestData.Snapshot(clock.GetUtcNow()));
        coordinator.OnMonitorFailure("test");
        clock.Advance(TimeSpan.FromSeconds(9)); coordinator.Pump();
        Assert.Equal(0, coordinator.Status.SpokenCount);
        clock.Advance(TimeSpan.FromSeconds(1)); coordinator.Pump();
        await WaitForCount(coordinator, 1);
        clock.Advance(TimeSpan.FromMinutes(1)); coordinator.Pump();
        Assert.Equal(1, coordinator.Status.SpokenCount);
        Assert.False(coordinator.Settings.Muted);
    }

    [Fact]
    public async Task SuspendPreventsDelayedMonitorFailureCue()
    {
        var (coordinator, player, clock) = Create(new VoiceSettings { Muted = false });
        await using var owned = coordinator;
        coordinator.OnMonitorFailure("test");
        coordinator.OnSuspend();
        clock.Advance(TimeSpan.FromMinutes(1)); coordinator.Pump();
        coordinator.OnResume(); coordinator.Pump();
        await Task.Delay(40);
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task DisabledAllCompleteStillAllowsIndividualCompletion()
    {
        var (coordinator, _, clock) = Create(new VoiceSettings { Muted = false,
            Events = new Dictionary<string, bool> { ["all_tasks_complete"] = false } });
        await using var owned = coordinator;
        coordinator.OnSnapshot(VoiceTestData.Snapshot(clock.GetUtcNow(), new[] { VoiceTestData.Task(state: AgentTaskState.Done) },
            new[] { VoiceTestData.Event(AttentionEventKind.TaskCompleted, observedAt: clock.GetUtcNow()), VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, observedAt: clock.GetUtcNow()) }));
        clock.Advance(TimeSpan.FromSeconds(3)); coordinator.Pump();
        await WaitForCount(coordinator, 1);
    }

    [Fact]
    public async Task PreviewUsesExactlyTheSelectedProviderAndNoOther()
    {
        using var home = new SyntheticCodexHome();
        var player = new FakeVoicePlayer();

        foreach (var name in new[] { "pack", "speech", "tts" })
        {
            // Fresh recording fakes per provider so the counters belong to one case.
            var pack = new FakeVoiceProvider("pack");
            var speech = new FakeVoiceProvider("speech");
            var tts = new FakeVoiceProvider("tts");
            var builders = new VoiceProviderBuilders(Pack: (_, _) => pack, Speech: (_, _) => speech, Tts: (_, _, _) => tts);
            var settings = new VoiceSettings { Provider = name, Muted = false, Endpoint = "https://tts.example.invalid/speak" };
            var preview = new VoicePreviewService(home.Root, () => settings, player, builders);

            var outcome = await preview.PreviewAsync(VoiceEventKind.TaskCompleted, CancellationToken.None);

            Assert.True(outcome.ClipCreated, name);
            Assert.True(outcome.Played, name);
            Assert.Equal(name, outcome.Code);
            var fakes = new Dictionary<string, FakeVoiceProvider>(StringComparer.Ordinal)
            {
                ["pack"] = pack,
                ["speech"] = speech,
                ["tts"] = tts,
            };
            foreach (var (id, fake) in fakes)
            {
                Assert.Equal(id == name ? 1 : 0, fake.Calls);
            }
        }

        Assert.Equal(new[] { "pack", "speech", "tts" }, player.Played.Select(clip => clip.SourceCode).ToArray());
    }

    [Fact]
    public async Task AnAssignedRecordingStillWinsOverTheSelectedProvider()
    {
        using var home = new SyntheticCodexHome();
        var source = Path.Combine(home.Root, "source.wav");
        await File.WriteAllBytesAsync(source, VoicePackValidator.CreateTone(300, 0.05));
        var name = await VoiceRecordingStore.ImportAsync(home.Root, source, CancellationToken.None);
        var settings = new VoiceSettings
        {
            Provider = "speech",
            Muted = false,
            Clips = new Dictionary<string, string> { ["task_complete"] = name },
        };
        var speech = new FakeVoiceProvider("speech");
        var builders = new VoiceProviderBuilders(Speech: (_, _) => speech);
        var player = new FakeVoicePlayer();
        var preview = new VoicePreviewService(home.Root, () => settings, player, builders);

        var outcome = await preview.PreviewAsync(VoiceEventKind.TaskCompleted, CancellationToken.None);

        Assert.True(outcome.Played);
        Assert.Equal("recording", outcome.Code);
        Assert.Equal(0, speech.Calls);
    }

    [Fact]
    public async Task AFailingSelectedProviderIsNeverReplacedByAnotherProvider()
    {
        using var home = new SyntheticCodexHome();
        var pack = new FakeVoiceProvider("pack");
        var tts = new FakeVoiceProvider("tts", available: false);
        var builders = new VoiceProviderBuilders(Pack: (_, _) => pack, Tts: (_, _, _) => tts);
        var settings = new VoiceSettings { Provider = "tts", Muted = false, Endpoint = "https://tts.example.invalid/speak" };
        var player = new FakeVoicePlayer();
        var preview = new VoicePreviewService(home.Root, () => settings, player, builders);

        var outcome = await preview.PreviewAsync(VoiceEventKind.TaskError, CancellationToken.None);

        Assert.False(outcome.ClipCreated);
        Assert.False(outcome.Played);
        Assert.Equal("unavailable", outcome.Code);
        Assert.Equal(1, tts.Calls);
        Assert.Equal(0, pack.Calls);
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task PlaybackSpeaksOnlyTheSelectedProvider()
    {
        using var home = new SyntheticCodexHome();
        var pack = new FakeVoiceProvider("pack");
        var speech = new FakeVoiceProvider("speech");
        var builders = new VoiceProviderBuilders(Pack: (_, _) => pack, Speech: (_, _) => speech);
        var settings = new VoiceSettings { Provider = "speech", Muted = false };
        var (coordinator, player, clock) = CreateSelected(settings, home.Root, builders);
        await using var owned = coordinator;

        Request(coordinator, clock);
        await WaitForCount(coordinator, 1);

        Assert.Equal("speech", Assert.Single(player.Played).SourceCode);
        Assert.Equal(1, speech.Calls);
        Assert.Equal(0, pack.Calls);
    }

    [Fact]
    public async Task ChangingTheSelectedProviderTakesEffectForTheNextCue()
    {
        using var home = new SyntheticCodexHome();
        var pack = new FakeVoiceProvider("pack");
        var speech = new FakeVoiceProvider("speech");
        var builders = new VoiceProviderBuilders(Pack: (_, _) => pack, Speech: (_, _) => speech);
        var settings = new VoiceSettings { Provider = "pack", Muted = false };
        var (coordinator, player, clock) = CreateSelected(settings, home.Root, builders);
        await using var owned = coordinator;

        Request(coordinator, clock, "turn-1");
        await WaitForCount(coordinator, 1);
        Assert.Equal("pack", player.Played[0].SourceCode);

        // The user picks the offline baseline in the panel: the very next cue uses it.
        coordinator.UpdateSettings(settings with { Provider = "speech" });
        clock.Advance(TimeSpan.FromSeconds(11));
        Request(coordinator, clock, "turn-2");
        await WaitForCount(coordinator, 2);

        Assert.Equal("speech", player.Played[1].SourceCode);
        Assert.Equal(1, pack.Calls);
        Assert.Equal(1, speech.Calls);
    }

    [Fact]
    public void ReadingProviderStatusNeitherSynthesizesNorSendsARequest()
    {
        using var home = new SyntheticCodexHome();
        var engine = new CountingSpeechEngine();
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var builders = new VoiceProviderBuilders(
            Speech: (_, settings) => new WindowsSpeechVoiceProvider(engine, settings),
            Tts: (_, settings, _) => new TtsEndpointProvider(client, settings));

        foreach (var name in new[] { "speech", "tts" })
        {
            var settings = new VoiceSettings { Provider = name, Muted = false, Endpoint = "https://tts.example.invalid/speak" };
            using var provider = VoiceProviderFactory.Create(home.Root, () => settings, builders);

            var text = VoiceSetupStatus.ProviderRow(settings, provider.Status);

            Assert.False(string.IsNullOrWhiteSpace(text));
        }

        Assert.Equal(0, engine.Calls);
        Assert.Equal(0, handler.RequestCount);
        client.Dispose();
    }

    [Fact]
    public void BaselineAvailabilityIsProbedWithoutSynthesizingAnything()
    {
        var missing = new CountingSpeechEngine { IsAvailable = false, UnavailableCode = "speech-missing" };
        var status = VoiceProviderFactory.DescribeSpeechBaseline(missing);

        Assert.False(status.IsAvailable);
        Assert.Equal("speech-missing", status.LastCode);
        Assert.Equal(0, missing.Calls);

        var ready = VoiceProviderFactory.DescribeSpeechBaseline(new CountingSpeechEngine());
        Assert.True(ready.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(ready.LastCode));
    }

    [Fact]
    public void RowStatusNeverClaimsThePackWhenAnotherProviderIsSelected()
    {
        var speech = VoiceSetupStatus.ProviderRow(
            new VoiceSettings { Provider = "speech" },
            new VoiceProviderStatus("speech", false, "speech-no-english-voice"));
        Assert.DoesNotContain("voice pack", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("speech-no-english-voice", speech, StringComparison.Ordinal);

        var ready = VoiceSetupStatus.ProviderRow(
            new VoiceSettings { Provider = "speech" },
            new VoiceProviderStatus("speech", true, "speech-ok"));
        Assert.DoesNotContain("voice pack", ready, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("baseline", ready, StringComparison.OrdinalIgnoreCase);

        var unconfigured = VoiceSetupStatus.ProviderRow(
            new VoiceSettings { Provider = "tts" },
            new VoiceProviderStatus("tts", false, "no-endpoint"));
        Assert.Contains("not configured", unconfigured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("voice pack", unconfigured, StringComparison.OrdinalIgnoreCase);

        var pack = VoiceSetupStatus.PackRow("example", clipAvailable: true);
        Assert.Contains("voice pack", pack, StringComparison.OrdinalIgnoreCase);

        var missingClip = VoiceSetupStatus.PackRow("example", clipAvailable: false);
        Assert.Contains("No usable recording", missingClip, StringComparison.Ordinal);
        Assert.DoesNotContain("Ready", missingClip, StringComparison.Ordinal);

        var baseline = VoiceSetupStatus.Baseline(new VoiceProviderStatus("speech", false, "speech-no-english-voice"));
        Assert.Contains("speech-no-english-voice", baseline, StringComparison.Ordinal);
        Assert.Contains("unavailable", baseline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("available", VoiceSetupStatus.Baseline(new VoiceProviderStatus("speech", true, "speech-ok")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheVoicePanelOffersProviderChoiceBaselineStatusAndUseBaseline()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var viewModel = new HudViewModel(HudSettings.Default);
            var rows = VoiceEventCatalog.All.Select(item => new VoiceSetupRow(item, item.DefaultEnabled)).ToArray();
            var window = new NotificationSettingsWindow(viewModel, rows);
            try
            {
                var providers = (ComboBox)window.FindName("ProviderBox");
                Assert.Equal(3, providers.Items.Count);
                Assert.Equal(new[] { "pack", "speech", "tts" }, providers.Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag).ToArray());
                Assert.Equal(9, ((ItemsControl)window.FindName("Rows")).Items.Count);

                var requested = new List<string>();
                var baselineRequests = 0;
                var stops = 0;
                window.ProviderChangeRequested += requested.Add;
                window.BaselineRequested += () => baselineRequests++;
                window.StopRequested += () => stops++;

                // Showing the stored provider must not look like a user change.
                window.ShowProvider("speech");
                Assert.Equal("speech", ((ComboBoxItem)providers.SelectedItem).Tag);
                Assert.Empty(requested);

                providers.SelectedIndex = 0;
                Assert.Equal(new[] { "pack" }, requested);

                window.ShowStatus("Windows speech baseline (offline): unavailable — speech-no-english-voice.");
                Assert.Contains("speech-no-english-voice", ((TextBlock)window.FindName("BaselineStatusText")).Text, StringComparison.Ordinal);

                ((Button)window.FindName("UseBaselineButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, baselineRequests);

                var stop = FindButton(window, "Stop preview");
                Assert.NotNull(stop);
                stop!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, stops);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        if (root is Button button && string.Equals(button.Content as string, content, StringComparison.Ordinal))
        {
            return button;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = FindButton(child, content);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static (NotificationCoordinator Coordinator, FakeVoicePlayer Player, ManualTimeProvider Clock) CreateSelected(
        VoiceSettings settings,
        string dataDirectory,
        VoiceProviderBuilders builders)
    {
        var clock = new ManualTimeProvider(VoiceTestData.Start);
        var player = new FakeVoicePlayer();
        NotificationCoordinator? coordinator = null;
        var provider = VoiceProviderFactory.Create(dataDirectory, () => coordinator?.Settings ?? settings, builders);
        var manager = new VoiceManager(provider, player,
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero }, clock,
            candidate => coordinator!.IsStillRelevant(candidate), settings.Muted);
        coordinator = new NotificationCoordinator(new ActivityHistory(), new VoiceEventPolicy(), manager, clock, settings);
        coordinator.Start();
        return (coordinator, player, clock);
    }

    private static void Request(NotificationCoordinator coordinator, ManualTimeProvider clock, string turn = "turn-1") =>
        coordinator.OnSnapshot(VoiceTestData.Snapshot(clock.GetUtcNow(),
            new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval, turnId: turn) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, turnId: turn, observedAt: clock.GetUtcNow()) }));

    /// <summary>Fake engine that only counts requests; it never touches SAPI or audio.</summary>
    private sealed class CountingSpeechEngine : ISpeechSynthesisEngine
    {
        public string Id => "counting-speech";

        public bool IsAvailable { get; set; } = true;

        public string UnavailableCode { get; set; } = "speech-no-english-voice";

        public int Calls { get; private set; }

        public SpeechSynthesisResult Synthesize(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return new SpeechSynthesisResult(VoicePackValidator.CreateTone(440, 0.03), "speech-ok");
        }
    }
}
