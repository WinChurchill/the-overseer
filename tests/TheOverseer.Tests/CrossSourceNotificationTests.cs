using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

/// <summary>
/// Cross-source notification behaviour: harness-qualified identity keeps equal
/// conversation ids apart, a failing peer never silences or duplicates healthy
/// work, and a hydration-only publication speaks nothing.
/// </summary>
public sealed class CrossSourceNotificationTests
{
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

    private static (NotificationCoordinator Coordinator, FakeVoicePlayer Player) Create()
    {
        var policy = new VoiceEventPolicy(VoicePolicyOptions.Default with { Cooldown = TimeSpan.Zero });
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer();
        var manager = new VoiceManager(
            provider,
            player,
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero },
            TimeProvider.System,
            revalidate: candidate => policy.IsRelevant(candidate, DateTimeOffset.UtcNow),
            muted: false);
        var coordinator = new NotificationCoordinator(
            new ActivityHistory(),
            policy,
            manager,
            TimeProvider.System,
            new VoiceSettings { Muted = false, Provider = "pack" });
        coordinator.Start();
        return (coordinator, player);
    }

    private static AgentTask CrossTask(string harnessId, string sourceInstance, string conversation, AgentTaskState state) =>
        VoiceTestData.Task(state: state) with
        {
            Key = TaskKey.For(harnessId, sourceInstance, conversation),
            ProfileId = sourceInstance,
            ConversationId = conversation,
        };

    [Fact]
    public async Task TheSameConversationIdInTwoHarnessesIsTwoDistinctNotifications()
    {
        var (coordinator, player) = Create();
        var start = Start;
        const string conversation = "01a0e000-0000-0000-0000-0000000000f1";
        var codexKey = TaskKey.For(HarnessIds.Codex, "codex", conversation);
        var dshKey = TaskKey.For(HarnessIds.Dsh, "dsh-default", conversation);

        var snapshot = VoiceTestData.Snapshot(
            start,
            new[]
            {
                CrossTask(HarnessIds.Codex, "codex", conversation, AgentTaskState.NeedsApproval),
                CrossTask(HarnessIds.Dsh, "dsh-default", conversation, AgentTaskState.NeedsApproval),
            },
            new[]
            {
                VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: codexKey, observedAt: start),
                VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: dshKey, observedAt: start),
            });

        coordinator.OnSnapshot(snapshot);

        // Harness-qualified keys must not collapse into one task or one cue.
        Assert.True(await WaitForAsync(() => player.Played.Count == 2), "played " + player.Played.Count);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task AHealthySourceStillSpeaksWhileItsPeerSourceIsFailing()
    {
        var (coordinator, player) = Create();
        var start = Start;
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-0000000000f2");

        // The composite reports a degraded, partially covered snapshot because one
        // peer source failed; healthy work must still notify once.
        coordinator.OnSnapshot(VoiceTestData.Snapshot(
            start,
            new[] { CrossTask(HarnessIds.Codex, "codex", key.ConversationId, AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: start) },
            level: MonitoringHealthLevel.Degraded,
            coverageComplete: false));

        Assert.True(await WaitForAsync(() => player.Played.Count == 1), "played " + player.Played.Count);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task HydrationOnlyPublicationsAndRepeatsSpeakNothingExtra()
    {
        var (coordinator, player) = Create();
        var start = Start;
        var key = TaskKey.For(HarnessIds.Dsh, "dsh-default", "01a0e000-0000-0000-0000-0000000000f3");

        // A bridge attach publishes current state with no fresh edges, repeatedly.
        for (var index = 0; index < 5; index++)
        {
            coordinator.OnSnapshot(VoiceTestData.Snapshot(
                start.AddMilliseconds(index * 50),
                new[] { CrossTask(HarnessIds.Dsh, "dsh-default", key.ConversationId, AgentTaskState.Working) }));
        }

        await Task.Delay(300);
        Assert.Empty(player.Played);

        // A later explicit condition in the same session still speaks exactly once.
        coordinator.OnSnapshot(VoiceTestData.Snapshot(
            start.AddSeconds(1),
            new[] { CrossTask(HarnessIds.Dsh, "dsh-default", key.ConversationId, AgentTaskState.NeedsInput) },
            new[] { VoiceTestData.Event(AttentionEventKind.InputRequired, key: key, observedAt: start.AddSeconds(1), state: AgentTaskState.NeedsInput) }));

        Assert.True(await WaitForAsync(() => player.Played.Count == 1), "played " + player.Played.Count);
        await coordinator.DisposeAsync();
    }
}
