using System.Windows;
using System.Windows.Controls;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class TaskViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static AgentTask Task(int number, AgentTaskState state, int minutesAgo, bool stale = false) =>
        VoiceTestData.Task(key: VoiceTestData.Key("task-" + number), number: number, state: state, stale: stale,
            needsHuman: state is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput) with
        { LastActivityAt = Now.AddMinutes(-minutesAgo), CompletedAt = state == AgentTaskState.Done ? Now.AddMinutes(-minutesAgo) : null };

    [Fact]
    public void NowHidesOldSettledAndStaleRowsButKeepsCurrentWork()
    {
        var tasks = new[] { Task(1, AgentTaskState.Working, 180), Task(2, AgentTaskState.NeedsApproval, 20),
            Task(3, AgentTaskState.Done, 5), Task(4, AgentTaskState.Done, 60), Task(5, AgentTaskState.Idle, 1),
            Task(6, AgentTaskState.Working, 90, stale: true) };
        var vm = new HudViewModel(HudSettings.Default, sessionStartedAt: Now.AddMinutes(-30));
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, tasks));
        Assert.Equal(new[] { 2, 1, 3 }, vm.Tasks.Select(row => row.Number));
        Assert.Equal("3 shown · 6 tracked", vm.TaskCountLine);
        var counts = vm.WorkingCountText;
        vm.TaskView = "recent";
        Assert.Equal(new[] { 5, 3, 2, 4, 6 }, vm.Tasks.Select(row => row.Number));
        Assert.Equal(counts, vm.WorkingCountText); // filter never changes monitoring counts
    }

    [Fact]
    public void SessionUsesLaunchBoundaryNotHydrationOrViewSwitchTime()
    {
        var vm = new HudViewModel(HudSettings.Default, sessionStartedAt: Now.AddMinutes(-30));
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Done, 31), Task(2, AgentTaskState.Idle, 30) }));
        vm.TaskView = "session";
        Assert.Equal(2, Assert.Single(vm.Tasks).Number);
        vm.TaskView = "recent";
        vm.UpdateAges(Now.AddMinutes(1));
        vm.TaskView = "session";
        Assert.Equal(2, Assert.Single(vm.Tasks).Number);
    }

    [Fact]
    public void TimeWindowsExpireWithoutAnotherSnapshotAndKeepUnknownHealth()
    {
        var vm = new HudViewModel(new HudSettings { TaskView = "recent", RecentHours = 1 });
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Done, 59) }));
        Assert.Single(vm.Tasks);
        vm.UpdateAges(Now.AddMinutes(2));
        Assert.Empty(vm.Tasks);
        vm.NotifyMonitorFailure("lost");
        vm.TaskView = "now";
        vm.UpdateAges(Now.AddMinutes(3));
        Assert.Empty(vm.Tasks);
        Assert.Equal("?", vm.WorkingCountText);
        Assert.Equal("unavailable", vm.StatusKind);
    }

    [Fact]
    public void RecentCompletionLeavesNowAfterTenMinutes()
    {
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Done, 9) }));
        Assert.Single(vm.Tasks);
        vm.UpdateAges(Now.AddMinutes(2));
        Assert.Empty(vm.Tasks);
    }

    [Fact]
    public void UnsupportedAttentionShowsUnknownRatherThanReassuringZero()
    {
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(1, AgentTaskState.Working, 0) }));
        Assert.Equal("?", vm.NeedsHumanCountText);
        Assert.Equal("working", vm.StatusKind);
        Assert.Contains("limited", vm.AttentionDisclosure);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(2, AgentTaskState.NeedsApproval, 0) }));
        Assert.Equal("1+", vm.NeedsHumanCountText);
        Assert.Equal("attention", vm.StatusKind);
        Assert.Equal("Needs you", vm.StatusHeadline);
    }

    [Fact]
    public void ViewPreferencesRoundTripAndNormalizeWithoutChangingMonitorSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            MonitorConfigStore.Save(path, MonitorConfig.Default with { Hud = new HudSettings { TaskView = "session", RecentHours = 8 } });
            var config = MonitorConfigStore.Load(path).Config;
            Assert.Equal("session", config.Hud.TaskView);
            Assert.Equal(8, config.Hud.RecentHours);
            Assert.Equal("now", new HudSettings { TaskView = "nonsense" }.Normalized().TaskView);
            Assert.Equal(24, new HudSettings { RecentHours = 500 }.Normalized().RecentHours);
            var vm = new HudViewModel(config.Hud);
            Assert.True(vm.IsSessionView);
            var changes = 0;
            vm.SettingsChanged += (_, _) => changes++;
            vm.IsRecentView = true;
            // The Recent window is chosen explicitly now; the old cycling gesture is
            // gone, so the selector is the only thing that changes it.
            vm.SelectedRecentHours = 24;
            Assert.Equal(24, vm.RecentHours);
            Assert.Equal(2, changes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ElapsedTurnTimeSurvivesActionChangesAndResetsOnlyForANewTurn()
    {
        var task = Task(1, AgentTaskState.Working, 0) with { StartedAt = Now.AddMinutes(-5) };
        var row = new TaskRowViewModel(task, false, "");
        row.UpdateAge(Now, task.LastActivityAt);
        Assert.Equal("5m", row.AgeText);
        row.Update(task with { Action = new AgentAction(AgentActionKind.EditingFiles, EvidenceKind.Structured), LastActivityAt = Now.AddSeconds(10) });
        row.UpdateAge(Now.AddSeconds(10), Now.AddSeconds(10));
        Assert.Equal("5m", row.AgeText);
        Assert.Contains("Last activity: 0s ago", row.AgeHint);
        row.Update(task with { TurnId = "new-turn", StartedAt = Now.AddSeconds(10) });
        row.UpdateAge(Now.AddSeconds(11), Now.AddSeconds(10));
        Assert.Equal("1s", row.AgeText);
        row.Update(task with { TurnId = "unobserved-start", StartedAt = null });
        row.UpdateAge(Now, Now);
        Assert.Equal("—", row.AgeText);
        Assert.Contains("not observed", row.AgeHint);
    }

    [Fact]
    public void IncompleteZeroExplainsUnreadHistoryWithoutClaimingIdle()
    {
        var vm = new HudViewModel(HudSettings.Default);
        var snapshot = VoiceTestData.Snapshot(Now, working: CountValue.Partial(0), coverageComplete: false);
        snapshot = snapshot with { Health = snapshot.Health with { Reasons = new[] { HealthReasons.HydrationBudget } } };
        vm.ApplySnapshot(snapshot);
        Assert.Equal("Limited visibility", vm.StatusHeadline);
        Assert.Contains("Limited coverage", vm.MonitoringBadgeText);
        Assert.Contains("unread", vm.StatusDetails);
        Assert.Equal("uncertain", vm.StatusKind);
        Assert.Equal("0+", vm.WorkingCountText);
        Assert.Contains("unread", vm.MonitoringDisclosure);
        Assert.Contains("may miss work", vm.MonitoringDisclosure);
        vm.NotifyMonitorFailure("test");
        Assert.Contains("unknown", vm.MonitoringDisclosure);
        Assert.Equal("?", vm.WorkingCountText);
    }

    [Fact]
    public void WindowIsFindableWithoutAutomaticallyActivatingAndTabsAreBound()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var vm = new HudViewModel(HudSettings.Default);
            var window = new HudWindow { DataContext = vm, Width = 360, Height = 440 };
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.True(window.ShowInTaskbar);
                Assert.False(window.ShowActivated);
                ((RadioButton)window.FindName("SessionTab")!).IsChecked = true;
                Assert.Equal("session", vm.TaskView);
                ((RadioButton)window.FindName("RecentTab")!).IsChecked = true;
                Assert.Equal("recent", vm.TaskView);
            }
            finally { window.Close(); }
        });
    }
}
