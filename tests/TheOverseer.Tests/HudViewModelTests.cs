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
        Assert.Equal("Working", row.StateText);
        Assert.Contains("question pending", row.MarksText, StringComparison.Ordinal);
        Assert.Contains("Question pending", row.RowSubtitle, StringComparison.Ordinal);
        Assert.DoesNotContain("· Working", row.RowSubtitle, StringComparison.Ordinal);
        Assert.NotEqual("Needs input", row.StateText);
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
        Assert.Equal("Working", row.StateText);
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

    // ---- 2026-09-27 review fixes ---------------------------------------------

    private static readonly DateTimeOffset ReviewNow = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static AgentTask ReviewTask(
        string conversation,
        AgentTaskState state = AgentTaskState.Working,
        bool needsHuman = false,
        bool stale = false,
        DateTimeOffset? lastActivity = null,
        string turnId = "turn-1")
    {
        var at = lastActivity ?? ReviewNow;
        return VoiceTestData.Task(key: TaskKey.For(HarnessIds.Codex, "codex", conversation), state: state, needsHuman: needsHuman, stale: stale, turnId: turnId) with
        {
            ConversationId = conversation,
            LastActivityAt = at,
            FirstSeenAt = at,
            StateSince = at,
            Project = new ProjectDescriptor(ProjectId.FromKey("review"), "Review", @"C:\synthetic", ProjectResolution.ConfiguredRoot),
        };
    }

    private static MonitoringSnapshot ReviewSnapshot(
        DateTimeOffset at,
        params AgentTask[] tasks) => VoiceTestData.Snapshot(at, tasks) with
        {
            Health = new MonitoringHealth(MonitoringHealthLevel.Healthy, Array.Empty<string>(), true, at, at),
        };

    /// <summary>
    /// Restoring disclosure state must not re-enter the row rebuild. Rebuilding a row
    /// set for a membership/order/state change used to fire the toggle event while the
    /// collections were half-populated, which rebuilt recursively.
    /// </summary>
    [Fact]
    public void TaskHistoryDisclosureSurvivesRebuildsWithoutRecursion()
    {
        var viewModel = new HudViewModel(new HudSettings());
        var first = ReviewTask("conv-1");
        var second = ReviewTask("conv-2");
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow, first));
        viewModel.IsExpanded = true;
        viewModel.Tasks[0].IsHistoryOpen = true;

        var resets = 0;
        viewModel.Tasks.CollectionChanged += (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && ++resets > 4)
            {
                throw new InvalidOperationException("rows were rebuilt recursively");
            }
        };

        // Membership change: a second task arrives.
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(1), first, second));
        Assert.Equal(2, viewModel.Tasks.Count);
        Assert.True(viewModel.Tasks.Single(row => row.Key.ConversationId == "conv-1").IsHistoryOpen);

        // Reorder plus state change, then removal of the disclosed row.
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(2), second, first with { State = AgentTaskState.NeedsInput, NeedsHuman = true }));
        Assert.True(viewModel.Tasks.Single(row => row.Key.ConversationId == "conv-1").IsHistoryOpen);
        Assert.False(viewModel.Tasks.Single(row => row.Key.ConversationId == "conv-2").IsHistoryOpen);

        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(3), second));
        Assert.Single(viewModel.Tasks);
        Assert.True(resets <= 4, "each changed snapshot rebuilds at most once");
    }

    [Fact]
    public void ProjectHistoryDisclosureSurvivesRebuildsWithoutRecursion()
    {
        var viewModel = new HudViewModel(new HudSettings());
        var first = ReviewTask("conv-1");
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow, first));
        viewModel.IsExpanded = true;
        viewModel.Projects[0].IsHistoryOpen = true;
        Assert.True(viewModel.AnyProjectHistoryOpen);

        var resets = 0;
        viewModel.Projects.CollectionChanged += (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && ++resets > 4)
            {
                throw new InvalidOperationException("projects were rebuilt recursively");
            }
        };

        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(1), first with { State = AgentTaskState.NeedsInput, NeedsHuman = true }));
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(2), first with { TurnId = "turn-2" }));

        Assert.True(viewModel.Projects[0].IsHistoryOpen);
        Assert.True(viewModel.AnyProjectHistoryOpen);
        Assert.True(resets <= 4, "each changed snapshot rebuilds at most once");
    }

    /// <summary>
    /// A pending request is a request regardless of age, so the attention filter spans
    /// the whole snapshot. It must not rewrite the saved Recent window either.
    /// </summary>
    [Fact]
    public void NeedsYouExposesOldPendingAttentionWithoutRewritingTheSavedWindow()
    {
        var viewModel = new HudViewModel(new HudSettings { TaskView = "recent", RecentHours = 2 });
        var oldBlocking = ReviewTask("old", AgentTaskState.NeedsInput, needsHuman: true, stale: true, lastActivity: ReviewNow.AddHours(-9));
        var oldAsync = ReviewTask("async", AgentTaskState.Working, needsHuman: true, lastActivity: ReviewNow.AddHours(-5));
        var recent = ReviewTask("recent", AgentTaskState.Working, lastActivity: ReviewNow.AddMinutes(-1));
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow, oldBlocking, oldAsync, recent) with { Counts = new MonitoringCounts(
            CountValue.Known(2), CountValue.Partial(2), CountValue.Known(0), CountValue.Known(1), CountValue.Known(0), CountValue.Known(0), CountValue.Known(0), CountValue.Known(3)) });

        // The Recent window hides both old requests.
        Assert.Equal(new[] { "recent" }, viewModel.Tasks.Select(static row => row.Key.ConversationId).ToArray());

        viewModel.ShowAttentionCommand.Execute(null);

        Assert.True(viewModel.IsFilterAttention);
        Assert.Equal("now", viewModel.TaskView);
        Assert.Equal(2, viewModel.RecentHours);
        Assert.Equal(
            new[] { "async", "old" },
            viewModel.Tasks.Select(static row => row.Key.ConversationId).OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        // Uncertainty stays visible on the rows that carry it.
        var stale = viewModel.Tasks.Single(row => row.Key.ConversationId == "old");
        Assert.True(stale.IsStale);
        Assert.Contains("stale", stale.RowSubtitle, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.Tasks.Single(row => row.Key.ConversationId == "async").NeedsHuman);
        // Counts keep describing all monitored work, not the filtered rows.
        Assert.Equal("2+", viewModel.NeedsHumanCountText);

        // All is the reset: the saved view and window come back untouched.
        viewModel.ShowAllTasksCommand.Execute(null);
        Assert.True(viewModel.IsFilterAll);
        Assert.Equal("now", viewModel.TaskView);
        Assert.Equal(2, viewModel.RecentHours);
        Assert.Equal(2, viewModel.Tasks.Count);
    }

    [Theory]
    [InlineData("attention", AgentTaskState.Working, "Needs you")]
    [InlineData("working", AgentTaskState.Done, "Working")]
    public void EmptyFilteredRecentViewExplainsTheFilterAndAllRestoresTrackedWork(
        string filter, AgentTaskState state, string label)
    {
        var viewModel = new HudViewModel(new HudSettings { TaskView = "recent", RecentHours = 2 });
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow,
            ReviewTask("recent", state, lastActivity: ReviewNow.AddMinutes(-1))));
        Assert.Single(viewModel.Tasks);

        viewModel.TaskFilter = filter;

        Assert.Empty(viewModel.Tasks);
        Assert.Equal("0 shown · 1 tracked", viewModel.TaskCountLine);
        Assert.Equal("No observed tasks match the " + label + " filter. Try All.", viewModel.EmptyStateText);

        viewModel.ShowAllTasksCommand.Execute(null);
        Assert.Single(viewModel.Tasks);
        Assert.Equal("recent", viewModel.TaskView);
    }

    /// <summary>
    /// Each source carries its own evidence time, so a fresh peer can never renew a
    /// silent source's displayed freshness, and two instances of one harness stay apart.
    /// </summary>
    [Fact]
    public void HealthDetailsUseEachSourcesOwnEvidenceAndInstanceIdentity()
    {
        var viewModel = new HudViewModel(new HudSettings());
        var observed = MonitorCapabilities.Initial with
        {
            PermissionRequests = new CapabilityStatus(CapabilitySupport.LiveObserved, "codex", "codex"),
            InputRequests = new CapabilityStatus(CapabilitySupport.LiveObserved, "codex", "codex"),
        };
        var unsupported = MonitorCapabilities.Initial with
        {
            PermissionRequests = new CapabilityStatus(CapabilitySupport.Unsupported, "bridge", "bridge"),
            InputRequests = new CapabilityStatus(CapabilitySupport.Unavailable, "bridge", "bridge"),
        };
        var snapshot = CompleteSnapshot(1, 0, ReviewTask("conv-1")) with
        {
            GeneratedAt = ReviewNow,
            Sources = new[]
            {
                new SourceHealth(HarnessIds.Codex, "codex", "Codex", MonitoringHealthLevel.Healthy, Array.Empty<string>(), true, ReviewNow, observed),
                new SourceHealth(HarnessIds.Dsh, "dsh-a", "DeepSeek Harness (dsh-a)", MonitoringHealthLevel.Unavailable, new[] { "source-silent" }, false, ReviewNow.AddMinutes(-2), unsupported),
                new SourceHealth(HarnessIds.Dsh, "dsh-b", "DeepSeek Harness (dsh-b)", MonitoringHealthLevel.Degraded, new[] { "bridge-stale-or-incomplete" }, false, ReviewNow.AddSeconds(-5), unsupported),
            },
        };

        viewModel.ApplySnapshot(snapshot);

        Assert.Equal(3, viewModel.HealthDetails.Count);
        var codex = viewModel.HealthDetails[0];
        Assert.Equal("Codex · codex", codex.Title);
        Assert.Contains("live", codex.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0s ago", codex.Freshness, StringComparison.Ordinal);
        Assert.Contains("observed", codex.Capability, StringComparison.Ordinal);

        var silent = viewModel.HealthDetails[1];
        Assert.Equal("DSH · dsh-a", silent.Title);
        Assert.Contains("not readable", silent.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source-silent", silent.Detail, StringComparison.Ordinal);
        // The healthy peer's freshness must not leak into this row.
        Assert.Contains("2m ago", silent.Freshness, StringComparison.Ordinal);
        Assert.DoesNotContain("0s ago", silent.Freshness, StringComparison.Ordinal);
        Assert.Contains("unsupported", silent.Capability, StringComparison.Ordinal);
        Assert.Contains("unreadable", silent.Capability, StringComparison.Ordinal);

        // Two instances of one harness are separate rows with separate freshness.
        var second = viewModel.HealthDetails[2];
        Assert.Equal("DSH · dsh-b", second.Title);
        Assert.Contains("limited", second.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5s ago", second.Freshness, StringComparison.Ordinal);

        // The next action comes from the worst reported condition, not from the label.
        Assert.Contains("stopped publishing", viewModel.HealthNextAction, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.HealthDetails.All(static row => row.Description.Length > 0));
    }

    /// <summary>
    /// A request that is collapsed, disappears and then asks again in a new turn is a
    /// new episode: it may reveal once. Within one episode a manual collapse wins, and
    /// collapse-all is never overridden.
    /// </summary>
    [Fact]
    public void AttentionEpisodeIsDerivedFromTheWholeSnapshot()
    {
        var viewModel = new HudViewModel(new HudSettings());
        var requesting = ReviewTask("conv-1", AgentTaskState.NeedsInput, needsHuman: true);
        var quiet = ReviewTask("conv-1", AgentTaskState.Working);

        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow, requesting));
        viewModel.IsExpanded = true;
        Assert.True(viewModel.Projects[0].IsExpanded);

        // The user collapses the requesting project: the same episode must not reopen it.
        viewModel.ToggleProject(viewModel.Projects[0]);
        Assert.False(viewModel.Projects[0].IsExpanded);
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(1), requesting));
        Assert.False(viewModel.Projects[0].IsExpanded);

        // The request is answered and the tasks leave the snapshot entirely.
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(2), quiet));
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(3)));

        // A new turn asks again: that is a new episode and reveals the project once.
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(4), requesting with { TurnId = "turn-2" }));
        Assert.True(viewModel.Projects[0].IsExpanded);

        // Collapse-all stays authoritative for the rest of the episode.
        viewModel.SetAllProjectsExpanded(false);
        viewModel.ApplySnapshot(ReviewSnapshot(ReviewNow.AddSeconds(5), requesting with { TurnId = "turn-2" }));
        Assert.False(viewModel.Projects[0].IsExpanded);
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
