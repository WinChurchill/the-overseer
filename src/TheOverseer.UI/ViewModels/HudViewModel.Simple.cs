using System.Windows.Input;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Progressive-disclosure projections for the simplified HUD: the attention-first
/// filter, the single pinned health control with per-source detail, and the
/// per-project/per-task history disclosure. Pure projection: no I/O, no state
/// derivation and no change to what the monitor records or what voice announces.
/// </summary>
public sealed partial class HudViewModel
{
    private string _taskFilter = "all";
    private string _healthLabel = "Connecting";
    private bool _healthOpen;
    private IReadOnlyList<HealthSourceDetail> _healthDetails = Array.Empty<HealthSourceDetail>();
    private string _healthNextAction = string.Empty;

    // ---- view filter ----------------------------------------------------------

    /// <summary>
    /// "all", "attention" or "working". The filter only selects which rows are
    /// shown: the summary counts still describe all monitored work.
    /// </summary>
    public string TaskFilter
    {
        get => _taskFilter;
        set
        {
            var normalized = NormalizeFilter(value);
            if (!Set(ref _taskFilter, normalized))
            {
                return;
            }

            Raise(nameof(IsFilterAll));
            Raise(nameof(IsFilterAttention));
            Raise(nameof(IsFilterWorking));
            Raise(nameof(HasActiveFilter));
            Raise(nameof(FilterLine));
            RefreshTaskView();
        }
    }

    public bool IsFilterAll { get => _taskFilter == "all"; set { if (value) TaskFilter = "all"; } }

    public bool IsFilterAttention { get => _taskFilter == "attention"; set { if (value) TaskFilter = "attention"; } }

    public bool IsFilterWorking { get => _taskFilter == "working"; set { if (value) TaskFilter = "working"; } }

    public bool HasActiveFilter => _taskFilter != "all";

    /// <summary>Screen-reader/automation wording for the active filter.</summary>
    public string FilterLine => _taskFilter switch
    {
        "attention" => "Filter: needs you only",
        "working" => "Filter: working only",
        _ => "Filter: all monitored work",
    };

    public ICommand SetTaskFilterCommand { get; private set; } = null!;

    /// <summary>
    /// Attention first: it opens the attention filter and, when the current view
    /// would hide a known attention row, widens to the Recent window so the summary
    /// can never point at a filter that shows nothing.
    /// </summary>
    public ICommand ShowAttentionCommand { get; private set; } = null!;

    public ICommand ShowWorkingCommand { get; private set; } = null!;

    public ICommand ShowAllTasksCommand { get; private set; } = null!;

    private void InitializeSimpleCommands()
    {
        SetTaskFilterCommand = new RelayCommand<string>(value => TaskFilter = value);
        ShowAttentionCommand = new RelayCommand(() =>
        {
            TaskFilter = "attention";
            if (HasHiddenAttention())
            {
                TaskView = "recent";
                RecentHours = Math.Max(RecentHours, 8);
            }
            else
            {
                TaskView = "now";
            }

            IsExpanded = true;
        });
        ShowWorkingCommand = new RelayCommand(() =>
        {
            TaskFilter = "working";
            TaskView = "now";
            IsExpanded = true;
        });
        ShowAllTasksCommand = new RelayCommand(() => TaskFilter = "all");
        ToggleHealthCommand = new RelayCommand(() => IsHealthOpen = !IsHealthOpen);
        CloseHealthCommand = new RelayCommand(() => IsHealthOpen = false);
    }

    private static string NormalizeFilter(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "attention" or "needs-you" or "needsyou" => "attention",
        "working" => "working",
        _ => "all",
    };

