using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Tests;

public sealed class AttentionDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static AgentTask Task(
        string conversationId,
        AgentTaskState state,
        bool stale = false,
        bool cancelled = false,
        bool failed = false,
        AgentTaskState? previousState = null) => new(
        new TaskKey("codex", conversationId),
        1,
        conversationId,
        "codex",
        ClientSurface.Desktop,
        "Synthetic",
        "turn-1",
        state,
        new AgentAction(state == AgentTaskState.Done ? AgentActionKind.Completed : AgentActionKind.RunningCommand, EvidenceKind.Explicit),
        EvidenceKind.Explicit,
        Now,
        Now,
        Now,
        Now,
        state == AgentTaskState.Done ? Now : null,
        stale,
        false,
        state is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput,
        cancelled,
        failed,
        null,
        1);

    private static TaskTransition Transition(AgentTaskState from, AgentTaskState to, bool hydration = false, TaskTransitionKind kind = TaskTransitionKind.StateChanged) =>
        new(kind, new TaskKey("codex", "conversation-1"), 1, from, to, "cause", "turn-1", hydration, Now);

    [Fact]
    public void HydrationTransitionsAreBaselineOnly()
    {
        var detector = new AttentionDetector(MonitorOptions.Default);
        detector.MarkHydrationComplete();
        var transitions = new[] { Transition(AgentTaskState.Idle, AgentTaskState.Working, hydration: true) };
        Assert.Empty(detector.Detect(new[] { Task("conversation-1", AgentTaskState.Working) }, transitions, Now));
        Assert.Empty(detector.Detect(new[] { Task("conversation-1", AgentTaskState.Done) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.Done, hydration: true) }, Now));
    }

    [Fact]
    public void AttentionStatesRaiseTheirSemanticEvents()
    {
        var detector = new AttentionDetector(MonitorOptions.Default);
        detector.MarkHydrationComplete();

        var permission = detector.Detect(new[] { Task("conversation-1", AgentTaskState.NeedsApproval) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.NeedsApproval) }, Now);
        Assert.Single(permission);
        Assert.Equal(AttentionEventKind.PermissionRequired, permission[0].Kind);

        var input = detector.Detect(new[] { Task("conversation-1", AgentTaskState.NeedsInput) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.NeedsInput) }, Now);
        Assert.Single(input);
        Assert.Equal(AttentionEventKind.InputRequired, input[0].Kind);

        var error = detector.Detect(new[] { Task("conversation-1", AgentTaskState.Error) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.Error) }, Now);
        Assert.Single(error);
        Assert.Equal(AttentionEventKind.TaskError, error[0].Kind);

        var completed = detector.Detect(new[] { Task("conversation-1", AgentTaskState.Done) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.Done) }, Now);
        Assert.Single(completed);
        Assert.Equal(AttentionEventKind.TaskCompleted, completed[0].Kind);
    }

    [Fact]
    public void StaleEvidenceAnnouncesNothing()
    {
        var detector = new AttentionDetector(MonitorOptions.Default);
        detector.MarkHydrationComplete();
        var events = detector.Detect(new[] { Task("conversation-1", AgentTaskState.NeedsApproval, stale: true) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.NeedsApproval) }, Now);
        Assert.Empty(events);
    }

    [Fact]
    public void NonBlockingInputRaisesAttentionOnceWithoutStateChange()
    {
        var detector = new AttentionDetector(MonitorOptions.Default);
        detector.MarkHydrationComplete();
        var task = Task("conversation-1", AgentTaskState.Working);
        var raised = detector.Detect(new[] { task }, new[] { Transition(AgentTaskState.Working, AgentTaskState.Working, kind: TaskTransitionKind.NonBlockingInputRaised) }, Now);
        Assert.Single(raised);
        Assert.Equal(AttentionEventKind.InputRequired, raised[0].Kind);
        Assert.Equal("input-requested-nonblocking", raised[0].Code);

        // A state change caused by ordinary work is not re-announced.
        var quiet = detector.Detect(new[] { task }, new[] { Transition(AgentTaskState.Working, AgentTaskState.Working) }, Now);
        Assert.Empty(quiet);
    }

    [Fact]
    public void AllTasksCompletedRequiresKnownPreviouslyRunningWork()
    {
        var detector = new AttentionDetector(MonitorOptions.Default);

        // Baseline: one running task during hydration.
        detector.Detect(new[] { Task("conversation-1", AgentTaskState.Working) }, new[] { Transition(AgentTaskState.Idle, AgentTaskState.Working, hydration: true) }, Now);
        detector.MarkHydrationComplete();

        var events = detector.Detect(new[] { Task("conversation-1", AgentTaskState.Done) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.Done) }, Now);
        Assert.Contains(events, item => item.Kind == AttentionEventKind.TaskCompleted);
        Assert.Contains(events, item => item.Kind == AttentionEventKind.AllTasksCompleted);

        // Starting new work re-arms the aggregate.
        detector.Detect(new[] { Task("conversation-1", AgentTaskState.Working) }, new[] { Transition(AgentTaskState.Done, AgentTaskState.Working) }, Now);
        var second = detector.Detect(new[] { Task("conversation-1", AgentTaskState.Done) }, new[] { Transition(AgentTaskState.Working, AgentTaskState.Done) }, Now);
        Assert.Contains(second, item => item.Kind == AttentionEventKind.AllTasksCompleted);
    }

    [Fact]
    public void AllTasksCompletedNeverComesFromStartupEmptyStateOrDisappearance()
    {
        var detector = new AttentionDetector(MonitorOptions.Default);
        detector.MarkHydrationComplete();

        // No previously running work: nothing to aggregate.
        Assert.Empty(detector.Detect(Array.Empty<AgentTask>(), Array.Empty<TaskTransition>(), Now));

        // Running work that disappears from the list is not a completion.
        detector.Detect(new[] { Task("conversation-1", AgentTaskState.Working) }, new[] { Transition(AgentTaskState.Idle, AgentTaskState.Working) }, Now);
        var disappeared = detector.Detect(Array.Empty<AgentTask>(), Array.Empty<TaskTransition>(), Now);
        Assert.DoesNotContain(disappeared, item => item.Kind == AttentionEventKind.AllTasksCompleted);
    }

    [Fact]
    public void AllTasksCompletedIsNotRaisedForCancellationOrError()
    {
        var detector = new AttentionDetector(MonitorOptions.Default);
        detector.Detect(new[] { Task("conversation-1", AgentTaskState.Working) }, new[] { Transition(AgentTaskState.Idle, AgentTaskState.Working, hydration: true) }, Now);
        detector.MarkHydrationComplete();

        var cancelled = detector.Detect(
            new[] { Task("conversation-1", AgentTaskState.Idle, cancelled: true) },
            new[] { Transition(AgentTaskState.Working, AgentTaskState.Idle) },
            Now);
        Assert.DoesNotContain(cancelled, item => item.Kind == AttentionEventKind.AllTasksCompleted);

        detector.Detect(new[] { Task("conversation-1", AgentTaskState.Working) }, new[] { Transition(AgentTaskState.Idle, AgentTaskState.Working) }, Now);
        var failed = detector.Detect(
            new[] { Task("conversation-1", AgentTaskState.Error, failed: true) },
            new[] { Transition(AgentTaskState.Working, AgentTaskState.Error) },
            Now);
        Assert.DoesNotContain(failed, item => item.Kind == AttentionEventKind.AllTasksCompleted);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var detector = new AttentionDetector(MonitorOptions.Default with { MaxAttentionHistory = 4 });
        detector.MarkHydrationComplete();
        for (var index = 0; index < 40; index++)
        {
            var from = index % 2 == 0 ? AgentTaskState.Working : AgentTaskState.NeedsApproval;
            var to = index % 2 == 0 ? AgentTaskState.NeedsApproval : AgentTaskState.Working;
            detector.Detect(new[] { Task("conversation-1", to) }, new[] { Transition(from, to) }, Now);
        }

        Assert.True(detector.History.Count <= 4);
    }
}
