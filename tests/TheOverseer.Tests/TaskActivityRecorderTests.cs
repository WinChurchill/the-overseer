using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class TaskActivityRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProjectId Project = ProjectId.FromKey("project:alpha");

    private static AgentTask Task(
        AgentTaskState state = AgentTaskState.Working,
        AgentActionKind action = AgentActionKind.RunningTests,
        EvidenceKind evidence = EvidenceKind.Explicit,
        bool stale = false,
        string? detail = "test-runner",
        string? attention = null) =>
        VoiceTestData.Task(state: state, stale: stale) with
        {
            Action = new AgentAction(action, evidence, detail),
            Evidence = evidence,
            AttentionCode = attention,
            // Observations carry the task's own instant: align it with this test's
            // clock so the recording is inside the freshness window.
            FirstSeenAt = Start,
            LastActivityAt = Start,
            StateSince = Start,
            Project = new ProjectDescriptor(Project, "alpha", @"c:\work\alpha", ProjectResolution.ConfiguredRoot),
        };

    [Fact]
    public void ToolWorkIsRecordedAsWorkingWithItsStructuralDetail()
    {
        var history = new TaskActivityHistory(timeProvider: TimeProvider.System);
        var task = Task();

        TaskActivityRecorder.Record(history, VoiceTestData.Snapshot(Start, new[] { task }));

        var lane = history.Lane(task.Key, Start)!;
        Assert.Equal(ActivityCategory.Working, lane.Current);
        Assert.Equal(Project, lane.ProjectId);
        Assert.Equal("test-runner", lane.CurrentDetailCode);
    }

    [Fact]
    public void AttentionBecomesWaitingNotWorking()
    {
        var history = new TaskActivityHistory();
        var task = Task(state: AgentTaskState.NeedsApproval, action: AgentActionKind.WaitingForApproval, attention: "approval-pending");

        TaskActivityRecorder.Record(history, VoiceTestData.Snapshot(Start, new[] { task }));

        var lane = history.Lane(task.Key, Start)!;
        Assert.Equal(ActivityCategory.Waiting, lane.Current);
        Assert.DoesNotContain(lane.Intervals, interval => interval.Category == ActivityCategory.Idle);
    }

    [Fact]
    public void StaleAndUnhydratedTasksStayUnknownInsteadOfIdle()
    {
        var history = new TaskActivityHistory();
        var stale = Task(stale: true);
        var unhydrated = VoiceTestData.Task() with { Evidence = EvidenceKind.None };

        TaskActivityRecorder.Record(history, VoiceTestData.Snapshot(Start, new[] { stale, unhydrated }));

        Assert.Equal(ActivityCategory.Unknown, history.Lane(stale.Key, Start)!.Current);
        Assert.Equal(ActivityCategory.Unknown, history.Lane(unhydrated.Key, Start)!.Current);
        Assert.DoesNotContain(history.Lanes(Start), lane => lane.Current == ActivityCategory.Idle);
    }

    [Fact]
    public void CompletionAndFailureAreMarkersOverTheActionTimeline()
    {
        var history = new TaskActivityHistory();
        var working = Task();
        var done = working with { State = AgentTaskState.Done, Action = new AgentAction(AgentActionKind.Completed, EvidenceKind.Explicit, "turn-complete") };

        TaskActivityRecorder.Record(history, VoiceTestData.Snapshot(Start, new[] { working }));
        TaskActivityRecorder.Record(history, VoiceTestData.Snapshot(Start.AddSeconds(5), new[] { done }));

        var lane = history.Lane(done.Key, Start.AddSeconds(5))!;
        Assert.Contains(lane.Intervals, interval => interval.IsMarker && interval.Category == ActivityCategory.Complete);
        Assert.Contains(lane.Intervals, interval => !interval.IsMarker && interval.Category == ActivityCategory.Working);

        // The terminal marker closes the action timeline: nothing after it is
        // invented, and the completed work stays visible.
        Assert.DoesNotContain(lane.Intervals, interval => !interval.IsMarker && interval.Start >= Start.AddSeconds(5));
    }

    [Fact]
    public void InferredThinkingIsMarkedEstimatedWhileExplicitPlanWorkIsPlanning()
    {
        var history = new TaskActivityHistory();
        var inferred = VoiceTestData.Task() with
        {
            Action = new AgentAction(AgentActionKind.Thinking, EvidenceKind.Inferred, "turn-active"),
            Evidence = EvidenceKind.Inferred,
            Project = new ProjectDescriptor(Project, "alpha", @"c:\work\alpha", ProjectResolution.ConfiguredRoot),
        };
        var planned = VoiceTestData.Task() with
        {
            Action = new AgentAction(AgentActionKind.Thinking, EvidenceKind.Explicit, "plan-update"),
            Evidence = EvidenceKind.Explicit,
            Project = new ProjectDescriptor(Project, "alpha", @"c:\work\alpha", ProjectResolution.ConfiguredRoot),
        };

        var inferredClass = TaskActivityRecorder.Classify(inferred);
        var plannedClass = TaskActivityRecorder.Classify(planned);

        Assert.Equal(ActivityCategory.Thinking, inferredClass.Category);
        Assert.Equal(ActivityEvidence.Inferred, inferredClass.Evidence);
        Assert.Equal(ActivityCategory.Planning, plannedClass.Category);
        Assert.Equal(ActivityEvidence.Explicit, plannedClass.Evidence);
        _ = history;
    }

    [Fact]
    public void RecordingIsIdempotentForAnUnchangedSnapshot()
    {
        var history = new TaskActivityHistory();
        var task = Task();
        var snapshot = VoiceTestData.Snapshot(Start, new[] { task });

        TaskActivityRecorder.Record(history, snapshot);
        TaskActivityRecorder.Record(history, snapshot with { GeneratedAt = Start.AddSeconds(1) });

        var lane = history.Lane(task.Key, Start.AddSeconds(1))!;

        // Repeating the same observation adds nothing: exactly one confirmed
        // interval (the bounded inferred belief tail is a separate, honest segment).
        Assert.Single(lane.Intervals, interval => !interval.IsMarker && interval.Evidence == ActivityEvidence.Explicit);
        Assert.Equal(ActivityCategory.Working, lane.Current);
    }
}
