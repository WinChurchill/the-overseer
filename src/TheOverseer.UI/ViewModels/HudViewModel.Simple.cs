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
    /// "all", "attention", "working" or "review". The filter only selects which rows are
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
            Raise(nameof(IsFilterReview));
            Raise(nameof(ShowTickAll));
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
        "review" => "Filter: finished tasks to review",
        _ => "Filter: all monitored work",
    };

    public ICommand SetTaskFilterCommand { get; private set; } = null!;

    /// <summary>
    /// Attention first: it opens the attention filter, which spans every known pending
    /// request rather than the selected time window. It never rewrites the user's saved
    /// Recent window as a side effect; only the selected view changes.
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
            TaskView = "now";
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
        "review" or "to-review" => "review",
        _ => "all",
    };

    /// <summary>Attention includes async needs-you on a working task, not just blocking states.</summary>
    internal static bool IsAttention(AgentTask task) =>
        task.HasAttention;

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

    /// <summary>Rows shown in the health popup before the list is summarized.</summary>
    public const int MaximumHealthRows = 8;

    /// <summary>
    /// Builds the health detail. When the snapshot carries typed per-source health, each
    /// row is one source with its own instance identity, level, reason codes, evidence
    /// freshness and typed capability set. The aggregate fallback exists only for a
    /// publisher that does not describe separate sources (single-source monitors and
    /// synthetic fixtures); no free-text capability note is ever parsed.
    /// </summary>
    private void UpdateHealthProjection(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        HealthLabel = HealthLabelFor(snapshot);
        var rows = snapshot.Sources.Count > 0
            ? FromSources(snapshot.Sources, now)
            : FromAggregate(snapshot, now);
        HealthDetails = rows;
        HealthNextAction = rows
            .Select(static row => row.Action)
            .FirstOrDefault(static action => !string.Equals(action, NoAction, StringComparison.Ordinal)) ?? NoAction;
    }

    private const string NoAction = "No action needed.";

    /// <summary>One row per reporting source, bounded and in the snapshot's own order.</summary>
    private static IReadOnlyList<HealthSourceDetail> FromSources(IReadOnlyList<SourceHealth> sources, DateTimeOffset now)
    {
        var rows = new List<HealthSourceDetail>(Math.Min(sources.Count, MaximumHealthRows) + 1);
        for (var index = 0; index < sources.Count && index < MaximumHealthRows; index++)
        {
            var source = sources[index];
            rows.Add(new HealthSourceDetail(
                HudText.ShortHarness(source.HarnessId),
                source.SourceInstanceId,
                StateText(source),
                HudText.CapabilityDetail(source.Capabilities),
                HudText.Freshness(source.EvidenceAt, now),
                FirstAction(source.Reasons)));
        }

        if (sources.Count > MaximumHealthRows)
        {
            var hidden = sources.Count - MaximumHealthRows;
            rows.Add(new HealthSourceDetail(
                "+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more sources",
                string.Empty,
                "not shown",
                "requests: not shown",
                string.Empty,
                "Source list is bounded; the oldest entries are omitted."));
        }

        return rows;
    }

    /// <summary>
    /// Fallback for a snapshot without typed per-source health: group the source-prefixed
    /// reason codes and report the aggregate freshness as the aggregate's own.
    /// </summary>
    private static IReadOnlyList<HealthSourceDetail> FromAggregate(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        var freshness = HudText.Freshness(snapshot.Health.LastSuccessfulReadAt, now);
        var rows = new List<HealthSourceDetail>(4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reason in snapshot.Health.Reasons)
        {
            var code = HudText.ReasonCode(reason);
            var source = HudText.ReasonSource(reason);
            if (!seen.Add(source + "|" + code))
            {
                continue;
            }

            rows.Add(new HealthSourceDetail(
                source.Length == 0 ? "Monitoring" : HudText.ShortHarness(source),
                string.Empty,
                code,
                HudText.CapabilityDetail(snapshot.Capabilities),
                freshness,
                HudText.ReasonAction(code)));
        }

        if (rows.Count == 0)
        {
            rows.Add(new HealthSourceDetail(
                "Monitoring",
                string.Empty,
                snapshot.Health.CoverageComplete ? "complete" : "partial coverage",
                HudText.CapabilityDetail(snapshot.Capabilities),
                freshness,
                NoAction));
        }

        return rows;
    }

    private static string StateText(SourceHealth source)
    {
        var state = source.Level switch
        {
            MonitoringHealthLevel.Unavailable => "not readable",
            MonitoringHealthLevel.Degraded => "limited",
            _ => "live",
        };
        if (!source.CoverageComplete)
        {
            state += " · partial";
        }

        var codes = source.Reasons
            .Select(HudText.ReasonCode)
            .Where(static code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        return codes.Length == 0 ? state : state + " · " + string.Join(", ", codes);
    }

    private static string FirstAction(IReadOnlyList<string> reasons)
    {
        foreach (var reason in reasons)
        {
            var action = HudText.ReasonAction(reason);
            if (!string.Equals(action, NoAction, StringComparison.Ordinal))
            {
                return action;
            }
        }

        return NoAction;
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

    /// <summary>True while at least one task's history is disclosed.</summary>
    public bool AnyTaskHistoryOpen => Tasks.Any(static task => task.IsHistoryOpen);

    /// <summary>
    /// The shared hour axis/legend is shown whenever any disclosed history is on screen.
    /// A task's strip is reachable with every project history closed, and it uses the same
    /// axis and legend, so leaving a bare strip without them would contradict the
    /// disclosure contract.
    /// </summary>
    public bool ShowHistoryLegend => AnyProjectHistoryOpen || AnyTaskHistoryOpen;

    /// <summary>Called by a project row when its history disclosure changes.</summary>
    internal void OnProjectHistoryChanged()
    {
        Raise(nameof(AnyProjectHistoryOpen));
        Raise(nameof(ShowHistoryLegend));
        // Opening history must not wait for the next tick to show real bins.
        RefreshActivityNow();
    }

    /// <summary>Called by a task row when its history disclosure changes.</summary>
    internal void OnTaskHistoryChanged()
    {
        Raise(nameof(AnyTaskHistoryOpen));
        Raise(nameof(ShowHistoryLegend));
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

/// <summary>
/// One source's health line in the pinned health details: harness, source instance,
/// state with its own reason codes, its own evidence freshness, the typed capability
/// distinction and one next action. Provider-instance identity is what keeps two
/// instances of the same harness apart.
/// </summary>
public sealed record HealthSourceDetail(
    string Source,
    string Instance,
    string State,
    string Capability,
    string Freshness,
    string Action)
{
    /// <summary>Source and instance, e.g. "dsh · dsh-default".</summary>
    public string Title => Instance.Length > 0 ? Source + " · " + Instance : Source;

    /// <summary>State and this source's own evidence freshness.</summary>
    public string Detail => Freshness.Length > 0 ? State + " · " + Freshness : State;

    /// <summary>One readable line for automation, tooltips and tests.</summary>
    public string Description => Title + " · " + Detail + " — " + Action + " (" + Capability + ")";
}
