using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class VoiceEventPolicyTests
{
    private static readonly DateTimeOffset Start = VoiceTestData.Start;

    private static VoiceEventPolicy Policy(VoicePolicyOptions? options = null) => new(options);

    [Fact]
    public void HydrationWithoutExplicitEventsIsBaselineAndSilent()
    {
        var policy = Policy();
        var task = VoiceTestData.Task(state: AgentTaskState.NeedsApproval);
        var candidates = policy.Observe(VoiceTestData.Snapshot(Start, new[] { task }));

        Assert.Empty(candidates);
        Assert.True(policy.BaselineEstablished);
        Assert.True(policy.LatchCount > 0);
    }

    [Fact]
    public void FreshExplicitEventInTheFirstSnapshotIsAnnounced()
    {
        var policy = Policy();
        var task = VoiceTestData.Task(state: AgentTaskState.NeedsApproval);
        var attention = VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: Start);
        var candidates = policy.Observe(VoiceTestData.Snapshot(Start, new[] { task }, new[] { attention }));

        Assert.Single(candidates);
        Assert.Equal(VoiceEventKind.PermissionRequired, candidates[0].Kind);
    }

    [Fact]
    public void RepeatedOrDegradedReplayOfTheSameEventIsDeduped()
    {
        var policy = Policy();
        var task = VoiceTestData.Task(state: AgentTaskState.NeedsApproval);
        var attention = VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: Start);

        Assert.Single(policy.Observe(VoiceTestData.Snapshot(Start, new[] { task }, new[] { attention })));

        // Same transitions republished with a newer GeneratedAt (degraded PublishDegraded).
        for (var index = 1; index <= 3; index++)
        {
            var repeated = policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(index), new[] { task }, new[] { attention }));
            Assert.Empty(repeated);
        }
    }

    [Fact]
    public void StaleAndFutureEventsAreRejected()
    {
        var policy = Policy();
        var task = VoiceTestData.Task(state: AgentTaskState.NeedsApproval);
        var stale = VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: Start.AddSeconds(-45));
        var future = VoiceTestData.Event(AttentionEventKind.InputRequired, observedAt: Start.AddMinutes(5), key: VoiceTestData.Key("01a0e000-0000-0000-0000-000000000002"), number: 2);

        var candidates = policy.Observe(VoiceTestData.Snapshot(Start, new[] { task }, new[] { stale, future }));
        Assert.Empty(candidates);
    }

    [Fact]
    public void ConditionLongerThanCooldownIsAnnouncedOnceOnly()
    {
        var policy = Policy();
        var task = VoiceTestData.Task(state: AgentTaskState.NeedsApproval);
        var attention = VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: Start);
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(Start, new[] { task }, new[] { attention })));

        for (var index = 1; index <= 60; index++)
        {
            var at = Start.AddSeconds(index);
            Assert.Empty(policy.Observe(VoiceTestData.Snapshot(at, new[] { task })));
        }
    }

    [Fact]
    public void VerifiedClearanceReArmsANewEpisode()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var waiting = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval);
        var working = VoiceTestData.Task(key: key, state: AgentTaskState.Working);

        Assert.Single(policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { waiting },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start) })));

        // Fresh observed clearance.
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(20), new[] { working })));

        // A new episode after clearance is announced again, even inside cooldown of the old one.
        var second = policy.Observe(VoiceTestData.Snapshot(
            Start.AddSeconds(25),
            new[] { waiting },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start.AddSeconds(25)) }));
        Assert.Single(second);
    }

    [Fact]
    public void MissingStaleOrProvisionalEvidenceIsNotClearance()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var waiting = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval);
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { waiting },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start) })));

        // Task disappears entirely.
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(20), Array.Empty<AgentTask>())));

        // Task returns stale/provisional: the condition is still latched, and no
        // second announcement happens.
        var stale = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval, stale: true);
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(21), new[] { stale })));

        var fresh = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval);
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(22), new[] { fresh })));

        // Clearance after the stale period re-arms for a genuinely new episode.
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(23), new[] { VoiceTestData.Task(key: key, state: AgentTaskState.Working) })));
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(
            Start.AddSeconds(60),
            new[] { fresh },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start.AddSeconds(60)) })));
    }

    [Fact]
    public void NonBlockingNeedsHumanKeepsWorkingAndRaisesInput()
    {
        var policy = Policy();
        var task = VoiceTestData.Task(state: AgentTaskState.Working, needsHuman: true);
        var attention = VoiceTestData.Event(
            AttentionEventKind.InputRequired,
            observedAt: Start,
            code: "input-requested-nonblocking",
            state: AgentTaskState.Working);

        var candidates = policy.Observe(VoiceTestData.Snapshot(Start, new[] { task }, new[] { attention }));
        Assert.Single(candidates);
        Assert.Equal(VoiceEventKind.InputRequired, candidates[0].Kind);
    }

    [Fact]
    public void ApprovalPendingTaskDoesNotRaiseASecondInputCue()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        // A pending approval is a human need, but it must be announced once.
        var approval = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval, needsHuman: true);
        var candidates = policy.Observe(VoiceTestData.Snapshot(
            Start.AddSeconds(5),
            new[] { approval },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start.AddSeconds(5)) }));

        Assert.Single(candidates);
        Assert.Equal(VoiceEventKind.PermissionRequired, candidates[0].Kind);
        Assert.Equal(0, policy.PendingCount);
    }

    [Fact]
    public void InputStaysLatchedWhileApprovalTakesStatePrecedence()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var nonBlocking = VoiceTestData.Task(key: key, state: AgentTaskState.Working, needsHuman: true);
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { nonBlocking },
            new[] { VoiceTestData.Event(AttentionEventKind.InputRequired, key: key, observedAt: Start, code: "input-requested-nonblocking", state: AgentTaskState.Working) })));

        // Approval now owns the state, but input is still pending for a human.
        var approval = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval, needsHuman: true);
        var approvalCandidates = policy.Observe(VoiceTestData.Snapshot(
            Start.AddSeconds(12),
            new[] { approval },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start.AddSeconds(12)) }));
        Assert.Single(approvalCandidates);
        Assert.Equal(VoiceEventKind.PermissionRequired, approvalCandidates[0].Kind);

        // Resolving approval does not re-announce the still-latched input.
        var stillWaiting = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsInput, needsHuman: true);
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(14), new[] { stillWaiting })));
    }

    [Fact]
    public void NewTurnIsANewEpisodeEvenWithoutClearance()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var first = VoiceTestData.Task(key: key, state: AgentTaskState.Error, turnId: "turn-1");
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { first },
            new[] { VoiceTestData.Event(AttentionEventKind.TaskError, key: key, turnId: "turn-1", observedAt: Start) })));

        var second = VoiceTestData.Task(key: key, state: AgentTaskState.Error, turnId: "turn-2");
        var candidates = policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(30), new[] { second }));
        Assert.Single(candidates);
        Assert.Equal("turn-2", candidates[0].TurnId);
    }

    [Fact]
    public void ReusedNumberWithDifferentKeyIsItsOwnEpisode()
    {
        var policy = Policy();
        var firstKey = VoiceTestData.Key("01a0e000-0000-0000-0000-00000000000a");
        var secondKey = VoiceTestData.Key("01a0e000-0000-0000-0000-00000000000b");
        var first = VoiceTestData.Task(key: firstKey, number: 1, state: AgentTaskState.NeedsApproval);
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { first },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: firstKey, number: 1, observedAt: Start) })));

        var second = VoiceTestData.Task(key: secondKey, number: 1, state: AgentTaskState.NeedsApproval);
        var candidates = policy.Observe(VoiceTestData.Snapshot(
            Start.AddSeconds(30),
            new[] { second },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: secondKey, number: 1, observedAt: Start.AddSeconds(30)) }));
        Assert.Single(candidates);
        Assert.Equal(secondKey, candidates[0].Key);
    }

    [Fact]
    public void CompletionAggregatesIntoOneCueAfterTheWindow()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var done = VoiceTestData.Task(key: key, state: AgentTaskState.Done);
        var attention = VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key, observedAt: Start, state: AgentTaskState.Done);

        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start, new[] { done }, new[] { attention })));
        Assert.Empty(policy.Pump(Start.AddSeconds(1)));

        var flushed = policy.Pump(Start.AddSeconds(2));
        Assert.Single(flushed);
        Assert.Equal(VoiceEventKind.TaskCompleted, flushed[0].Kind);
        Assert.False(flushed[0].IsAggregate);
    }

    [Fact]
    public void DuplicateTaskAndTurnCompletionsProduceOneGroup()
    {
        var policy = Policy();
        var key1 = VoiceTestData.Key("01a0e000-0000-0000-0000-00000000000c");
        var key2 = VoiceTestData.Key("01a0e000-0000-0000-0000-00000000000d");
        var done1 = VoiceTestData.Task(key: key1, number: 1, state: AgentTaskState.Done);
        var done2 = VoiceTestData.Task(key: key2, number: 2, state: AgentTaskState.Done);
        var events = new[]
        {
            VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key1, number: 1, observedAt: Start, state: AgentTaskState.Done),
            VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key1, number: 1, observedAt: Start, state: AgentTaskState.Done),
            VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key2, number: 2, observedAt: Start, state: AgentTaskState.Done),
        };

        policy.Observe(VoiceTestData.Snapshot(Start, new[] { done1, done2 }, events));
        var flushed = policy.Pump(Start.AddSeconds(2));

        Assert.Single(flushed);
        Assert.True(flushed[0].IsAggregate);
        Assert.Equal(2, flushed[0].TaskCount);
        Assert.Contains("2 tasks", VoiceNotificationFormatter.Text(flushed[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidAllCompleteReplacesTheGroupInsteadOfAddingASecondCue()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var done = VoiceTestData.Task(key: key, state: AgentTaskState.Done);
        var events = new[]
        {
            VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key, observedAt: Start, state: AgentTaskState.Done),
            VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, key: default, number: 0, turnId: string.Empty, observedAt: Start, state: AgentTaskState.Done),
        };

        policy.Observe(VoiceTestData.Snapshot(Start, new[] { done }, events, working: CountValue.Known(0)));
        var flushed = policy.Pump(Start.AddSeconds(2));

        Assert.Single(flushed);
        Assert.Equal(VoiceEventKind.AllTasksCompleted, flushed[0].Kind);
        Assert.True(flushed[0].IsAggregate);
        Assert.Equal("All tasks finished.", VoiceNotificationFormatter.Text(flushed[0]));
    }

    [Fact]
    public void NewWorkInvalidatesGlobalCompletion()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var done = VoiceTestData.Task(key: key, state: AgentTaskState.Done);
        policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { done },
            new[] { VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, key: default, number: 0, turnId: string.Empty, observedAt: Start, state: AgentTaskState.Done) },
            working: CountValue.Known(0)));

        // New work appears during the aggregation window.
        var newWork = VoiceTestData.Task(key: VoiceTestData.Key("01a0e000-0000-0000-0000-00000000000e"), number: 2, state: AgentTaskState.Working);
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1), new[] { done, newWork }, working: CountValue.Known(1)));

        Assert.Empty(policy.Pump(Start.AddSeconds(2)));
    }

    [Fact]
    public void CancellationErrorAndPartialCoverageNeverEstablishAllComplete()
    {
        var key = VoiceTestData.Key();
        var done = VoiceTestData.Task(key: key, state: AgentTaskState.Done);
        var aggregate = VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, key: default, number: 0, turnId: string.Empty, observedAt: Start, state: AgentTaskState.Done);

        var cancelled = Policy();
        var cancelledTask = VoiceTestData.Task(key: key, state: AgentTaskState.Idle, cancelled: true);
        cancelled.Observe(VoiceTestData.Snapshot(Start, new[] { cancelledTask }, new[] { aggregate }, working: CountValue.Known(0)));
        Assert.Empty(cancelled.Pump(Start.AddSeconds(2)));

        var failed = Policy();
        var failedTask = VoiceTestData.Task(key: key, state: AgentTaskState.Error, failed: true);
        failed.Observe(VoiceTestData.Snapshot(Start, new[] { failedTask }, new[] { aggregate }, working: CountValue.Known(0)));
        Assert.Empty(failed.Pump(Start.AddSeconds(2)));

        var partial = Policy();
        partial.Observe(VoiceTestData.Snapshot(Start, new[] { done }, new[] { aggregate }, working: CountValue.Partial(0), coverageComplete: false));
        Assert.Empty(partial.Pump(Start.AddSeconds(2)));

        var unknown = Policy();
        unknown.Observe(VoiceTestData.Snapshot(Start, new[] { done }, new[] { aggregate }, working: CountValue.Unknown, coverageComplete: false));
        Assert.Empty(unknown.Pump(Start.AddSeconds(2)));
    }

    [Fact]
    public void IndependentLaterAggregateEpisodeIsAnnouncedAgain()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var done = VoiceTestData.Task(key: key, state: AgentTaskState.Done);
        var first = VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, key: default, number: 0, turnId: string.Empty, observedAt: Start, state: AgentTaskState.Done);

        policy.Observe(VoiceTestData.Snapshot(Start, new[] { done }, new[] { first }, working: CountValue.Known(0)));
        Assert.Single(policy.Pump(Start.AddSeconds(2)));

        var second = VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, key: default, number: 0, turnId: string.Empty, observedAt: Start.AddMinutes(10), state: AgentTaskState.Done);
        policy.Observe(VoiceTestData.Snapshot(Start.AddMinutes(10), new[] { done }, new[] { second }, working: CountValue.Known(0)));
        Assert.Single(policy.Pump(Start.AddMinutes(10).AddSeconds(2)));
    }

    [Fact]
    public void CompletionForADisappearedTaskIsNotAnnounced()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var attention = VoiceTestData.Event(AttentionEventKind.TaskCompleted, key: key, observedAt: Start, state: AgentTaskState.Done);
        policy.Observe(VoiceTestData.Snapshot(Start, new[] { VoiceTestData.Task(key: key, state: AgentTaskState.Done) }, new[] { attention }));

        // The task disappears before the aggregation window closes.
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1), Array.Empty<AgentTask>()));
        Assert.Empty(policy.Pump(Start.AddSeconds(2)));
    }

    [Fact]
    public void CooldownDefersRatherThanDropsAndExpiresAfterThirtySeconds()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var waiting = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval);

        Assert.Single(policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { waiting },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start) })));

        // Clear and re-raise inside the 10s cooldown: the new episode waits.
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(2), new[] { VoiceTestData.Task(key: key, state: AgentTaskState.Working) }));
        var raised = policy.Observe(VoiceTestData.Snapshot(
            Start.AddSeconds(3),
            new[] { waiting },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start.AddSeconds(3)) }));
        Assert.Empty(raised);
        Assert.Equal(1, policy.PendingCount);

        // Still inside the cooldown window.
        Assert.Empty(policy.Pump(Start.AddSeconds(5)));

        // After the cooldown it becomes eligible.
        Assert.Single(policy.Pump(Start.AddSeconds(13)));
        Assert.Equal(0, policy.PendingCount);
    }

    [Fact]
    public void PendingCandidatesExpireInsteadOfRetryingForever()
    {
        var policy = Policy();
        var key = VoiceTestData.Key();
        var waiting = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval);
        policy.Observe(VoiceTestData.Snapshot(
            Start,
            new[] { waiting },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start) }));

        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(2), new[] { VoiceTestData.Task(key: key, state: AgentTaskState.Working) }));
        policy.Observe(VoiceTestData.Snapshot(
            Start.AddSeconds(3),
            new[] { waiting },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start.AddSeconds(3)) }));

        Assert.Empty(policy.Pump(Start.AddSeconds(45)));
        Assert.Equal(0, policy.PendingCount);
    }

    [Fact]
    public void LatchStateIsBoundedByTaskLifetimeAndCaps()
    {
        var options = VoicePolicyOptions.Default with { MaximumLatches = 32 };
        var policy = Policy(options);
        for (var index = 0; index < 200; index++)
        {
            var key = VoiceTestData.Key("01a0e000-0000-0000-0000-0000000000" + (index % 100).ToString("x2"));
            policy.Observe(VoiceTestData.Snapshot(
                Start.AddSeconds(index),
                new[] { VoiceTestData.Task(key: key, number: index, state: AgentTaskState.NeedsApproval) }));
        }

        Assert.True(policy.LatchCount <= 32, "latches: " + policy.LatchCount);
    }

    [Fact]
    public void DedupeSetIsBoundedUnderRepeatedEvents()
    {
        var options = VoicePolicyOptions.Default with { MaximumDedupeEntries = 64 };
        var policy = Policy(options);
        for (var index = 0; index < 500; index++)
        {
            var key = VoiceTestData.Key("01a0e000-0000-0000-0000-0000000000" + (index % 100).ToString("x2"));
            policy.Observe(VoiceTestData.Snapshot(
                Start.AddSeconds(index),
                new[] { VoiceTestData.Task(key: key, number: index, state: AgentTaskState.NeedsApproval) },
                new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, number: index, observedAt: Start.AddSeconds(index)) }));
        }

        Assert.True(true);
    }

    [Fact]
    public void ResetClearsLatchesAndBaseline()
    {
        var policy = Policy();
        policy.Observe(VoiceTestData.Snapshot(Start, new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval) }));
        Assert.True(policy.BaselineEstablished);

        policy.Reset();
        Assert.False(policy.BaselineEstablished);
        Assert.Equal(0, policy.LatchCount);
    }
}
