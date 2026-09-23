using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

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
}
