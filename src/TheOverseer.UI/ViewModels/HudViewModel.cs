using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Projects a MonitoringSnapshot into HUD-ready text. It never performs I/O,
/// never derives task state from timing, and never computes counts itself: the
/// monitor's CountValue/Health/Capabilities are displayed as reported.
/// </summary>
public sealed partial class HudViewModel : INotifyPropertyChanged
{
    private readonly Action<TaskRowViewModel>? _activateTask;
    private readonly Action<TaskRowViewModel>? _copyTaskId;
    private HudTheme _theme;
    private bool _alwaysOnTop;
    private double _opacity;
    private bool _startWithWindows;
    private bool _isExpanded;
    private string _workingCount = "?";
    private string _needsHumanCount = "?";
    private string _countQualifier = "unknown";
    private string _monitoringTitle = "MONITORING UNAVAILABLE";
    private string _monitoringDetail = string.Empty;
    private string _emptyState = "Starting monitor…";
    private string _healthLine = string.Empty;
    private string _updatedLine = string.Empty;
    private string _capabilityLine = string.Empty;
    private string _taskCountLine = string.Empty;
    private bool _hasTasks;
    private bool _countsNeedAttention;
    private bool _monitoringUnavailable = true;
    private bool _isPartial;
    private bool _hasAppliedSnapshot;
    private string _statusMessage = string.Empty;
    private DateTimeOffset? _statusExpiresAt;
    private string _placementLine = string.Empty;

