using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>
/// The strip source projects producer-side observed activity into bounded bins that
/// keep the evidence, coverage, interval and detail the category-only transport used
/// to drop. Unknown coverage must never render as confirmed idle, an unobserved task
/// must not invent a strip at all, and one refresh must not re-project per row.
/// </summary>
public sealed class ActivityStripSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProjectDescriptor Project = new(
        ProjectId.FromKey("project:alpha"),
        "alpha",
        @"D:\work\alpha",
        ProjectResolution.ConfiguredRoot);

    private static AgentTask Task(TaskKey key, AgentTaskState state = AgentTaskState.Working, bool stale = false) =>
        VoiceTestData.Task(key: key, state: state, stale: stale) with
        {
            ProfileId = key.ProfileId,
            ConversationId = key.ConversationId,
            Action = new AgentAction(AgentActionKind.RunningTests, EvidenceKind.Explicit, "test-runner"),
            Evidence = EvidenceKind.Explicit,
            Project = Project,
            FirstSeenAt = Now,
            LastActivityAt = Now,
            StateSince = Now,
        };

    private static (NotificationCoordinator Coordinator, CoordinatorActivityStripSource Source) Create()
    {
        var settings = new VoiceSettings { Muted = true, Provider = "pack" };
        var manager = new VoiceManager(
            new FakeVoiceProvider(),
            new FakeVoicePlayer(),
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero },
            TimeProvider.System,
            muted: true);
        var coordinator = new NotificationCoordinator(
            new ActivityHistory(),
            new VoiceEventPolicy(),
            manager,
            TimeProvider.System,
            settings);
        coordinator.Start();
        return (coordinator, new CoordinatorActivityStripSource(coordinator, TimeProvider.System));
    }

    [Fact]
    public void AProjectWithObservedWorkShowsBoundedBinsAndNeverIdle()
    {
        var (coordinator, source) = Create();
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000301");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Now, new[] { Task(key) }));

        // Projected a moment later, as the 1 s HUD tick does: the window is
        // half-open, so an interval starting exactly at the query instant is out.
        var bins = source.ProjectBins(Project.Id, Now.AddSeconds(5));

        Assert.InRange(bins.Count, 1, 56);
        Assert.Contains(bins, bin => bin.Category == ActivityCategory.Working);
        Assert.DoesNotContain(bins, bin => bin.Category == ActivityCategory.Idle);
        Assert.Contains(bins, bin => bin.Category == ActivityCategory.Unknown);

        // Unknown is coverage, never a confirmed idle cell.
        var unknown = bins.First(bin => bin.Category == ActivityCategory.Unknown);
        Assert.Equal(ActivityCoverage.Unknown, unknown.Coverage);
        Assert.Contains("not idle", unknown.Description, StringComparison.OrdinalIgnoreCase);
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void BinsCarryCategoryEvidenceCoverageIntervalAndDetail()
    {
        var (coordinator, source) = Create();
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000306");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Now, new[] { Task(key) }));

        var bins = source.ProjectBins(Project.Id, Now.AddSeconds(5));
        var working = bins.First(bin => bin.Category == ActivityCategory.Working);

        Assert.Equal(ActivityEvidence.Explicit, working.Evidence);
        Assert.Equal(ActivityCoverage.Complete, working.Coverage);
        Assert.Equal("test-runner", working.DetailCode);
        Assert.True(working.End > working.Start);
        Assert.InRange(working.Duration.TotalSeconds, 1, 90);
        Assert.Equal(1, working.ConcurrentTasks);

        // The tooltip/automation text is the observable contract for the transport.
        Assert.Contains("Working", working.Description, StringComparison.Ordinal);
        Assert.Contains("explicit observation", working.Description, StringComparison.Ordinal);
        Assert.Contains("complete coverage", working.Description, StringComparison.Ordinal);
        Assert.Contains("test-runner", working.Description, StringComparison.Ordinal);
        Assert.Contains("12:", working.Description, StringComparison.Ordinal);
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void AnInferredTailIsMarkedInferredAndPartialRatherThanConfirmed()
    {
        var (coordinator, source) = Create();
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000307");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Now, new[] { Task(key) }));

        // Ninety seconds after the last observation the ten-second belief window has
        // passed: the confirmed second, the inferred stretch and the unobserved tail
        // must be three different claims, never one "working" bin.
        var bins = source.TaskBins(key, Now.AddSeconds(90));
        var known = bins.Where(bin => bin.Category == ActivityCategory.Working).ToArray();

        Assert.NotEmpty(known);
        Assert.Contains(known, bin => bin.Evidence == ActivityEvidence.Inferred);
        Assert.Contains(known, bin => bin.Coverage == ActivityCoverage.Partial);
        Assert.DoesNotContain(known, bin => bin.Evidence == ActivityEvidence.Inferred && bin.Coverage == ActivityCoverage.Complete);
        Assert.Contains(bins, bin => bin.Category == ActivityCategory.Unknown && bin.Coverage == ActivityCoverage.Unknown);
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void OneRefreshProjectsEachDistinctProjectAndTaskOnlyOnce()
    {
        var (coordinator, source) = Create();
        var first = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000308");
        var second = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000309");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Now, new[] { Task(first), Task(second) }));
        var at = Now.AddSeconds(5);

        // Same refresh instant: the projection is cached, so repeated row asks for
        // one project return the very same projection instead of rescanning lanes.
        Assert.Same(source.ProjectBins(Project.Id, at), source.ProjectBins(Project.Id, at));
        Assert.Same(source.TaskBins(first, at), source.TaskBins(first, at));
        Assert.NotSame(source.TaskBins(first, at), source.TaskBins(second, at));

        // A new tick is a new projection, and it is still bounded.
        Assert.NotSame(source.ProjectBins(Project.Id, at), source.ProjectBins(Project.Id, at.AddSeconds(1)));
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void TheViewModelAsksEachProjectOncePerTickAndEachTaskOnce()
    {
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-00000000030a");
        var other = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-00000000030b");
        var counting = new CountingStripSource();
        var viewModel = new HudViewModel(HudSettings.Default);
        viewModel.UseActivitySource(counting);
        viewModel.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { Task(key), Task(other) }));

        // Nothing is projected while every history disclosure is closed.
        counting.Reset();
        viewModel.UpdateAges(Now.AddSeconds(5));
        Assert.Equal(0, counting.ProjectCalls);
        Assert.Equal(0, counting.TaskCalls);

        // One disclosed project and one disclosed task lane: each distinct project is
        // projected once per tick and only the open task lane is projected at all.
        viewModel.Projects[0].IsHistoryOpen = true;
        viewModel.Tasks[0].IsHistoryOpen = true;
        counting.Reset();
        viewModel.UpdateAges(Now.AddSeconds(6));

        Assert.Equal(1, counting.ProjectCalls);
        Assert.Equal(1, counting.TaskCalls);
        Assert.Single(viewModel.Projects);
        Assert.NotEmpty(viewModel.Projects[0].Bins);
        Assert.NotEmpty(viewModel.Tasks[0].TaskBins);
        Assert.Empty(viewModel.Tasks[1].TaskBins);
    }

    [Fact]
    public void ATaskLaneRendersItsOwnBinsAndAnUnknownTaskRendersNothing()
    {
        var (coordinator, source) = Create();
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000302");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Now, new[] { Task(key) }));

        var bins = source.TaskBins(key, Now.AddSeconds(5));
        Assert.Contains(bins, bin => bin.Category == ActivityCategory.Working);

        var unknown = TaskKey.For(HarnessIds.Dsh, "dsh-default", "01a0e000-0000-0000-0000-000000000303");
        Assert.Empty(source.TaskBins(unknown, Now));
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void WithoutAnyObservationEverythingIsUnknownCoverage()
    {
        var (coordinator, source) = Create();
        var unobservedProject = ProjectId.FromKey("project:never");

        var bins = source.ProjectBins(unobservedProject, Now);

        Assert.NotEmpty(bins);
        Assert.All(bins, bin =>
        {
            Assert.Equal(ActivityCategory.Unknown, bin.Category);
            Assert.Equal(ActivityCoverage.Unknown, bin.Coverage);
        });
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void AStaleLaneAgesToUnknownInsteadOfClaimingCurrentWork()
    {
        var (coordinator, source) = Create();
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000304");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Now, new[] { Task(key) }));

        // Twenty minutes later the ten-second freshness bound has long passed.
        var later = Now.AddMinutes(20);
        var bins = source.TaskBins(key, later);

        Assert.DoesNotContain(bins.Where((_, index) => index >= bins.Count - 15),
            bin => bin.Category == ActivityCategory.Working);
        Assert.Contains(bins, bin => bin.Category == ActivityCategory.Unknown);
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void RowStripsOnlyRaiseAChangeWhenTheBinsActuallyChanged()
    {
        var row = new TaskRowViewModel(Task(TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000305")), false, string.Empty);
        var raised = 0;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TaskRowViewModel.ProjectBins))
            {
                raised++;
            }
        };

        var first = new[]
        {
            Bin(ActivityCategory.Working, ActivityEvidence.Explicit, ActivityCoverage.Complete),
            Bin(ActivityCategory.Unknown, ActivityEvidence.Unknown, ActivityCoverage.Unknown),
        };
        row.UpdateActivity(first, Array.Empty<ActivityStripBin>());
        row.UpdateActivity(first, Array.Empty<ActivityStripBin>());
        Assert.Equal(1, raised);
        Assert.Equal(first, row.ProjectBins);

        // A category change and a coverage-only change both count as a real change.
        row.UpdateActivity(new[] { Bin(ActivityCategory.Waiting, ActivityEvidence.Explicit, ActivityCoverage.Complete), first[1] }, Array.Empty<ActivityStripBin>());
        Assert.Equal(2, raised);
        row.UpdateActivity(new[] { Bin(ActivityCategory.Waiting, ActivityEvidence.Explicit, ActivityCoverage.Partial), first[1] }, Array.Empty<ActivityStripBin>());
        Assert.Equal(3, raised);
    }

    [Fact]
    public void ProjectStripTooltipSummarizesUnknownCoverageWithoutCallingItIdle()
    {
        var row = new TaskRowViewModel(Task(TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-00000000030c")), false, string.Empty);
        row.UpdateActivity(new[]
        {
            Bin(ActivityCategory.Working, ActivityEvidence.Explicit, ActivityCoverage.Complete),
            Bin(ActivityCategory.Unknown, ActivityEvidence.Unknown, ActivityCoverage.Unknown),
        }, Array.Empty<ActivityStripBin>());

        Assert.Contains("Last hour", row.ProjectStripText, StringComparison.Ordinal);
        Assert.Contains("Working", row.ProjectStripText, StringComparison.Ordinal);
        Assert.Contains("no observation", row.ProjectStripText, StringComparison.OrdinalIgnoreCase);
    }

    private static ActivityStripBin Bin(ActivityCategory category, ActivityEvidence evidence, ActivityCoverage coverage) =>
        new(Now, Now.AddMinutes(1), category, evidence, coverage, "test", 1, false);

    /// <summary>Counts how often the view model asks for a projection.</summary>
    private sealed class CountingStripSource : IActivityStripSource
    {
        public int ProjectCalls { get; private set; }

        public int TaskCalls { get; private set; }

        public void Reset()
        {
            ProjectCalls = 0;
            TaskCalls = 0;
        }

        public IReadOnlyList<ActivityStripBin> ProjectBins(ProjectId projectId, DateTimeOffset now)
        {
            ProjectCalls++;
            return new[] { Bin(ActivityCategory.Working, ActivityEvidence.Explicit, ActivityCoverage.Complete) };
        }

        public IReadOnlyList<ActivityStripBin> TaskBins(TaskKey key, DateTimeOffset now)
        {
            TaskCalls++;
            return new[] { Bin(ActivityCategory.Working, ActivityEvidence.Explicit, ActivityCoverage.Complete) };
        }
    }
}
