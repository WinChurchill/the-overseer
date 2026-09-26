using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class HudViewModelTests
{
    private static HudViewModel CreateViewModel() => new(new HudSettings());

    [Fact]
    public void CompleteCountsDisplayPlainNumbers()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CompleteSnapshot(3, 1));

        Assert.Equal("3", viewModel.WorkingCountText);
        Assert.Equal("1", viewModel.NeedsHumanCountText);
        Assert.Equal(string.Empty, viewModel.CountQualifier);
        Assert.Equal("3 working · 1 needs you", viewModel.SummaryText);
        Assert.False(viewModel.MonitoringUnavailable);
        Assert.False(viewModel.IsPartial);
    }

    [Fact]
    public void PartialCountsAreVisiblyQualified()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(PartialSnapshot(3, 1));

        Assert.Equal("3+", viewModel.WorkingCountText);
        Assert.Equal("1+", viewModel.NeedsHumanCountText);
        Assert.Equal("partial", viewModel.CountQualifier);
        Assert.Contains("Counts may miss work", viewModel.MonitoringDisclosure, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.IsPartial);
    }

    [Fact]
    public void UnknownCountsNeverBecomeZero()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(SyntheticHudScenarios.Unavailable());

        Assert.Equal("?", viewModel.WorkingCountText);
        Assert.Equal("?", viewModel.NeedsHumanCountText);
        Assert.Equal("unknown", viewModel.CountQualifier);
        Assert.True(viewModel.MonitoringUnavailable);
        Assert.Contains("unknown", viewModel.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0 working", viewModel.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthyEmptyDiffersFromUnavailable()
    {
        var healthy = CreateViewModel();
        healthy.ApplySnapshot(SyntheticHudScenarios.HealthyEmpty());
        Assert.False(healthy.MonitoringUnavailable);
        Assert.Equal("0", healthy.WorkingCountText);
        Assert.Contains("No agent sessions tracked yet", healthy.EmptyStateText, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex", healthy.EmptyStateText, StringComparison.Ordinal);

        var unavailable = CreateViewModel();
        unavailable.ApplySnapshot(SyntheticHudScenarios.Unavailable());
        Assert.True(unavailable.MonitoringUnavailable);
        Assert.Equal("?", unavailable.WorkingCountText);
        Assert.StartsWith("Task activity unavailable.", unavailable.EmptyStateText, StringComparison.Ordinal);
        Assert.Contains("monitoring details", unavailable.EmptyStateText, StringComparison.Ordinal);
    }

    [Fact]
    public void DegradedSnapshotRetainsRowsAndMarksThem()
    {
        var viewModel = CreateViewModel();
        viewModel.TaskView = "recent";
        viewModel.ApplySnapshot(SyntheticHudScenarios.Mixed());

        Assert.True(viewModel.Tasks.Count > 0);
        var stale = viewModel.Tasks.Single(row => row.Label == "Sparse long-running job");
        Assert.True(stale.IsStale);
        Assert.Contains("stale", stale.MarksText, StringComparison.Ordinal);

        var provisional = viewModel.Tasks.Single(row => row.Label == "Unresolved identity");
        Assert.True(provisional.IsProvisional);
        Assert.Contains("provisional", provisional.MarksText, StringComparison.Ordinal);
    }

    [Fact]
    public void NonBlockingNeedsHumanStaysWorkingAndIsDisclosed()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(SyntheticHudScenarios.Mixed());

        var row = viewModel.Tasks.Single(candidate => candidate.Key.ConversationId.EndsWith("0005", StringComparison.Ordinal));
        Assert.Equal(AgentTaskState.Working, row.TaskState);
        Assert.True(row.NeedsHuman);
        Assert.Equal("WORKING", row.StateText);
        Assert.Contains("needs you soon", row.MarksText, StringComparison.Ordinal);
        Assert.NotEqual("NEEDS_INPUT", row.StateText);
    }

    [Fact]
    public void RowsKeepStableKeysAndDeterministicOrder()
    {
        var viewModel = CreateViewModel();
        var snapshot = SyntheticHudScenarios.Mixed();
        viewModel.ApplySnapshot(snapshot);

        var firstOrder = viewModel.Tasks.Select(row => row.Key).ToArray();
        var identities = viewModel.Tasks.ToDictionary(row => row.Key, row => row);
        viewModel.UpdateAges(snapshot.GeneratedAt.AddMinutes(3));
        Assert.Equal(firstOrder, viewModel.Tasks.Select(row => row.Key).ToArray());

        viewModel.ApplySnapshot(snapshot with { GeneratedAt = snapshot.GeneratedAt.AddMinutes(3) });
        Assert.Equal(firstOrder, viewModel.Tasks.Select(row => row.Key).ToArray());

        // Attention-bearing states come first, then stable numbers.
        Assert.Equal(1, viewModel.Tasks[0].Number);
        Assert.Equal(2, viewModel.Tasks[1].Number);
        Assert.Equal("Aurora release checklist", identities[firstOrder[0]].Label);
    }

    [Fact]
    public void LabelFallbackAndActionProjectionUseTheMonitorValues()
    {
        var task = new AgentTask(
            new TaskKey("codex", "01a0d000-0000-0000-0000-00000000000f"),
            7,
            "01a0d000-0000-0000-0000-00000000000f",
            "codex",
            ClientSurface.Desktop,
            string.Empty,
            "turn-1",
            AgentTaskState.Working,
            new AgentAction(AgentActionKind.RunningTests, EvidenceKind.Structured, "test-runner"),
            EvidenceKind.Structured,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            null,
            1);

        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CompleteSnapshot(1, 0, task));

        var row = Assert.Single(viewModel.Tasks);
        Assert.Equal("Task #7", row.Label);
        Assert.Equal("Running tests", row.ActionText);
        Assert.Equal("WORKING", row.StateText);
        Assert.Equal("#7", row.NumberText);
        Assert.Contains("Running tests", row.RowSubtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void AgeTextIsBoundedAndNeverNegative()
    {
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("now", HudText.Relative(TimeSpan.FromSeconds(-5)));
        Assert.Equal("0s", HudText.Relative(TimeSpan.FromSeconds(0.4)));
        Assert.Equal("59s", HudText.Relative(TimeSpan.FromSeconds(59)));
        Assert.Equal("5m", HudText.Relative(TimeSpan.FromMinutes(5)));
        Assert.Equal("3h", HudText.Relative(TimeSpan.FromHours(3)));
        Assert.Equal("2d", HudText.Relative(TimeSpan.FromDays(2)));
        Assert.Equal("unknown", HudText.Freshness(null, now));
        Assert.Equal("now", HudText.Freshness(now.AddSeconds(5), now));
    }

    [Fact]
    public void CapabilityDisclosureStatesUnsupportedAttention()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(SyntheticHudScenarios.Mixed());

        Assert.Contains("unsupported", viewModel.CapabilityLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approval", viewModel.CapabilityLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MonitorFailureYieldsUnknownInsteadOfFrozenSummary()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CompleteSnapshot(3, 1));
        Assert.Equal("3", viewModel.WorkingCountText);

        viewModel.NotifyMonitorFailure("monitor-faulted");

        Assert.Equal("?", viewModel.WorkingCountText);
        Assert.Equal("?", viewModel.NeedsHumanCountText);
        Assert.Equal("unknown", viewModel.CountQualifier);
        Assert.True(viewModel.MonitoringUnavailable);
        Assert.Contains("unknown", viewModel.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusMessagesSurfaceTransientlyAndClear()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CompleteSnapshot(1, 0));
        viewModel.NotifyStatus("Opening task in Codex…");
        Assert.Equal("Opening task in Codex…", viewModel.FooterHint);
        Assert.Contains("Working", viewModel.CollapsedStatusLine);
        viewModel.ClearStatus();
        Assert.NotEqual("Opening task in Codex…", viewModel.CollapsedStatusLine);
    }

    [Fact]
    public void SettingsChangesRaiseOneEventAndStayBounded()
    {
        var viewModel = CreateViewModel();
        var changes = 0;
        viewModel.SettingsChanged += (_, _) => changes++;

        viewModel.Opacity = 5.0;
        Assert.Equal(HudSettingsMaximumOpacity, viewModel.Opacity);
        viewModel.Opacity = double.NaN;
        Assert.Equal(HudSettings.NormalizeOpacity(double.NaN), viewModel.Opacity);
        viewModel.Theme = HudTheme.Light;
        viewModel.AlwaysOnTop = false;
        Assert.True(changes >= 3);
        Assert.Equal("light", viewModel.ThemeName);
    }

    [Fact]
    public void ExpansionGlyphAndFooterFollowState()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(SyntheticHudScenarios.Mixed());
        Assert.Equal("▾", viewModel.ExpandGlyph);
        viewModel.IsExpanded = true;
        Assert.Equal("▴", viewModel.ExpandGlyph);
        Assert.Contains("Click a Codex task", viewModel.FooterHint, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterAndRowHintsNeverAdvertiseAnUnsupportedRoute()
    {
        var codex = new TaskKey("codex", "01a0d000-0000-0000-0000-0000000000c1") { HarnessId = HarnessIds.Codex };
        var dsh = TaskKey.For(HarnessIds.Dsh, "dsh-default", "01a0d000-0000-0000-0000-0000000000d1");
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CompleteSnapshot(2, 0, VoiceTestData.Task(key: codex), VoiceTestData.Task(key: dsh, number: 2)));

        // A mixed view offers the Codex route for Codex rows only.
        Assert.True(viewModel.Tasks.Single(row => row.Key.HarnessId == HarnessIds.Codex).CanNavigate);
        var dshRow = viewModel.Tasks.Single(row => row.Key.HarnessId == HarnessIds.Dsh);
        Assert.False(dshRow.CanNavigate);
        Assert.Contains("no supported open route", dshRow.NavigationHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copy", dshRow.NavigationHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Codex", dshRow.NavigationHint, StringComparison.Ordinal);

        // A view with no routable task never claims a Codex link at all.
        var dshOnly = CreateViewModel();
        dshOnly.ApplySnapshot(CompleteSnapshot(1, 0, VoiceTestData.Task(key: dsh, number: 2)));
        Assert.False(dshOnly.HasNavigableRows);
        Assert.DoesNotContain("Codex", dshOnly.FooterHint, StringComparison.Ordinal);
        Assert.Contains("no supported open route", dshOnly.FooterHint, StringComparison.OrdinalIgnoreCase);
    }

    private const double HudSettingsMaximumOpacity = 1.0;

    private static MonitoringSnapshot CompleteSnapshot(int working, int needsHuman, params AgentTask[] extra)
    {
        var tasks = extra.Length > 0 ? extra.ToList() : new List<AgentTask>();
        return new MonitoringSnapshot(
            new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
            new MonitoringHealth(MonitoringHealthLevel.Healthy, Array.Empty<string>(), true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new MonitoringCounts(
                CountValue.Known(working),
                CountValue.Known(needsHuman),
                CountValue.Known(0),
                CountValue.Known(0),
                CountValue.Known(0),
                CountValue.Known(0),
                CountValue.Known(0),
                CountValue.Known(tasks.Count)),
            tasks,
            Array.Empty<AttentionEvent>(),
            MonitorCapabilities.Initial with
            {
                PermissionRequests = new(CapabilitySupport.Supported, "test", "test"),
                InputRequests = new(CapabilitySupport.Supported, "test", "test"),
            },
            LiveObservationStats.Empty);
    }

    private static MonitoringSnapshot PartialSnapshot(int working, int needsHuman) =>
        CompleteSnapshot(working, needsHuman) with
        {
            Counts = new MonitoringCounts(
                CountValue.Partial(working),
                CountValue.Partial(needsHuman),
                CountValue.Partial(0),
                CountValue.Partial(0),
                CountValue.Partial(0),
                CountValue.Partial(0),
                CountValue.Partial(0),
                CountValue.Partial(0)),
            Health = new MonitoringHealth(MonitoringHealthLevel.Degraded, new[] { "stale-tasks" }, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        };
}
