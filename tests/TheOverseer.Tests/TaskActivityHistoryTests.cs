using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.Tests;

public sealed class TaskActivityHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProjectId Project = ProjectId.FromKey("project:alpha");

    private static TaskKey Key(string id = "01a0e000-0000-0000-0000-0000000000a1") =>
        TaskKey.For(HarnessIds.Codex, "codex", id);

    private static TaskActivityHistory History(TaskActivityOptions? options = null) =>
        new(options, TimeProvider.System);

    [Fact]
    public void RepeatingTheSameActionExtendsOneInterval()
    {
        var history = History();
        var key = Key();

        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "running-tests", Start);
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "running-tests", Start.AddSeconds(4));
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "running-tests", Start.AddSeconds(8));

        var lane = history.Lane(key, Start.AddSeconds(9))!;

        // Confirmed evidence is exactly what was observed; the bounded belief
        // window after it is a separate inferred interval, never extra certainty.
        var working = Assert.Single(lane.Intervals, interval => !interval.IsMarker && interval.Evidence == ActivityEvidence.Explicit);
        Assert.Equal(ActivityCategory.Working, working.Category);
        Assert.Equal(TimeSpan.FromSeconds(8), working.Duration);
        Assert.Contains(lane.Intervals, interval => interval.Evidence == ActivityEvidence.Inferred && interval.Category == ActivityCategory.Working);
    }

    [Fact]
    public void ActionChangesSplitAtTheObservedInstant()
    {
        var history = History();
        var key = Key();

        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Thinking, ActivityEvidence.Explicit, "reasoning", Start);
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "editing-file", Start.AddSeconds(10));

        var lane = history.Lane(key, Start.AddSeconds(11))!;

        // The confirmed thinking evidence is exactly the observation instant; the
        // switch to working is a confirmed interval starting at the new instant.
        var confirmed = lane.Intervals.Where(interval => interval.Evidence == ActivityEvidence.Explicit).ToArray();
        Assert.Contains(confirmed, interval => interval.Category == ActivityCategory.Thinking && interval.Start == Start);
        var working = Assert.Single(confirmed, interval => interval.Category == ActivityCategory.Working);
        Assert.Equal(Start.AddSeconds(10), working.Start);
        Assert.Equal(ActivityCategory.Working, lane.Current);
    }

    [Fact]
    public void MissingObservationBecomesAnExplicitUnknownTailNotIdle()
    {
        var history = History();
        var key = Key();
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "running-tests", Start);

        var lane = history.Lane(key, Start.AddSeconds(30))!;

        Assert.True(lane.CoverageUnknown);
        Assert.Equal(ActivityCategory.Working, lane.Current);
        Assert.Contains(lane.Intervals, interval => interval.Category == ActivityCategory.Unknown);
        Assert.DoesNotContain(lane.Intervals, interval => interval.Category == ActivityCategory.Idle);
    }

    [Fact]
    public void IdleIsOnlyReportedWhenItWasActuallyObserved()
    {
        var history = History();
        var key = Key();
        var never = Key("01a0e000-0000-0000-0000-0000000000a2");

        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Idle, ActivityEvidence.Explicit, "settled", Start);

        Assert.Equal(ActivityCategory.Idle, history.Lane(key, Start)!.Current);

        // A task with no observation at all has no lane: absence of evidence is
        // reported as unknown coverage by its caller, never as idle.
        Assert.Null(history.Lane(never, Start));
        Assert.True(history.Lane(key, Start.AddMinutes(5))!.CoverageUnknown);
    }

    [Fact]
    public void InferredActionIsVisibleAsEstimatedAndCompletionMarkersDoNotEraseWork()
    {
        var history = History();
        var key = Key();
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Thinking, ActivityEvidence.Inferred, "turn-active", Start);
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "running-command", Start.AddSeconds(5));
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Complete, ActivityEvidence.Explicit, "task-complete", Start.AddSeconds(20), isMarker: true);

        var lane = history.Lane(key, Start.AddSeconds(21))!;
        var marker = Assert.Single(lane.Intervals, interval => interval.IsMarker);
        Assert.Equal(ActivityCategory.Complete, marker.Category);
        Assert.Equal(ActivityEvidence.Inferred, lane.Intervals[0].Evidence);
        Assert.Contains(lane.Intervals, interval => interval.Category == ActivityCategory.Working && !interval.IsMarker);
    }

    [Fact]
    public void ProjectUnionIsOverlapNotSumOfTaskDurations()
    {
        var history = History();
        var first = Key("01a0e000-0000-0000-0000-0000000000b1");
        var second = Key("01a0e000-0000-0000-0000-0000000000b2");

        history.Record(first, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(second, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(first, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start.AddSeconds(30));
        history.Record(second, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start.AddSeconds(30));

        var strip = history.Project(Project, Start.AddSeconds(31));

        Assert.Equal(2, strip.MemberTaskCount);
        Assert.True(strip.ActiveObserved <= TimeSpan.FromSeconds(31), "active " + strip.ActiveObserved);
        Assert.True(strip.ActiveObserved >= TimeSpan.FromSeconds(30), "active " + strip.ActiveObserved);
        Assert.Single(strip.Intervals, interval => !interval.IsMarker);
    }

    [Fact]
    public void AttentionOutranksToolWorkInAProjectUnion()
    {
        var history = History();
        var working = Key("01a0e000-0000-0000-0000-0000000000c1");
        var waiting = Key("01a0e000-0000-0000-0000-0000000000c2");

        history.Record(working, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(waiting, Project, HarnessIds.Claude, ActivityCategory.Waiting, ActivityEvidence.Explicit, "approval", Start.AddSeconds(5));

        var strip = history.Project(Project, Start.AddSeconds(6));
        var waitingInterval = Assert.Single(strip.Intervals, interval => interval.Category == ActivityCategory.Waiting);
        Assert.Equal(Start.AddSeconds(5), waitingInterval.Start);
    }

    [Fact]
    public void AStaleMemberShowsKnownWorkPlusPartialCoverage()
    {
        var history = History();
        var fresh = Key("01a0e000-0000-0000-0000-0000000000d1");
        var stale = Key("01a0e000-0000-0000-0000-0000000000d2");

        history.Record(fresh, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(stale, Project, HarnessIds.Dsh, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(fresh, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start.AddSeconds(60));

        var strip = history.Project(Project, Start.AddSeconds(61));

        Assert.Contains(strip.Intervals, interval => interval.Category == ActivityCategory.Working && !interval.IsMarker);
        Assert.True(strip.CoverageUnknown);
        Assert.Equal(1, strip.ObservedTaskCount);
        Assert.Equal(2, strip.MemberTaskCount);
    }

    [Fact]
    public void WindowClipsOldIntervalsAndNeverFabricatesHistory()
    {
        var history = History();
        var key = Key();
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Thinking, ActivityEvidence.Explicit, "reasoning", Start.AddSeconds(10));

        var lane = history.Lane(key, Start.AddMinutes(90))!;

        Assert.DoesNotContain(lane.Intervals, interval => interval.End < Start.AddMinutes(85));
        Assert.True(lane.CoverageUnknown);
    }

    [Fact]
    public void IntervalCapacityEvictsTheOldestEntriesPerTask()
    {
        var options = TaskActivityOptions.Default with { MaximumIntervalsPerTask = 4 };
        var history = History(options);
        var key = Key();

        for (var index = 0; index < 20; index++)
        {
            var category = index % 2 == 0 ? ActivityCategory.Working : ActivityCategory.Thinking;
            history.Record(key, Project, HarnessIds.Codex, category, ActivityEvidence.Explicit, "step", Start.AddSeconds(index * 5));
        }

        // Stored capacity is per task; the projection may add the open action's
        // confirmed, inferred and unknown segments on top of the retained ones.
        Assert.True(history.IntervalCount <= options.MaximumIntervalsPerTask + 1, "stored " + history.IntervalCount);
        var lane = history.Lane(key, Start.AddSeconds(101))!;
        Assert.True(lane.Intervals.Count <= options.MaximumIntervalsPerTask + 3, "projected " + lane.Intervals.Count);
    }

    [Fact]
    public void ReassignmentAffectsFutureIntervalsOnly()
    {
        var history = History();
        var key = Key();
        var other = ProjectId.FromKey("project:beta");

        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Reassign(key, other, Start.AddSeconds(10));
        history.Record(key, other, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start.AddSeconds(10));

        var alphaStrip = history.Project(Project, Start.AddSeconds(11));
        var betaStrip = history.Project(other, Start.AddSeconds(11));

        // Past attribution is not rewritten; the reassignment owns future time.
        Assert.All(alphaStrip.Intervals, interval => Assert.Equal(Project, interval.Project));
        Assert.All(betaStrip.Intervals, interval => Assert.Equal(other, interval.Project));
        Assert.Equal(other, history.Lane(key, Start.AddSeconds(11))!.ProjectId);
    }

    [Fact]
    public void GeometryStaysBoundedAndKeepsBriefAttentionMarkers()
    {
        var history = History();
        var key = Key();
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Waiting, ActivityEvidence.Explicit, "approval", Start.AddSeconds(600));
        history.Record(key, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start.AddSeconds(601));

        var now = Start.AddSeconds(700);
        var strip = history.Project(Project, now);
        var bins = ActivityTimelineGeometry.Bin(strip, now, TimeSpan.FromMinutes(60), 240);

        Assert.True(bins.Count <= 240, "bins " + bins.Count);
        Assert.Contains(bins, bin => bin.Category == ActivityCategory.Waiting);
        Assert.Contains(bins, bin => bin.Category == ActivityCategory.Working);
    }

    [Fact]
    public void HarnessQualifiedKeysDoNotCollideAcrossSources()
    {
        var history = History();
        var conversation = "01a0e000-0000-0000-0000-0000000000e1";
        var codex = TaskKey.For(HarnessIds.Codex, "codex", conversation);
        var dsh = TaskKey.For(HarnessIds.Dsh, "dsh-default", conversation);

        history.Record(codex, Project, HarnessIds.Codex, ActivityCategory.Working, ActivityEvidence.Explicit, "edit", Start);
        history.Record(dsh, Project, HarnessIds.Dsh, ActivityCategory.Thinking, ActivityEvidence.Explicit, "reasoning", Start);

        Assert.NotEqual(codex, dsh);
        Assert.Equal(2, history.HistoryCount);
        Assert.Equal(2, history.Lanes(Start).Count);
    }
}
