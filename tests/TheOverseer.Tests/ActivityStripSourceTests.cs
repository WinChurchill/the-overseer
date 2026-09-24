using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>
/// The strip source projects producer-side observed activity into bounded bins.
/// Unknown coverage must never be rendered as confirmed idle, and an unobserved
/// task must not invent a strip at all.
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
        Assert.Contains(ActivityCategory.Working, bins);
        Assert.DoesNotContain(ActivityCategory.Idle, bins);
        Assert.Contains(ActivityCategory.Unknown, bins);
        _ = coordinator.DisposeAsync();
    }

    [Fact]
    public void ATaskLaneRendersItsOwnBinsAndAnUnknownTaskRendersNothing()
    {
        var (coordinator, source) = Create();
        var key = TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000302");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Now, new[] { Task(key) }));

        var bins = source.TaskBins(key, Now.AddSeconds(5));
        Assert.Contains(ActivityCategory.Working, bins);

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
        Assert.All(bins, category => Assert.Equal(ActivityCategory.Unknown, category));
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

        Assert.DoesNotContain(ActivityCategory.Working, bins.Where((_, index) => index >= bins.Count - 15));
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

        var first = new[] { ActivityCategory.Working, ActivityCategory.Unknown };
        row.UpdateActivity(first, Array.Empty<ActivityCategory>());
        row.UpdateActivity(first, Array.Empty<ActivityCategory>());
        Assert.Equal(1, raised);
        Assert.Equal(first, row.ProjectBins);

        row.UpdateActivity(new[] { ActivityCategory.Waiting, ActivityCategory.Unknown }, Array.Empty<ActivityCategory>());
        Assert.Equal(2, raised);
    }
}
