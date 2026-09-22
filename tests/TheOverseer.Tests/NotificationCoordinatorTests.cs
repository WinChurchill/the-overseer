using System.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class NotificationCoordinatorTests
{
    /// <summary>
    /// Sample time near the real clock: the voice manager judges candidate TTL
    /// against the wall clock, exactly as production does.
    /// </summary>
    private static DateTimeOffset Start => DateTimeOffset.UtcNow;

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private static (NotificationCoordinator Coordinator, FakeVoicePlayer Player, ActivityHistory History, FakeVoiceProvider Provider) Create(
        TimeSpan? cooldown = null,
        bool muted = false)
    {
        var history = new ActivityHistory();
        var policy = new VoiceEventPolicy(VoicePolicyOptions.Default with { Cooldown = cooldown ?? TimeSpan.Zero });
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer();
        var manager = new VoiceManager(
            provider,
            player,
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero },
            TimeProvider.System,
            revalidate: candidate => policy.IsRelevant(candidate, DateTimeOffset.UtcNow),
            muted: muted);
        var settings = new VoiceSettings { Muted = muted, Provider = "pack" };
        var coordinator = new NotificationCoordinator(history, policy, manager, TimeProvider.System, settings);
        coordinator.Start();
        return (coordinator, player, history, provider);
    }

    [Fact]
    public async Task RaiseClearRaiseSurvivesWithoutAnyUiDrain()
    {
        var (coordinator, player, _, _) = Create();
        var key = VoiceTestData.Key();
        var start = Start;

        // No UI pump runs at all in this test: every snapshot must still be seen.
        coordinator.OnSnapshot(VoiceTestData.Snapshot(
            start,
            new[] { VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: start) }));
        Assert.True(await WaitForAsync(() => player.Played.Count == 1));
        coordinator.OnSnapshot(VoiceTestData.Snapshot(start.AddSeconds(1), new[] { VoiceTestData.Task(key: key, state: AgentTaskState.Working) }));
        coordinator.OnSnapshot(VoiceTestData.Snapshot(
            start.AddSeconds(2),
            new[] { VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: start.AddSeconds(2)) }));

        Assert.True(await WaitForAsync(() => player.Played.Count == 2), "played " + player.Played.Count);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DegradedReplayWithNewerTimestampDoesNotSpeakAgain()
    {
        var (coordinator, player, _, _) = Create();
        var key = VoiceTestData.Key();
        var start = Start;
        var attention = VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: start);
        var task = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval);

        coordinator.OnSnapshot(VoiceTestData.Snapshot(start, new[] { task }, new[] { attention }));
        Assert.True(await WaitForAsync(() => player.Played.Count == 1), "played " + player.Played.Count);

        for (var index = 1; index <= 5; index++)
        {
            // Same transitions, newer GeneratedAt: exactly what PublishDegraded does.
            coordinator.OnSnapshot(VoiceTestData.Snapshot(start.AddMilliseconds(index * 100), new[] { task }, new[] { attention }));
        }

        await Task.Delay(200);
        Assert.Single(player.Played);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task MonitorFailureCancelsVoiceAndBreaksHistory()
    {
        var (coordinator, player, history, _) = Create();
        var key = VoiceTestData.Key();
        var start = Start;
        coordinator.OnSnapshot(VoiceTestData.Snapshot(
            start,
            new[] { VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: start) }));
        Assert.True(await WaitForAsync(() => player.Played.Count == 1), "played " + player.Played.Count);

        coordinator.OnMonitorFailure("monitor-faulted");
        Assert.True(coordinator.IsMuted);
        Assert.Single(history.Snapshot(start.AddMinutes(1)).Samples);
        Assert.True(history.Snapshot(start.AddMinutes(1)).HasGap);

        // Later snapshots are ignored: a broken monitor never speaks.
        coordinator.OnSnapshot(VoiceTestData.Snapshot(
            start.AddMilliseconds(500),
            new[] { VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: start.AddMilliseconds(500)) }));
        await Task.Delay(200);
        Assert.Single(player.Played);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ProducerCallbackNeverBlocksOnProviderIo()
    {
        var (coordinator, player, history, provider) = Create();
        provider.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = VoiceTestData.Key();
        var start = Start;

        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 200; index++)
        {
            // One snapshot per second: 200 distinct one-second history buckets.
            coordinator.OnSnapshot(VoiceTestData.Snapshot(
                start.AddSeconds(index),
                new[] { VoiceTestData.Task(key: key, state: AgentTaskState.Working) }));
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 500, "producer path took " + stopwatch.ElapsedMilliseconds + "ms");
        Assert.Empty(player.Played);
        Assert.Equal(200, history.Snapshot(start.AddSeconds(199)).SampleCount);

        provider.Gate.SetResult();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task HistoryRecordsEverySnapshotEvenWhenTheUiCoalesces()
    {
        var (coordinator, _, history, _) = Create();
        var start = Start;
        for (var index = 0; index < 90; index++)
        {
            coordinator.OnSnapshot(VoiceTestData.Snapshot(
                start.AddSeconds(index),
                new[] { VoiceTestData.Task(state: AgentTaskState.Working) },
                working: CountValue.Known(index % 5)));
        }

        var series = history.Snapshot(start.AddSeconds(90));
        Assert.Equal(90, series.SampleCount);
        Assert.Equal(4, series.PeakWorking);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task MuteIsSharedAndImmediateThroughTheCoordinator()
    {
        var (coordinator, player, _, _) = Create();
        var key = VoiceTestData.Key();
        var start = Start;

        coordinator.SetMuted(true);
        Assert.True(coordinator.IsMuted);
        Assert.True(coordinator.Settings.Muted);

        coordinator.OnSnapshot(VoiceTestData.Snapshot(
            start,
            new[] { VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: start) }));

        await Task.Delay(150);
        Assert.Empty(player.Played);

        // Unmuting does not replay the muted condition.
        coordinator.SetMuted(false);
        await Task.Delay(200);
        Assert.Empty(player.Played);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task CompletionAggregationSpeaksOncePerGroup()
    {
        var (coordinator, player, _, _) = Create();
        var start = Start;
        var key1 = VoiceTestData.Key("01a0e000-0000-0000-0000-0000000000a1");
        var key2 = VoiceTestData.Key("01a0e000-0000-0000-0000-0000000000a2");
        var tasks = new[]
        {
            VoiceTestData.Task(key: key1, number: 1, state: AgentTaskState.Done),
            VoiceTestData.Task(key: key2, number: 2, state: AgentTaskState.Done),
        };
        var events = new[]
        {
            VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key1, number: 1, observedAt: start, state: AgentTaskState.Done),
            VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key2, number: 2, observedAt: start, state: AgentTaskState.Done),
        };

        coordinator.OnSnapshot(VoiceTestData.Snapshot(start, tasks, events, working: CountValue.Partial(0), coverageComplete: false));
        await Task.Delay(150);
        Assert.Empty(player.Played);

        // The pump is what flushes the aggregation window.
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        coordinator.Pump();
        Assert.True(await WaitForAsync(() => player.Played.Count == 1), "played " + player.Played.Count);
        await coordinator.DisposeAsync();
    }
}
