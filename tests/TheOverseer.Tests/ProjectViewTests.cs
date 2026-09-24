using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>
/// Project-first row grouping: rows carry a sortable project key so one header is
/// rendered per project, ordered attention → active → recent with Unassigned last,
/// and the header summary never exposes a local root path.
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
    public void RowsAreGroupedAttentionFirstThenActiveThenUnassignedLast()
    {
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000201", ProjectDescriptor.UnassignedProject),
            Task("01a0e000-0000-0000-0000-000000000202", Project("beta")),
            Task("01a0e000-0000-0000-0000-000000000203", Project("alpha"), state: AgentTaskState.NeedsApproval, needsHuman: true));

        var keys = viewModel.Tasks.Select(row => row.ProjectGroupKey).ToArray();

        Assert.StartsWith("0|", keys[0], StringComparison.Ordinal);
        Assert.All(keys.Take(1), key => Assert.Equal(keys[0], key));
        Assert.Contains(keys, key => key.StartsWith("1|", StringComparison.Ordinal));
        Assert.EndsWith("9|~unassigned", keys[^1], StringComparison.Ordinal);
        Assert.Equal("Unassigned", viewModel.Tasks[^1].ProjectGroupLabel);
    }

    [Fact]
    public void OneProjectSharesAGroupKeyAndSummarizesCountsAndHarnesses()
    {
        var beta = Project("beta");
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000211", beta),
            Task("01a0e000-0000-0000-0000-000000000212", beta, harnessId: HarnessIds.Dsh, source: "dsh-default"));

        Assert.Single(viewModel.Tasks.Select(row => row.ProjectGroupKey).Distinct(StringComparer.Ordinal));
        var row = viewModel.Tasks[0];
        Assert.Equal("beta", row.ProjectGroupLabel);
        Assert.Contains("2 working", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.Contains("0 needs you", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.Contains("Codex", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.Contains("dsh", row.ProjectGroupMeta, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", row.ProjectGroupMeta, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleMemberMakesTheGroupSummaryDisclosePartialCoverage()
    {
        var beta = Project("beta");
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000221", beta),
            Task("01a0e000-0000-0000-0000-000000000222", beta, stale: true));

        Assert.Contains("partial", viewModel.Tasks[0].ProjectGroupMeta, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupLabelsAndSummariesNeverExposeALocalRoot()
    {
        var viewModel = View(
            Task("01a0e000-0000-0000-0000-000000000231", Project("gamma")),
            Task("01a0e000-0000-0000-0000-000000000232", ProjectDescriptor.UnassignedProject));

        foreach (var row in viewModel.Tasks)
        {
            Assert.DoesNotContain("secret", row.ProjectGroupLabel, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", row.ProjectGroupMeta, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer", row.ProjectGroupMeta, StringComparison.OrdinalIgnoreCase);
        }
    }
}