    /// <summary>True when an attention row exists that the Now view would exclude.</summary>
    private bool HasHiddenAttention()
    {
        if (_latestSnapshot is not { } snapshot)
        {
            return false;
        }

        foreach (var task in snapshot.Tasks)
        {
            if (IsAttention(task) && !IsCurrent(task, _viewNow))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Attention includes async needs-you on a working task, not just blocking states.</summary>
    internal static bool IsAttention(AgentTask task) =>
        task.NeedsHuman || task.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput;

    // ---- one health control ---------------------------------------------------

    /// <summary>Concise label for the single pinned health control.</summary>
    public string HealthLabel
    {
        get => _healthLabel;
        private set => Set(ref _healthLabel, value);
    }

    /// <summary>False while the health details popup is closed.</summary>
    public bool IsHealthOpen
    {
        get => _healthOpen;
        set => Set(ref _healthOpen, value);
    }

    public ICommand ToggleHealthCommand { get; private set; } = null!;

    public ICommand CloseHealthCommand { get; private set; } = null!;

    /// <summary>One line per reporting source, plus a global line when needed.</summary>
    public IReadOnlyList<HealthSourceDetail> HealthDetails
    {
        get => _healthDetails;
        private set => Set(ref _healthDetails, value);
    }

    /// <summary>The single most useful next action for the worst known condition.</summary>
    public string HealthNextAction
    {
        get => _healthNextAction;
        private set => Set(ref _healthNextAction, value);
    }

    /// <summary>
    /// Builds the per-source detail from source-prefixed reasons and the aggregated
    /// capability notes. No paths, no session content: reason codes and status words
    /// only.
    /// </summary>
    private void UpdateHealthProjection(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        HealthLabel = HealthLabelFor(snapshot);
        var freshness = HudText.Freshness(snapshot.Health.LastSuccessfulReadAt, now);
        var rows = new List<HealthSourceDetail>(4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var worstAction = string.Empty;
        foreach (var reason in snapshot.Health.Reasons)
        {
            var code = HudText.ReasonCode(reason);
            var source = HudText.ReasonSource(reason);
            var key = source + "|" + code;
            if (!seen.Add(key))
            {
                continue;
            }

            var action = HudText.ReasonAction(code);
            if (worstAction.Length == 0 && !string.Equals(action, "No action needed.", StringComparison.Ordinal))
            {
                worstAction = action;
            }

            rows.Add(new HealthSourceDetail(
                source.Length == 0 ? "Monitoring" : HudText.ShortHarness(source),
                code,
                action,
                source.Length == 0 ? string.Empty : freshness));
        }

        // Every source that reported capabilities is listed even when healthy, so a
        // working harness is visible next to a degraded one.
        foreach (var harness in CapabilitySources(snapshot.Capabilities))
        {
            if (rows.Any(row => string.Equals(row.Source, HudText.ShortHarness(harness), StringComparison.Ordinal)))
            {
                continue;
            }

            rows.Add(new HealthSourceDetail(HudText.ShortHarness(harness), "reporting", "No action needed.", freshness));
        }

        if (rows.Count == 0)
        {
            rows.Add(new HealthSourceDetail("Monitoring", snapshot.Health.CoverageComplete ? "complete" : "partial coverage",
                "No action needed.", freshness));
        }

        HealthDetails = rows;
        HealthNextAction = worstAction.Length > 0 ? worstAction : "No action needed.";
    }

    /// <summary>Harness ids present in the aggregated capability notes ("dsh:supported").</summary>
    private static IEnumerable<string> CapabilitySources(MonitorCapabilities capabilities)
    {
        var notes = new[]
        {
            capabilities.LifecycleEvents.Note,
            capabilities.StructuredActivity.Note,
            capabilities.PermissionRequests.Note,
            capabilities.InputRequests.Note,
            capabilities.TaskFailureEvidence.Note,
            capabilities.SessionTitles.Note,
            capabilities.AttentionLiveVerification.Note,
        };
        var found = new List<string>(3);
        foreach (var note in notes)
        {
            foreach (var token in note.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = token.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var harness = token[..separator];
                if (HarnessIds.IsKnown(harness) && !found.Contains(HarnessIds.Normalize(harness)))
                {
                    found.Add(HarnessIds.Normalize(harness));
                }
            }
        }

        return found;
    }

    private static string HealthLabelFor(MonitoringSnapshot snapshot)
    {
        if (snapshot.Health.Level == MonitoringHealthLevel.Unavailable)
        {
            return HudText.HealthLabel(MonitoringHealthLevel.Unavailable, false);
        }

        return HudText.HealthLabel(snapshot.Health.Level, snapshot.Health.CoverageComplete);
    }

    // ---- history disclosure ---------------------------------------------------

    /// <summary>True while at least one project's history is disclosed.</summary>
    public bool AnyProjectHistoryOpen => Projects.Any(static project => project.IsHistoryOpen);

    /// <summary>The shared hour axis/legend is only shown with an open history area.</summary>
    public bool ShowHistoryLegend => AnyProjectHistoryOpen;

    /// <summary>Called by a project row when its history disclosure changes.</summary>
    internal void OnProjectHistoryChanged()
    {
        Raise(nameof(AnyProjectHistoryOpen));
        Raise(nameof(ShowHistoryLegend));
        // Opening history must not wait for the next tick to show real bins.
        RefreshActivityNow();
    }

    /// <summary>Re-projects observed activity immediately (bounded, one pass).</summary>
    public void RefreshActivityNow()
    {
        if (_latestSnapshot is not null)
        {
            UpdateAges(_viewNow);
        }
    }

    /// <summary>The left state accent is redundant when the status light is on.</summary>
    public bool LeftAccentVisible => !LightVisible;

    /// <summary>Explicit Recent window selection (1, 2, 4, 8 or 24 hours).</summary>
    public IReadOnlyList<int> RecentHourOptions => RecentHourChoices;

    public int SelectedRecentHours
    {
        get => RecentHours;
        set => RecentHours = value;
    }
}

/// <summary>One source's health line in the pinned health details.</summary>
public sealed record HealthSourceDetail(string Source, string State, string Action, string Freshness)
{
    /// <summary>One readable line: source, state, freshness and next action.</summary>
    public string Description => Freshness.Length > 0
        ? Source + " · " + State + " · " + Freshness + " — " + Action
        : Source + " · " + State + " — " + Action;
}
