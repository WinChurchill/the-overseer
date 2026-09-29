using System.ComponentModel;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>
/// Regressions for the stage-one UI contract defects found by the carryover audit:
/// a selector that never re-notified, an accent that ignored the light toggle, a task
/// history strip without the shared axis, a voice failure nothing rendered, and status
/// feedback that reached no surface.
/// </summary>
public sealed class UiContractCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VoiceFailureBindingsFollowMuteAndCombinedStatusChanges()
    {
        StaTestHost.Invoke(() =>
        {
            var vm = new HudViewModel(HudSettings.Default);
            var text = new System.Windows.Controls.TextBlock();
            System.Windows.Data.BindingOperations.SetBinding(text,
                System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(HudViewModel.VoiceFailureLine)) { Source = vm });
            vm.ApplyVoiceStatus(FailedVoice());
            Assert.Equal(vm.VoiceFailureLine, text.Text);
            Assert.NotEmpty(text.Text);
            vm.IsMuted = true;
            Assert.Empty(text.Text);
            vm.IsMuted = false;
            Assert.NotEmpty(text.Text);
            vm.ApplyVoiceStatus(WorkingVoice() with { IsMuted = true });
            Assert.Empty(text.Text);
            vm.ApplyVoiceStatus(FailedVoice());
            Assert.Equal(vm.VoiceFailureLine, text.Text);
        });
    }

    private static AgentTask Task(int number, AgentTaskState state, int minutesAgo) =>
        VoiceTestData.Task(key: VoiceTestData.Key("task-" + number), number: number, state: state)
            with { LastActivityAt = Now.AddMinutes(-minutesAgo) };

    private static VoiceStatus FailedVoice() => new(
        IsMuted: false,
        ProviderId: "speech",
        LastOutcomeCode: "provider-unavailable",
        QueuedCount: 0,
        SpokenCount: 0,
        DroppedCount: 0,
        FailedCount: 1,
        FallbackCount: 0,
        IsSpeaking: false,
        Detail: "Voice unavailable: provider-unavailable.");

    private static VoiceStatus WorkingVoice() => new(
        IsMuted: false,
        ProviderId: "speech",
        LastOutcomeCode: "spoken",
        QueuedCount: 0,
        SpokenCount: 3,
        DroppedCount: 0,
        FailedCount: 0,
        FallbackCount: 0,
        IsSpeaking: false,
        Detail: "Spoken 3.");

    [Fact]
    public void ChangingRecentHoursFromTheMenuNotifiesTheBoundSelector()
    {
        var vm = new HudViewModel(new HudSettings { RecentHours = 2 });
        var raised = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        Assert.Equal(2, vm.SelectedRecentHours);

        // What the header menu does: set the window behind the ComboBox's back.
        vm.RecentHours = 8;

        Assert.Equal(8, vm.SelectedRecentHours);
        Assert.Contains(nameof(HudViewModel.SelectedRecentHours), raised);
        Assert.Contains(nameof(HudViewModel.RecentWindowLabel), raised);

        // Re-selecting the value now shown is a no-op rather than a silent desync.
        vm.SelectedRecentHours = 8;
        Assert.Equal(8, vm.RecentHours);
    }

    [Fact]
    public void LeftAccentFollowsTheLightToggleBothWays()
    {
        var vm = new HudViewModel(HudSettings.Default);
        Assert.True(vm.LightVisible, "the light starts visible");
        Assert.False(vm.LeftAccentVisible, "the accent is redundant while the light carries state");

        var raised = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.ToggleLightCommand.Execute(null);
        Assert.False(vm.LightVisible);
        Assert.True(vm.LeftAccentVisible, "turning the light off must reveal the accent");
        Assert.Contains(nameof(HudViewModel.LeftAccentVisible), raised);

        raised.Clear();
        vm.ToggleLightCommand.Execute(null);
        Assert.True(vm.LightVisible);
        Assert.False(vm.LeftAccentVisible, "turning the light back on must hide the redundant accent");
        Assert.Contains(nameof(HudViewModel.LeftAccentVisible), raised);
    }

    [Fact]
    public void TaskOnlyHistoryStillShowsTheSharedAxisAndLegend()
    {
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Working, 1) }));

        var row = Assert.Single(vm.Tasks);
        Assert.False(vm.ShowHistoryLegend, "a closed history shows no axis");
        Assert.False(vm.AnyTaskHistoryOpen);

        var raised = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        // The task strip is reachable with every project history closed.
        row.IsHistoryOpen = true;

        Assert.True(vm.AnyTaskHistoryOpen);
        Assert.True(vm.ShowHistoryLegend, "the task strip uses the same axis and legend as a project strip");
        Assert.Contains(nameof(HudViewModel.ShowHistoryLegend), raised);
        Assert.Contains(nameof(HudViewModel.AnyTaskHistoryOpen), raised);

        raised.Clear();
        row.IsHistoryOpen = false;
        Assert.False(vm.AnyTaskHistoryOpen);
        Assert.False(vm.ShowHistoryLegend);
        Assert.Contains(nameof(HudViewModel.ShowHistoryLegend), raised);
    }

    [Fact]
    public void ProjectHistoryStillDrivesTheLegend()
    {
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Working, 1) }));

        var project = Assert.Single(vm.Projects);
        project.IsHistoryOpen = true;

        Assert.True(vm.AnyProjectHistoryOpen);
        Assert.True(vm.ShowHistoryLegend);
    }

    [Fact]
    public void AVoiceFailureIsRenderableButNothingAppearsWhileVoiceIsMutedOrWorking()
    {
        var vm = new HudViewModel(HudSettings.Default);

        // Muted (the first-run default): never a failure banner.
        vm.ApplyVoiceStatus(VoiceStatus.Initial);
        Assert.True(vm.IsMuted);
        Assert.False(vm.VoiceFailed);
        Assert.Empty(vm.VoiceFailureLine);

        // Enabled and working: still nothing.
        vm.ApplyVoiceStatus(WorkingVoice());
        Assert.False(vm.IsMuted);
        Assert.False(vm.VoiceFailed);
        Assert.Empty(vm.VoiceFailureLine);

        // Enabled and failing: the failure has to be visible somewhere.
        var raised = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        vm.ApplyVoiceStatus(FailedVoice());

        Assert.True(vm.VoiceFailed);
        Assert.Equal("Voice unavailable: provider-unavailable.", vm.VoiceFailureLine);
        Assert.Contains(nameof(HudViewModel.VoiceFailed), raised);
        Assert.Contains(nameof(HudViewModel.VoiceFailureLine), raised);
    }

    [Fact]
    public void MutingClearsAnExistingVoiceFailureLine()
    {
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplyVoiceStatus(FailedVoice());
        Assert.True(vm.VoiceFailed);

        vm.IsMuted = true;

        Assert.False(vm.VoiceFailed);
        Assert.Empty(vm.VoiceFailureLine);
    }

    [Fact]
    public void StatusFeedbackIsRenderedAndCleared()
    {
        var vm = new HudViewModel(HudSettings.Default);
        Assert.False(vm.HasStatus);

        var raised = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.NotifyStatus("Settings or position could not be saved.");

        Assert.True(vm.HasStatus);
        Assert.Equal("Settings or position could not be saved.", vm.FooterHint);
        Assert.Contains(nameof(HudViewModel.HasStatus), raised);
        Assert.Contains(nameof(HudViewModel.FooterHint), raised);

        raised.Clear();
        vm.ClearStatus();
        Assert.False(vm.HasStatus);
        Assert.NotEqual("Settings or position could not be saved.", vm.FooterHint);
        Assert.Contains(nameof(HudViewModel.HasStatus), raised);
    }

    [Fact]
    public void ANotificationAfterASnapshotStillSurfacesOnTheFooter()
    {
        // A snapshot re-projects the footer; a later status must win, because a failed
        // save that a background refresh erases is not discoverable.
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Working, 1) }));

        vm.NotifyStatus("Task is no longer in view.");

        Assert.True(vm.HasStatus);
        Assert.Equal("Task is no longer in view.", vm.FooterHint);

        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Working, 1) }));
        Assert.Equal("Task is no longer in view.", vm.FooterHint);
    }

    [Fact]
    public void TheDiagnosticsCheckCanBePointedAtADisposableDataDirectory()
    {
        // The composition check composes the real graph and lets a real controller save
        // its state, so it must be possible to redirect it away from the live directory.
        var real = DataDirectoryResolver.Resolve(null);
        var isolated = Path.Combine(Path.GetTempPath(), "overseer-composition-test-" + Guid.NewGuid().ToString("N")[..8]);

        var live = Parse("--hud-composition-check", "--data-directory", real);
        var disposable = Parse("--hud-composition-check", "--data-directory", isolated);

        Assert.True(live.HudCompositionCheck);
        Assert.True(live.IsFixtureInvocation, "a diagnostics mode must not take the single-instance slot");
        Assert.Equal(real, DataDirectoryResolver.Resolve(live.DataDirectory));
        Assert.Equal(isolated, DataDirectoryResolver.Resolve(disposable.DataDirectory));
        Assert.NotEqual(
            DataDirectoryResolver.Resolve(live.DataDirectory),
            DataDirectoryResolver.Resolve(disposable.DataDirectory));
    }

    private static StartupArguments Parse(params string[] args)
    {
        Assert.True(StartupArguments.TryParse(args, out var parsed, out var error), error);
        return parsed;
    }
}
