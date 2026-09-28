using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.State;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class AgentStateReducerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static (AgentStateReducer Reducer, ManualTimeProvider Clock) Create(MonitorOptions? options = null)
    {
        var clock = new ManualTimeProvider(Start);
        var reducer = new AgentStateReducer(options ?? MonitorOptions.Default, clock);
        return (reducer, clock);
    }

    private static RawCodexEvent Event(
        RawCodexEventKind kind,
        string turn = "turn-1",
        string? callId = null,
        string? requestId = null,
        ToolCallCategory tool = ToolCallCategory.None,
        AgentActionKind? action = null,
        string? detail = null,
        bool? blocking = null,
        TurnOutcome? outcome = null,
        string? reason = null,
        int order = 1,
        int fileId = 1,
        bool hydration = false,
        DateTimeOffset? at = null) => new(
        kind,
        "codex",
        "conversation-1",
        turn,
        callId,
        requestId,
        tool,
        action,
        detail,
        blocking,
        outcome,
        reason,
        at ?? Start,
        at ?? Start,
        fileId,
        order,
        order,
        0,
        hydration);

    private static AgentTask Task(AgentStateReducer reducer, ManualTimeProvider clock, string conversationId = "conversation-1") =>
        reducer.Snapshot(clock.GetUtcNow()).Single(task => task.ConversationId == conversationId);

    [Theory]
    [InlineData("wrong-turn", 5)]
    [InlineData("turn-1", -5)]
    public void AsyncReplyCannotChangeTurnsOrResolveBeforeRequest(string turn, int seconds)
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted));
        reducer.Apply(Event(RawCodexEventKind.InputRequested, turn: "", requestId: "a", blocking: false, order: 2) with { InputQuestionCount = 1 });
        reducer.Apply(Event(RawCodexEventKind.InputResolved, turn: turn, order: 3, at: Start.AddSeconds(seconds)) with { InputAnswers = new[] { new InputAnswerReference("a", 0) } });
        Assert.True(Task(reducer, clock).NeedsHuman);
        Assert.Equal("turn-1", Task(reducer, clock).TurnId);
    }

    [Fact]
    public void AsyncReplyCannotResolveAnApprovalWithMatchingId()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted));
        reducer.Apply(Event(RawCodexEventKind.PermissionRequested, requestId: "a", order: 2) with { InputQuestionCount = 1 });
        reducer.Apply(Event(RawCodexEventKind.InputResolved, order: 3) with { InputAnswers = new[] { new InputAnswerReference("a", 0) } });
        Assert.Equal(AgentTaskState.NeedsApproval, Task(reducer, clock).State);
    }

    [Fact]
    public void ExplicitStartBecomesWorkingWithoutTokensOrAssistantText()
    {
        var (reducer, clock) = Create();
        var outcome = reducer.Apply(Event(RawCodexEventKind.TaskStarted));

        Assert.True(outcome.Applied);
        Assert.Equal(TaskTransitionKind.StateChanged, outcome.Transition!.Kind);
        Assert.Equal(AgentTaskState.Working, Task(reducer, clock).State);
        Assert.Equal(EvidenceKind.Explicit, Task(reducer, clock).Evidence);
        Assert.Equal(AgentActionKind.Thinking, Task(reducer, clock).Action.Kind);
    }

    [Fact]
    public void CompletionWithoutAssistantTextBecomesDone()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted));
        var outcome = reducer.Apply(Event(RawCodexEventKind.TaskCompleted, order: 2));

        Assert.Equal(AgentTaskState.Done, Task(reducer, clock).State);
        Assert.Equal(AgentActionKind.Completed, Task(reducer, clock).Action.Kind);
        Assert.Equal(TaskTransitionKind.StateChanged, outcome.Transition!.Kind);
    }

    [Fact]
    public void DuplicateSourceRecordIsRejectedWithoutDuplicateTransition()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        var duplicate = reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));

        Assert.False(duplicate.Applied);
        Assert.Equal("duplicate-record", duplicate.RejectCode);
        Assert.Null(duplicate.Transition);
    }

    [Fact]
    public void OlderTurnEvidenceCannotOverwriteNewerWork()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, turn: "turn-1", order: 1));
        reducer.Apply(Event(RawCodexEventKind.TaskCompleted, turn: "turn-1", order: 2));
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, turn: "turn-2", order: 3));

        var late = reducer.Apply(Event(RawCodexEventKind.TaskCompleted, turn: "turn-1", order: 4));
        Assert.False(late.Applied);
        Assert.Equal("old-turn", late.RejectCode);
        Assert.Equal(AgentTaskState.Working, Task(reducer, clock).State);
        Assert.Equal("turn-2", Task(reducer, clock).TurnId);
    }

    [Fact]
    public void EvidenceForATerminalTurnIsRejected()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.Apply(Event(RawCodexEventKind.TaskCompleted, order: 2));

        var late = reducer.Apply(Event(RawCodexEventKind.ItemActivity, order: 3, tool: ToolCallCategory.CommandExecution, action: AgentActionKind.RunningCommand));
        Assert.False(late.Applied);
        Assert.Equal("terminal-turn-evidence", late.RejectCode);
        Assert.Equal(AgentTaskState.Done, Task(reducer, clock).State);
    }

    [Fact]
    public void CancellationBecomesIdleWithReasonNotDone()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.Apply(Event(RawCodexEventKind.TurnAborted, order: 2, reason: "cancelled", outcome: TurnOutcome.Aborted));

        var task = Task(reducer, clock);
        Assert.Equal(AgentTaskState.Idle, task.State);
        Assert.True(task.CancellationObserved);
        Assert.False(task.TerminalFailureObserved);
        Assert.NotEqual(AgentTaskState.Done, task.State);
    }

    [Fact]
    public void AbortWithFailureReasonBecomesError()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.Apply(Event(RawCodexEventKind.TurnAborted, order: 2, reason: "failed"));

        Assert.Equal(AgentTaskState.Error, Task(reducer, clock).State);
        Assert.True(Task(reducer, clock).TerminalFailureObserved);
    }

    [Fact]
    public void ToolFailureIsNotTaskFailure()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.Apply(Event(RawCodexEventKind.ItemActivity, order: 2, tool: ToolCallCategory.CommandExecution, action: AgentActionKind.RunningCommand));

        var task = Task(reducer, clock);
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.False(task.TerminalFailureObserved);
    }

    [Fact]
    public void PermissionRequestAndResolutionRoundTrip()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        var raised = reducer.Apply(Event(RawCodexEventKind.PermissionRequested, order: 2, requestId: "req-1", callId: "req-1"));

        Assert.Equal(AgentTaskState.NeedsApproval, Task(reducer, clock).State);
        Assert.Equal(AgentActionKind.WaitingForApproval, Task(reducer, clock).Action.Kind);
        Assert.Equal("permission-requested", raised.Transition!.CauseCode);
        Assert.True(Task(reducer, clock).NeedsHuman);

        var resolved = reducer.Apply(Event(RawCodexEventKind.PermissionResolved, order: 3, requestId: "req-1", callId: "req-1"));
        Assert.Equal(AgentTaskState.Working, Task(reducer, clock).State);
        Assert.False(Task(reducer, clock).NeedsHuman);
        Assert.Equal(TaskTransitionKind.AttentionCleared, resolved.Transition!.Kind);
    }

    [Fact]
    public void UnrelatedToolOutputDoesNotClearPendingAttention()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.Apply(Event(RawCodexEventKind.PermissionRequested, order: 2, requestId: "req-1"));
        reducer.Apply(Event(RawCodexEventKind.ToolCallCompleted, order: 3, callId: "other-call", tool: ToolCallCategory.OpaqueWrapper));
        reducer.Apply(Event(RawCodexEventKind.TokenUsage, order: 4));

        Assert.Equal(AgentTaskState.NeedsApproval, Task(reducer, clock).State);
        Assert.True(Task(reducer, clock).NeedsHuman);
    }

    [Fact]
    public void ApprovalHasDisplayPrecedenceButBothRequestsRemainTracked()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.Apply(Event(RawCodexEventKind.InputRequested, order: 2, requestId: "in-1", blocking: true));
        reducer.Apply(Event(RawCodexEventKind.PermissionRequested, order: 3, requestId: "ap-1"));

        Assert.Equal(AgentTaskState.NeedsApproval, Task(reducer, clock).State);
        Assert.Equal(AgentActionKind.WaitingForApproval, Task(reducer, clock).Action.Kind);

        reducer.Apply(Event(RawCodexEventKind.PermissionResolved, order: 4, requestId: "ap-1"));
        Assert.Equal(AgentTaskState.NeedsInput, Task(reducer, clock).State);
        Assert.Equal(AgentActionKind.WaitingForInput, Task(reducer, clock).Action.Kind);
        Assert.True(Task(reducer, clock).NeedsHuman);

        reducer.Apply(Event(RawCodexEventKind.InputResolved, order: 5, requestId: "in-1"));
        Assert.Equal(AgentTaskState.Working, Task(reducer, clock).State);
        Assert.False(Task(reducer, clock).NeedsHuman);
    }

    [Fact]
    public void NonBlockingInputRaisesAttentionWithoutChangingWorkingState()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        var outcome = reducer.Apply(Event(RawCodexEventKind.InputRequested, order: 2, requestId: "in-1", blocking: false));

        var task = Task(reducer, clock);
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.True(task.NeedsHuman);
        Assert.Equal("input-requested-nonblocking", task.AttentionCode);
        Assert.Equal(TaskTransitionKind.NonBlockingInputRaised, outcome.Transition!.Kind);

        // An immediate async tool return is not a user answer.
        reducer.Apply(Event(RawCodexEventKind.ToolCallCompleted, order: 3, callId: "in-1"));
        Assert.True(Task(reducer, clock).NeedsHuman);

        reducer.Apply(Event(RawCodexEventKind.InputResolved, order: 4, requestId: "in-1"));
        Assert.False(Task(reducer, clock).NeedsHuman);
        Assert.Equal(AgentTaskState.Working, Task(reducer, clock).State);
    }

    [Fact]
    public void StructuredActivityWithoutLifecycleAnchorsWorkingState()
    {
        var (reducer, clock) = Create();
        var outcome = reducer.Apply(Event(RawCodexEventKind.ItemActivity, order: 1, turn: "turn-9", tool: ToolCallCategory.CommandExecution, action: AgentActionKind.RunningTests, detail: "test-runner"));

        Assert.Equal(AgentTaskState.Working, Task(reducer, clock).State);
        Assert.Equal(EvidenceKind.Structured, Task(reducer, clock).Evidence);
        Assert.Equal(AgentActionKind.RunningTests, Task(reducer, clock).Action.Kind);
        Assert.Equal(TaskTransitionKind.StateChanged, outcome.Transition!.Kind);
    }

    [Fact]
    public void ANewTurnAfterCompletionRevivesWorking()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, turn: "turn-1", order: 1));
        reducer.Apply(Event(RawCodexEventKind.TaskCompleted, turn: "turn-1", order: 2, at: Start.AddMinutes(1)));
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, turn: "turn-2", order: 3, at: Start.AddMinutes(2)));

        Assert.Equal(AgentTaskState.Working, Task(reducer, clock).State);
        Assert.Equal("turn-2", Task(reducer, clock).TurnId);
    }

    [Fact]
    public void NumbersAreAllocatedPerConversationAndSurviveReRegistration()
    {
        var (reducer, clock) = Create();
        var first = reducer.RegisterConversation("codex", "conversation-1", ClientSurface.Desktop, "One", true, Start);
        var second = reducer.RegisterConversation("codex", "conversation-2", ClientSurface.Cli, "Two", true, Start);

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);

        // Same conversation discovered again through another rollout file.
        var again = reducer.RegisterConversation("codex", "conversation-1", ClientSurface.Desktop, "One", true, Start);
        Assert.Equal(1, again.Number);
        Assert.Equal(2, reducer.ConversationCount);
    }

    [Fact]
    public void DistinctProfilesWithTheSameConversationIdStayDistinct()
    {
        var (reducer, clock) = Create();
        var one = reducer.RegisterConversation("codex", "shared-id", ClientSurface.Desktop, "One", true, Start);
        var two = reducer.RegisterConversation("codex-2", "shared-id", ClientSurface.Cli, "Two", true, Start);

        Assert.NotEqual(one.Number, two.Number);
        Assert.Equal(2, reducer.ConversationCount);
    }

    [Fact]
    public void StaleLeaseKeepsLastKnownStateButFlagsIt()
    {
        var options = MonitorOptions.Default with { StaleLease = TimeSpan.FromMinutes(5) };
        var (reducer, clock) = Create(options);
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.SetTrackedKeys(new[] { new TaskKey("codex", "conversation-1") });

        Assert.False(Task(reducer, clock).IsStale);

        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = Task(reducer, clock);
        Assert.True(stale.IsStale);
        Assert.Equal(AgentTaskState.Working, stale.State);
    }

    [Fact]
    public void UntrackedRolloutFileMakesTheTaskStale()
    {
        var (reducer, clock) = Create();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.SetTrackedKeys(Array.Empty<TaskKey>());
        Assert.True(Task(reducer, clock).IsStale);
    }

    [Fact]
    public void SourceActivityOnlyRefreshesNonTerminalTasks()
    {
        var options = MonitorOptions.Default with { StaleLease = TimeSpan.FromMinutes(5) };
        var (reducer, clock) = Create(options);
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, order: 1));
        reducer.SetTrackedKeys(new[] { new TaskKey("codex", "conversation-1") });
        clock.Advance(TimeSpan.FromMinutes(4));
        reducer.NoteSourceActivity(new TaskKey("codex", "conversation-1"), clock.GetUtcNow());
        Assert.False(Task(reducer, clock).IsStale);

        reducer.Apply(Event(RawCodexEventKind.TaskCompleted, order: 2));
        clock.Advance(TimeSpan.FromHours(1));
        reducer.NoteSourceActivity(new TaskKey("codex", "conversation-1"), clock.GetUtcNow());
        Assert.Equal(AgentTaskState.Done, Task(reducer, clock).State);
        Assert.False(Task(reducer, clock).IsStale);
    }

    [Fact]
    public void ManyTurnsKeepTheReducerBoundedAndCorrect()
    {
        var options = MonitorOptions.Default with { MaxTurnHistoryPerTask = 8 };
        var (reducer, clock) = Create(options);
        for (var index = 0; index < 200; index++)
        {
            var turn = "turn-" + index;
            reducer.Apply(Event(RawCodexEventKind.TaskStarted, turn: turn, order: index * 3 + 1, at: Start.AddSeconds(index * 3)));
            reducer.Apply(Event(RawCodexEventKind.ItemActivity, turn: turn, order: index * 3 + 2, at: Start.AddSeconds(index * 3 + 1), tool: ToolCallCategory.FileChange, action: AgentActionKind.EditingFiles));
            reducer.Apply(Event(RawCodexEventKind.TaskCompleted, turn: turn, order: index * 3 + 3, at: Start.AddSeconds(index * 3 + 2)));
        }

        var task = Task(reducer, clock);
        Assert.Equal(AgentTaskState.Done, task.State);
        Assert.Equal("turn-199", task.TurnId);
    }
}