    public HudViewModel(HudSettings settings, Action<TaskRowViewModel>? activateTask = null, Action<TaskRowViewModel>? copyTaskId = null,
        DateTimeOffset? sessionStartedAt = null)
    {
        var normalized = (settings ?? HudSettings.Default).Normalized();
        _theme = normalized.ResolvedTheme;
        _alwaysOnTop = normalized.AlwaysOnTop;
        _opacity = normalized.Opacity;
        _startWithWindows = normalized.StartWithWindows;
        _taskView = normalized.TaskView;
        _recentHours = normalized.RecentHours;
        _lightPosition = normalized.LightPosition;
        _lightSize = normalized.LightSize;
        _sessionStartedAt = sessionStartedAt ?? DateTimeOffset.UtcNow;
        _viewNow = _sessionStartedAt;
        ShowCurrentTasksCommand = new RelayCommand(() => { TaskView = "now"; IsExpanded = true; });
        _activateTask = activateTask;
        _copyTaskId = copyTaskId;
        ActivateTaskCommand = new RelayCommand<TaskRowViewModel>(task => _activateTask?.Invoke(task));
        CopyTaskIdCommand = new RelayCommand<TaskRowViewModel>(task => _copyTaskId?.Invoke(task));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        ToggleProjectCommand = new RelayCommand<ProjectRowViewModel>(ToggleProject);
        ExpandAllProjectsCommand = new RelayCommand(() => SetAllProjectsExpanded(true));
        CollapseAllProjectsCommand = new RelayCommand(() => SetAllProjectsExpanded(false));
        InitializeSimpleCommands();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a display setting changes so App can persist it.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised on explicit user expansion so App can re-save placement.</summary>
    public event EventHandler? ExpandedChanged;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    /// <summary>
    /// Expandable project rows, ordered attention -> active -> recent with a stable
    /// identity tie-break. This is the HUD's primary grouping; <see cref="Tasks"/>
    /// stays the flat ordered list the rows are shared with.
    /// </summary>
    public ObservableCollection<ProjectRowViewModel> Projects { get; } = new();

    public ICommand ActivateTaskCommand { get; }

    public ICommand CopyTaskIdCommand { get; }

    public ICommand ToggleExpandedCommand { get; }

    /// <summary>Project click: expands or collapses that project's task rows.</summary>
    public ICommand ToggleProjectCommand { get; }

    public ICommand ExpandAllProjectsCommand { get; }

    public ICommand CollapseAllProjectsCommand { get; }

    /// <summary>
    /// Small legend for the shared hour axis. The strip is never colour-only: the
    /// legend states each category, and unknown is spelled out as missing
    /// observation rather than idle.
    /// </summary>
    public IReadOnlyList<ActivityLegendEntry> Legend { get; } = new[]
    {
        new ActivityLegendEntry(ActivityCategory.Thinking, "Thinking", "Structured reasoning. An inferred active turn is marked estimated."),
        new ActivityLegendEntry(ActivityCategory.Planning, "Planning", "Explicit ongoing plan action only; a plan update is a marker, not duration."),
        new ActivityLegendEntry(ActivityCategory.Working, "Working", "Tool work: reading, editing, commands, tests or search."),
        new ActivityLegendEntry(ActivityCategory.Waiting, "Waiting", "Explicit approval or input request. Not working duration."),
        new ActivityLegendEntry(ActivityCategory.Error, "Error", "Explicit task failure marker."),
        new ActivityLegendEntry(ActivityCategory.Complete, "Complete", "Explicit task completion marker."),
        new ActivityLegendEntry(ActivityCategory.Idle, "Idle", "Blank: observation confirmed settled inactivity."),
        new ActivityLegendEntry(ActivityCategory.Unknown, "Unknown = no observation, not idle", "Hatched: stale, restart, sleep or disabled source. Never a claim of inactivity."),
    };

    /// <summary>Plain-text legend note for tooltips and screen readers.</summary>
    public string LegendNote =>
        "Last hour, now at the right. Each cell is about a minute. " +
        "Unknown (hatched) is missing observation, never confirmed idle. " +
        "Hover a cell for its category, interval, duration, evidence and coverage.";

    public HudTheme Theme
    {
        get => _theme;
        set
        {
            if (Set(ref _theme, value))
            {
                Raise(nameof(ThemeName));
                Raise(nameof(ThemeLabel));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string ThemeName => _theme == HudTheme.Light ? "light" : "dark";

    public string ThemeLabel => _theme == HudTheme.Light ? "Dark mode" : "Light mode";

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (Set(ref _alwaysOnTop, value))
            {
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public double Opacity
    {
        get => _opacity;
        set
        {
            var normalized = HudSettings.NormalizeOpacity(value);
            if (Set(ref _opacity, normalized))
            {
                Raise(nameof(OpacityPercentLabel));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string OpacityPercentLabel =>
        ((int)Math.Round(_opacity * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (Set(ref _startWithWindows, value))
            {
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
            {
                Raise(nameof(ExpandGlyph));
                Raise(nameof(FooterHint));
                ExpandedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Chevron glyph for the expand control.</summary>
    public string ExpandGlyph => _isExpanded ? "\u25B4" : "\u25BE";

    /// <summary>Prominent task status; monitoring limitations are explained separately.</summary>
    public string CollapsedStatusLine
    {
        get
        {
            if (MonitoringUnavailable)
            {
                return "Monitor offline";
            }

            return StatusHeadline;
        }
    }

    /// <summary>Compact metadata strip where Plan 3 will draw the activity chart.</summary>
    public string MetaStripLine => TaskCountLine + " · " + UpdatedLine;

    /// <summary>
    /// Footer/navigation wording. It only promises a route the visible tasks can
    /// actually take, so a dsh/Claude-only view never advertises a Codex-only link.
    /// </summary>
    public string FooterHint => StatusMessage.Length > 0
        ? StatusMessage
        : HasTasks
            ? HasNavigableRows
                ? "Click a Codex task to open it · right-click to copy a task id"
                : "Right-click a task to copy its id · no supported open route for these tasks"
            : EmptyStateText;

    /// <summary>True when at least one visible row has a supported open route.</summary>
    public bool HasNavigableRows
    {
        get
        {
            foreach (var row in Tasks)
            {
                if (row.CanNavigate)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public string WorkingCountText
    {
        get => _workingCount;
        private set => Set(ref _workingCount, value);
    }

    public string NeedsHumanCountText
    {
        get => _needsHumanCount;
        private set => Set(ref _needsHumanCount, value);
    }

    /// <summary>"unknown", "partial" or empty. Never hides incomplete coverage.</summary>
    public string CountQualifier
    {
        get => _countQualifier;
        private set => Set(ref _countQualifier, value);
    }

    public string MonitoringTitle
    {
        get => _monitoringTitle;
        private set
        {
            if (Set(ref _monitoringTitle, value))
            {
                Raise(nameof(HealthLevelText));
            }
        }
    }

    /// <summary>"HEALTHY", "DEGRADED" or "UNAVAILABLE" for the health marker.</summary>
    public string HealthLevelText => _monitoringTitle.Replace("MONITORING ", string.Empty, StringComparison.Ordinal);

    public string MonitoringDetail
    {
        get => _monitoringDetail;
        private set => Set(ref _monitoringDetail, value);
    }

    public string EmptyStateText
    {
        get => _emptyState;
        private set => Set(ref _emptyState, value);
    }

    public string HealthLine
    {
        get => _healthLine;
        private set { if (Set(ref _healthLine, value)) Raise(nameof(StatusDetails)); }
    }

    public string UpdatedLine
    {
        get => _updatedLine;
        private set => Set(ref _updatedLine, value);
    }

    public string CapabilityLine
    {
        get => _capabilityLine;
        private set => Set(ref _capabilityLine, value);
    }

    public string TaskCountLine
    {
        get => _taskCountLine;
        private set => Set(ref _taskCountLine, value);
    }

    public bool HasTasks
    {
        get => _hasTasks;
        private set => Set(ref _hasTasks, value);
    }

    public bool CountsNeedAttention
    {
        get => _countsNeedAttention;
        private set => Set(ref _countsNeedAttention, value);
    }

    public bool MonitoringUnavailable
    {
        get => _monitoringUnavailable;
        private set => Set(ref _monitoringUnavailable, value);
    }

    public bool IsPartial
    {
        get => _isPartial;
        private set => Set(ref _isPartial, value);
    }

    /// <summary>
    /// Monotonic count of applied snapshots. Diagnostics and tests use it to prove
    /// the monitor-to-UI pipeline is still delivering instead of merely surviving.
    /// </summary>
    public int AppliedSnapshots { get; private set; }

    /// <summary>True once a real snapshot has been applied.</summary>
    public bool HasAppliedSnapshot
    {
        get => _hasAppliedSnapshot;
        private set => Set(ref _hasAppliedSnapshot, value);
    }

    /// <summary>Summaries for tooltips/automation, using the monitor's own wording.</summary>
    public string SummaryText { get; private set; } = "working unknown · needs you unknown";

    /// <summary>Transient user-facing status (navigation, tray, startup, placement).</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value))
            {
                Raise(nameof(CollapsedStatusLine));
                Raise(nameof(FooterHint));
                Raise(nameof(HasStatus));
            }
        }
    }

    /// <summary>
    /// True while there is something to tell the user. The feedback for navigation,
    /// tray, startup and save failures is routed here, so it has to be rendered
    /// somewhere: a failed save that nothing shows is not discoverable.
    /// </summary>
    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>Resolved monitor device name, for diagnostics only.</summary>
    public string PlacementLine
    {
        get => _placementLine;
        private set => Set(ref _placementLine, value);
    }

    public void NotifyStatus(string message, bool transient = false)
    {
        _statusExpiresAt = transient ? _viewNow.AddSeconds(5) : null;
        StatusMessage = message ?? string.Empty;
    }

    public void ClearStatus() { _statusExpiresAt = null; StatusMessage = string.Empty; }

    /// <summary>Records the resolved placement; a recovered placement is disclosed once.</summary>
    public void NotifyPlacement(string deviceName, bool clamped)
    {
        PlacementLine = deviceName ?? string.Empty;
        if (clamped)
        {
            NotifyStatus("Placement recovered onto a reachable monitor.", transient: true);
        }
    }

    /// <summary>
    /// Monitor stopped unexpectedly: the HUD must show unknown state instead of
    /// freezing the last healthy summary.
    /// </summary>
    public void NotifyMonitorFailure(string code)
    {
        _latestSnapshot = null;
        StatusHeadline = "Monitor offline";
        StatusKind = "unavailable";
        StatusSymbol = string.Empty;
        AttentionDisclosure = "Task state is unknown. Check monitoring.";
        MonitoringDisclosure = "Cannot read task activity. Counts are unknown.";
        MonitoringBadgeText = "Activity unavailable";
        RaiseStatus();
        MonitoringTitle = "MONITORING UNAVAILABLE";
        MonitoringDetail = "monitor stopped · " + code;
        HealthLabel = "Monitoring offline";
        HealthDetails = new[]
        {
            new HealthSourceDetail(
                "Monitoring", string.Empty, "stopped", "requests: unknown", string.Empty,
                "Restart OVERSEER to resume monitoring."),
        };
        HealthNextAction = "Restart OVERSEER to resume monitoring.";
        HealthLine = MonitoringTitle + " · " + MonitoringDetail;
        Tasks.Clear();
        Projects.Clear();
        _lastActivity.Clear();
        HasTasks = false;
        TaskCountLine = "task state unknown";
        MonitoringUnavailable = true;
        IsPartial = true;
        CountsNeedAttention = false;
        WorkingCountText = HudText.UnknownMark;
        NeedsHumanCountText = HudText.UnknownMark;
        CountQualifier = "unknown";
        SummaryText = "working unknown · needs you unknown";
        Raise(nameof(SummaryText));
        EmptyStateText = "Task activity unavailable.";
        NotifyStatus("Monitor stopped (" + code + ") — task state is unknown.");
        Raise(nameof(CollapsedStatusLine));
        Raise(nameof(MetaStripLine));
        Raise(nameof(FooterHint));
    }

    /// <summary>Applies one snapshot. Must be called on the UI thread.</summary>
    public void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        HasAppliedSnapshot = true;
        AppliedSnapshots++;
        Raise(nameof(AppliedSnapshots));
        var now = snapshot.GeneratedAt;
        _latestSnapshot = snapshot;
        _viewNow = now;
        if (_statusExpiresAt is { } expiry && now >= expiry) ClearStatus();

        WorkingCountText = HudText.CountValue(snapshot.Counts.Working);
        NeedsHumanCountText = HudText.CountValue(snapshot.Counts.NeedsHuman);
        UpdateVisualStatus(snapshot, now);
        if (AttentionLimited && snapshot.Counts.NeedsHuman.IsKnown)
            NeedsHumanCountText = snapshot.Counts.NeedsHuman.Value == 0 ? "?"
                : snapshot.Counts.NeedsHuman.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+";
        CountQualifier = HudText.CountQualifier(snapshot.Counts.Working, snapshot.Counts.NeedsHuman);
        SummaryText = snapshot.Counts.Working.ToDisplay("working") + " · " +
            (AttentionLimited
                ? snapshot.Counts.NeedsHuman.IsKnown && snapshot.Counts.NeedsHuman.Value > 0
                    ? snapshot.Counts.NeedsHuman.Value + " needs you (partial)" : "needs you unknown"
                : snapshot.Counts.NeedsHuman.ToDisplay("needs you"));
        Raise(nameof(SummaryText));

        MonitoringTitle = HudText.HealthTitle(snapshot.Health.Level);
        MonitoringDetail = HudText.HealthDetail(snapshot);
        UpdateHealthProjection(snapshot, now);
        CapabilityLine = HudText.CapabilityDisclosure(snapshot.Capabilities);
        HealthLine = MonitoringTitle + " · " + MonitoringDetail;
        IsPartial = !snapshot.Health.CoverageComplete;
        MonitoringUnavailable = snapshot.Health.Level == MonitoringHealthLevel.Unavailable;
        CountsNeedAttention = snapshot.Counts.NeedsHuman.IsKnown && snapshot.Counts.NeedsHuman.Value > 0;
        EmptyStateText = HudText.EmptyState(snapshot);
        TaskCountLine = snapshot.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tracked";
        UpdateAges(now);
        Raise(nameof(CollapsedStatusLine));
        Raise(nameof(MetaStripLine));
        Raise(nameof(FooterHint));
    }

    /// <summary>Refreshes ages and expires rows outside the selected time window.</summary>
    public void UpdateAges(DateTimeOffset now)
    {
        _viewNow = now;
        if (_statusExpiresAt is { } expiry && now >= expiry) ClearStatus();
        if (_latestSnapshot is { } snapshot)
        {
            SyncRows(snapshot, now);
            UpdateVisualStatus(snapshot, now);
        }
        // Producer-side observed activity, already recorded before UI coalescing.
        // Each distinct project is projected exactly once per refresh and every
        // task lane is resolved once, so the strip can never become O(rows*lanes)
        // work on the one-second tick.
        Dictionary<ProjectId, IReadOnlyList<ActivityStripBin>>? projectBins = null;
        if (_activity is not null)
        {
            projectBins = new Dictionary<ProjectId, IReadOnlyList<ActivityStripBin>>();
        }

        for (var index = 0; index < Tasks.Count; index++)
        {
            var task = Tasks[index];
            if (index < _lastActivity.Count)
            {
                task.UpdateAge(now, _lastActivity[index]);
            }

            if (_activity is null || projectBins is null)
            {
                continue;
            }

            // Closed history stops the geometry projection entirely. Observation,
            // recording and voice are untouched: only this reading path is skipped.
            if (!task.IsHistoryOpen && !IsProjectHistoryOpen(task.ProjectGroupProjectId))
            {
                continue;
            }

            var projectId = task.ProjectGroupProjectId;
            if (!projectBins.TryGetValue(projectId, out var bins))
            {
                bins = _activity.ProjectBins(projectId, now);
                projectBins[projectId] = bins;
            }

            task.UpdateActivity(
                task.IsHistoryOpen ? bins : Array.Empty<ActivityStripBin>(),
                task.IsHistoryOpen ? _activity.TaskBins(task.Key, now) : Array.Empty<ActivityStripBin>());
        }

        if (projectBins is not null)
        {
            // Every project row is backed by at least one visible task row, so this
            // only ever reads the cache filled above.
            foreach (var project in Projects)
            {
                if (!project.IsHistoryOpen)
                {
                    project.UpdateBins(Array.Empty<ActivityStripBin>());
                    continue;
                }

                if (!projectBins.TryGetValue(project.ProjectId, out var bins))
                {
                    bins = _activity!.ProjectBins(project.ProjectId, now);
                    projectBins[project.ProjectId] = bins;
                }

                project.UpdateBins(bins);
            }
        }

        UpdatedLine = "updated " + (HasAppliedSnapshot ? HudText.Freshness(_generatedAt, now) : "never");
        Raise(nameof(CollapsedStatusLine));
        Raise(nameof(MetaStripLine));
    }

    /// <summary>
    /// Navigation availability uses normalized identity and harness capability.
    /// Only Codex desktop tasks have a supported codex:// route.
    /// </summary>
    public static bool CanNavigate(AgentTask task) =>
        HarnessIds.Normalize(task.Key.HarnessId) == HarnessIds.Codex &&
        task.Surface == ClientSurface.Desktop &&
        !task.IsProvisional &&
        task.State != AgentTaskState.Idle &&
        task.Key.ConversationId.Length > 0;

    /// <summary>Honest action wording for this task; never promises an unsupported route.</summary>
    public static string NavigationHint(AgentTask task)
    {
        var harness = HarnessIds.Normalize(task.Key.HarnessId);
        if (harness == HarnessIds.Codex)
        {
            return CanNavigate(task)
                ? "Open this task in Codex"
                : "No supported Codex route for this task — click to copy its id";
        }

        return "No supported open route for " + ShortHarness(harness) + " tasks — click to copy this task's id";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
