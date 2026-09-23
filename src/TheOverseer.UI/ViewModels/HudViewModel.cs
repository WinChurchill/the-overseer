using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TheOverseer.Core.Configuration;
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
        _sessionStartedAt = sessionStartedAt ?? DateTimeOffset.UtcNow;
        CycleRecentWindowCommand = new RelayCommand(() => RecentHours = RecentHourChoices.FirstOrDefault(hour => hour > RecentHours, 1));
        ShowCurrentTasksCommand = new RelayCommand(() => { TaskView = "now"; IsExpanded = true; });
        _activateTask = activateTask;
        _copyTaskId = copyTaskId;
        ActivateTaskCommand = new RelayCommand<TaskRowViewModel>(task => _activateTask?.Invoke(task));
        CopyTaskIdCommand = new RelayCommand<TaskRowViewModel>(task => _copyTaskId?.Invoke(task));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a display setting changes so App can persist it.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised on explicit user expansion so App can re-save placement.</summary>
    public event EventHandler? ExpandedChanged;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public ICommand ActivateTaskCommand { get; }

    public ICommand CopyTaskIdCommand { get; }

    public ICommand ToggleExpandedCommand { get; }

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

    /// <summary>One concise line under the counts: health, tracked rows, freshness.</summary>
    public string CollapsedStatusLine
    {
        get
        {
            if (MonitoringUnavailable)
            {
                return "monitor unavailable · counts unknown";
            }

            return StatusSymbol + " " + StatusHeadline + (IsPartial ? " · partial" : "");
        }
    }

    /// <summary>Compact metadata strip where Plan 3 will draw the activity chart.</summary>
    public string MetaStripLine => TaskCountLine + " · " + UpdatedLine +
                                   (IsPartial ? " · coverage partial" : " · coverage complete");

    public string FooterHint => StatusMessage.Length > 0
        ? StatusMessage
        : HasTasks
            ? "Click a task to open it in Codex · right-click to copy its id"
            : EmptyStateText;

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
        private set => Set(ref _healthLine, value);
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
            }
        }
    }

    /// <summary>Resolved monitor device name, for diagnostics only.</summary>
    public string PlacementLine
    {
        get => _placementLine;
        private set => Set(ref _placementLine, value);
    }

    public void NotifyStatus(string message) => StatusMessage = message ?? string.Empty;

    public void ClearStatus() => StatusMessage = string.Empty;

    /// <summary>Records the resolved placement; a recovered placement is disclosed once.</summary>
    public void NotifyPlacement(string deviceName, bool clamped)
    {
        PlacementLine = deviceName ?? string.Empty;
        if (clamped)
        {
            StatusMessage = "Placement recovered onto a reachable monitor.";
        }
    }

    /// <summary>
    /// Monitor stopped unexpectedly: the HUD must show unknown state instead of
    /// freezing the last healthy summary.
    /// </summary>
    public void NotifyMonitorFailure(string code)
    {
        _latestSnapshot = null;
        StatusHeadline = "Monitoring unavailable";
        StatusKind = "unavailable";
        StatusSymbol = "?";
        AttentionDisclosure = "Task state is unknown. Check monitoring.";
        RaiseStatus();
        MonitoringTitle = "MONITORING UNAVAILABLE";
        MonitoringDetail = "monitor stopped · " + code;
        HealthLine = MonitoringTitle + " · " + MonitoringDetail;
        Tasks.Clear();
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
        EmptyStateText = "Monitor unavailable — task state is unknown, not zero.";
        StatusMessage = "Monitor stopped (" + code + ") — task state is unknown.";
        Raise(nameof(CollapsedStatusLine));
        Raise(nameof(MetaStripLine));
        Raise(nameof(FooterHint));
    }

    /// <summary>Applies one snapshot. Must be called on the UI thread.</summary>
    public void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        HasAppliedSnapshot = true;
        var now = snapshot.GeneratedAt;
        _latestSnapshot = snapshot;
        _viewNow = now;

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
        if (_latestSnapshot is { } snapshot)
        {
            SyncRows(snapshot, now);
            UpdateVisualStatus(snapshot, now);
        }
        for (var index = 0; index < Tasks.Count; index++)
        {
            var task = Tasks[index];
            if (index < _lastActivity.Count)
            {
                task.UpdateAge(now, _lastActivity[index]);
            }
        }

        UpdatedLine = "updated " + (HasAppliedSnapshot ? HudText.Freshness(_generatedAt, now) : "never");
        Raise(nameof(CollapsedStatusLine));
        Raise(nameof(MetaStripLine));
    }

    private readonly List<DateTimeOffset> _lastActivity = new();
    private DateTimeOffset _generatedAt;

    private void SyncRows(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        var ordered = Order(SelectTasks(snapshot.Tasks, now));
        if (IsRecentView) ordered = ordered.OrderByDescending(task => task.LastActivityAt).ThenBy(task => task.Number).ToArray();
        _generatedAt = snapshot.GeneratedAt;
        TaskCountLine = ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " shown · " +
            snapshot.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tracked";
        if (snapshot.Tasks.Count > 0 && snapshot.Health.Level != MonitoringHealthLevel.Unavailable)
            EmptyStateText = TaskView switch
            {
                "recent" => "No tasks touched in the last " + RecentHours + " hours.",
                "session" => "No tasks touched since OVERSEER started. Try Recent.",
                _ => "No current tasks. Try Recent for earlier work.",
            };

        if (!SequenceEqual(ordered))
        {
            Tasks.Clear();
            _lastActivity.Clear();
            foreach (var task in ordered)
            {
                Tasks.Add(new TaskRowViewModel(task, CanNavigate(task), NavigationHint(task)));
                _lastActivity.Add(task.LastActivityAt);
            }

            HasTasks = Tasks.Count > 0;
            return;
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            Tasks[index].Update(ordered[index]);
            _lastActivity[index] = ordered[index].LastActivityAt;
        }

        HasTasks = Tasks.Count > 0;
    }

    private bool SequenceEqual(IReadOnlyList<AgentTask> ordered)
    {
        if (ordered.Count != Tasks.Count)
        {
            return false;
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            if (!Tasks[index].Key.Equals(ordered[index].Key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deterministic row order: attention-bearing states first, then by stable
    /// task number. Age never participates, so rows cannot shuffle every tick.
    /// </summary>
    public static IReadOnlyList<AgentTask> Order(IReadOnlyList<AgentTask> tasks)
    {
        var ordered = new List<AgentTask>(tasks);
        ordered.Sort(static (left, right) =>
        {
            var rank = TaskRowViewModel.Rank(left).CompareTo(TaskRowViewModel.Rank(right));
            return rank != 0 ? rank : left.Number.CompareTo(right.Number);
        });
        return ordered;
    }

    /// <summary>Navigation availability is decided from normalized identity only.</summary>
    public static bool CanNavigate(AgentTask task) =>
        task.Surface == ClientSurface.Desktop &&
        !task.IsProvisional &&
        task.State != AgentTaskState.Idle &&
        task.Key.ConversationId.Length > 0;

    public static string NavigationHint(AgentTask task) =>
        CanNavigate(task)
            ? "Open this task in Codex"
            : "Open this task in Codex manually — no supported route for this task";

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
