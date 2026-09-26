using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>
/// Project-first rows: an expandable project row per project, ordered
/// attention -> active -> recent with a stable identity tie-break (no jitter between
/// ticks), expansion that survives snapshot updates without stopping observation, and
/// labels/summaries that never expose a local root path.
/// </summary>
public sealed class ProjectViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static AgentTask Task(
        string conversation,
        ProjectDescriptor project,
        AgentTaskState state = AgentTaskState.Working,
        bool stale = false,
        bool needsHuman = false,
        string harnessId = HarnessIds.Codex,
        string source = "codex") =>
        VoiceTestData.Task(key: TaskKey.For(harnessId, source, conversation), state: state, stale: stale) with
        {
            ProfileId = source,
            ConversationId = conversation,
            NeedsHuman = needsHuman,
            Project = project,
            LastActivityAt = Now,
            FirstSeenAt = Now,
            StateSince = Now,
        };

    private static ProjectDescriptor Project(string name) => new(
        ProjectId.FromKey("project:" + name),
        name,
        @"D:\secret\customer\" + name,
        ProjectResolution.ConfiguredRoot);

    private static HudViewModel View(params AgentTask[] tasks)
    {
        var viewModel = new HudViewModel(HudSettings.Default);
        viewModel.ApplySnapshot(VoiceTestData.Snapshot(Now, tasks));
        return viewModel;
    }

    [Fact]
    public void ProjectRowsAreOrderedAttentionThenActiveThenUnassignedLast()
    {
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000201", ProjectDescriptor.UnassignedProject),
            Task("01a0e000-0000-0000-0000-000000000202", Project("beta")),
            Task("01a0e000-0000-0000-0000-000000000203", Project("alpha"), state: AgentTaskState.NeedsApproval, needsHuman: true));

        var keys = viewModel.Projects.Select(project => project.Key).ToArray();

        Assert.Equal(3, keys.Length);
        Assert.StartsWith("0|", keys[0], StringComparison.Ordinal);
        Assert.Equal("alpha", viewModel.Projects[0].Name);
        Assert.StartsWith("1|", keys[1], StringComparison.Ordinal);
        Assert.EndsWith("9|~unassigned", keys[^1], StringComparison.Ordinal);
        Assert.Equal("Unassigned", viewModel.Projects[^1].Name);
        Assert.Equal("0 working · 1 needs you · Codex", viewModel.Projects[0].SummaryText);
    }

    [Fact]
    public void RowsOfOneProjectStayContiguousAndOrderedByAttention()
    {
        var beta = Project("beta");
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000211", beta),
            Task("01a0e000-0000-0000-0000-000000000212", beta, state: AgentTaskState.NeedsInput, needsHuman: true),
            Task("01a0e000-0000-0000-0000-000000000213", Project("alpha"), state: AgentTaskState.Error));

        Assert.Equal(2, viewModel.Projects.Count);
        var betaRow = viewModel.Projects.Single(project => project.Name == "beta");
        Assert.Equal(new[] { "212", "211" }, betaRow.Tasks.Select(row => row.Key.ConversationId[^3..]));
        Assert.All(betaRow.Tasks, row => Assert.Equal(betaRow.Key, row.ProjectGroupKey));
        Assert.Single(viewModel.Projects.Single(project => project.Name == "alpha").Tasks);
    }

    [Fact]
    public void OrderAndExpansionAreStableAcrossSnapshotUpdates()
    {
        var beta = Project("beta");
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000221", beta),
            Task("01a0e000-0000-0000-0000-000000000222", beta, harnessId: HarnessIds.Dsh, source: "dsh-default"),
            Task("01a0e000-0000-0000-0000-000000000223", Project("alpha"), state: AgentTaskState.NeedsApproval, needsHuman: true));

        var firstOrder = viewModel.Projects.Select(project => project.Key).ToArray();
        var collapsed = viewModel.Projects[^1];
        viewModel.ToggleProject(collapsed);
        Assert.False(collapsed.IsExpanded);
        Assert.True(viewModel.AnyProjectCollapsed);

        // A later snapshot with different counts must not reorder projects, lose the
        // collapse, or drop the rows the monitor still observes.
        var snapshot = VoiceTestData.Snapshot(Now.AddMinutes(3), new[]
        {
            Task("01a0e000-0000-0000-0000-000000000221", beta),
            Task("01a0e000-0000-0000-0000-000000000222", beta, harnessId: HarnessIds.Dsh, source: "dsh-default"),
            Task("01a0e000-0000-0000-0000-000000000223", Project("alpha"), state: AgentTaskState.NeedsApproval, needsHuman: true),
        });
        viewModel.ApplySnapshot(snapshot);
        viewModel.UpdateAges(Now.AddMinutes(3));

        Assert.Equal(firstOrder, viewModel.Projects.Select(project => project.Key));
        var stillCollapsed = viewModel.Projects.Single(project => project.Name == collapsed.Name);
        Assert.False(stillCollapsed.IsExpanded);
        Assert.All(stillCollapsed.Tasks, row => Assert.False(row.IsProjectExpanded));
        Assert.Equal(3, viewModel.Tasks.Count);
    }

    [Fact]
    public void ATickDoesNotJitterProjectOrderWhenNothingChanged()
    {
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000231", Project("zeta")),
            Task("01a0e000-0000-0000-0000-000000000232", Project("alpha"), state: AgentTaskState.NeedsInput, needsHuman: true),
            Task("01a0e000-0000-0000-0000-000000000233", Project("mid")));

        var order = viewModel.Projects.Select(project => project.Key).ToArray();
        for (var tick = 1; tick <= 5; tick++)
        {
            viewModel.UpdateAges(Now.AddSeconds(tick));
            Assert.Equal(order, viewModel.Projects.Select(project => project.Key));
        }
    }

    [Fact]
    public void ProjectsThatShareADisplayNameStaySeparate()
    {
        // The same basename in two roots is two projects: identity, not the label,
        // decides the group, so they can never merge into one row.
        var first = new ProjectDescriptor(ProjectId.FromRoot(@"c:\one\repo"), "repo", @"c:\one\repo", ProjectResolution.GitRoot);
        var second = new ProjectDescriptor(ProjectId.FromRoot(@"c:\two\repo"), "repo", @"c:\two\repo", ProjectResolution.GitRoot);

        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000241", first),
            Task("01a0e000-0000-0000-0000-000000000242", second));

        Assert.Equal(2, viewModel.Projects.Count);
        Assert.All(viewModel.Projects, project => Assert.Equal("repo", project.Name));
        Assert.NotEqual(viewModel.Projects[0].Key, viewModel.Projects[1].Key);
        Assert.All(viewModel.Projects, project => Assert.Single(project.Tasks));
    }

    [Fact]
    public void CollapsingAProjectKeepsItsRowsObserved()
    {
        var counting = new CountingStripSource();
        var beta = Project("beta");
        var viewModel = new HudViewModel(HudSettings.Default);
        viewModel.UseActivitySource(counting);
        viewModel.ApplySnapshot(VoiceTestData.Snapshot(Now, new[]
        {
            Task("01a0e000-0000-0000-0000-000000000251", beta),
            Task("01a0e000-0000-0000-0000-000000000252", beta),
        }));

        viewModel.SetAllProjectsExpanded(false);
        counting.Reset();
        viewModel.UpdateAges(Now.AddSeconds(5));

        // Collapsing is a presentation state: the rows the monitor owns keep working
        // and the closed history projects no geometry at all.
        Assert.Equal(0, counting.ProjectCalls);
        Assert.Equal(0, counting.TaskCalls);
        Assert.Equal(2, viewModel.Tasks.Count);
        Assert.All(viewModel.Tasks, row => Assert.False(row.IsProjectExpanded));

        // Disclosing the history resumes exactly one bounded projection per tick.
        viewModel.Projects[0].IsHistoryOpen = true;
        counting.Reset();
        viewModel.UpdateAges(Now.AddSeconds(6));
        Assert.Equal(1, counting.ProjectCalls);
        Assert.Equal(0, counting.TaskCalls);
    }

    [Fact]
    public void LegendExplainsEveryCategoryAndSaysUnknownIsNotIdle()
    {
        var viewModel = View(Task("01a0e000-0000-0000-0000-000000000261", Project("beta")));

        Assert.Equal(Enum.GetValues<ActivityCategory>().Length, viewModel.Legend.Count);
        foreach (var category in Enum.GetValues<ActivityCategory>())
        {
            var entry = viewModel.Legend.Single(item => item.Category == category);
            Assert.False(string.IsNullOrWhiteSpace(entry.Label));
            Assert.False(string.IsNullOrWhiteSpace(entry.Meaning));
        }

        var unknown = viewModel.Legend.Single(item => item.Category == ActivityCategory.Unknown);
        Assert.Contains("not idle", unknown.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no observation", unknown.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not idle", unknown.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("now at the right", viewModel.LegendNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectSummaryDisclosesPartialCoverageAndNeverExposesARoot()
    {
        var beta = Project("beta");
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000271", beta),
            Task("01a0e000-0000-0000-0000-000000000272", beta, stale: true));

        Assert.True(viewModel.Projects[0].IsPartial);
        Assert.Contains("partial", viewModel.Projects[0].SummaryText, StringComparison.Ordinal);
        foreach (var project in viewModel.Projects)
        {
            Assert.DoesNotContain("secret", project.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", project.SummaryText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer", project.SummaryText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", project.HeaderTooltip, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GroupKeysCountsAndHarnessBadgesRemainOnTheTaskRows()
    {
        var beta = Project("beta");
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000281", beta),
            Task("01a0e000-0000-0000-0000-000000000282", beta, harnessId: HarnessIds.Dsh, source: "dsh-default"));

        Assert.Single(viewModel.Tasks.Select(row => row.ProjectGroupKey).Distinct(StringComparer.Ordinal));
        var row = viewModel.Tasks[0];
        Assert.Equal("beta", row.ProjectGroupLabel);
        Assert.Contains("2 working", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.Contains("0 needs you", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.Contains("Codex", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.Contains("dsh", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", row.ProjectGroupMeta, StringComparison.Ordinal);
    }

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
            return Array.Empty<ActivityStripBin>();
        }

        public IReadOnlyList<ActivityStripBin> TaskBins(TaskKey key, DateTimeOffset now)
        {
            TaskCalls++;
            return Array.Empty<ActivityStripBin>();
        }
    }
}
