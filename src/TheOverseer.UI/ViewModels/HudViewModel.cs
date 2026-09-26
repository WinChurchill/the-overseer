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
        CycleRecentWindowCommand = new RelayCommand(() => RecentHours = RecentHourChoices.FirstOrDefault(hour => hour > RecentHours, 1));
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
        HealthDetails = new[] { new HealthSourceDetail("Monitoring", "stopped", "Restart OVERSEER to resume monitoring.", string.Empty) };
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
        StatusMessage = "Monitor stopped (" + code + ") — task state is unknown.";
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

    private IActivityStripSource? _activity;

    /// <summary>
    /// Observed-activity source supplied by the host (the notification coordinator
    /// owns the producer-side history). Null until the host provides one, in which
    /// case rows simply carry no strip.
    /// </summary>
    public void UseActivitySource(IActivityStripSource? activity) => _activity = activity;

    private readonly List<DateTimeOffset> _lastActivity = new();
    private DateTimeOffset _generatedAt;

    private void SyncRows(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        _generatedAt = snapshot.GeneratedAt;
        // Select first (Now/Recent/This session), then group, then order the projects
        // attention -> active -> recent. Rows of one project stay contiguous so the
        // project order is the group order and cannot jitter between ticks.
        var entries = GroupRows(SelectTasks(snapshot.Tasks, now), snapshot.Tasks);
        SortRows(entries, IsRecentView);
        TaskCountLine = entries.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " shown · " +
            snapshot.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tracked";
        if (snapshot.Tasks.Count > 0 && snapshot.Health.Level != MonitoringHealthLevel.Unavailable)
            EmptyStateText = TaskView switch
            {
                "recent" => "No tasks touched in the last " + RecentHours + " hours.",
                "session" => "No tasks touched since OVERSEER started. Try Recent.",
                _ => "No current tasks. Try Recent for earlier work.",
            };

        if (!SequenceEqual(entries))
        {
            RebuildRows(entries);
            Raise(nameof(HasNavigableRows));
            Raise(nameof(FooterHint));
            return;
        }

        for (var index = 0; index < entries.Length; index++)
        {
            Tasks[index].Update(entries[index].Task);
            _lastActivity[index] = entries[index].Task.LastActivityAt;
        }

        HasTasks = Tasks.Count > 0;
    }

    private bool SequenceEqual((AgentTask Task, ProjectGroup Group)[] entries)
    {
        if (entries.Length != Tasks.Count)
        {
            return false;
        }

        for (var index = 0; index < entries.Length; index++)
        {
            if (!Tasks[index].Key.Equals(entries[index].Task.Key) ||
                !string.Equals(Tasks[index].ProjectGroupKey, entries[index].Group.Key, StringComparison.Ordinal) ||
                !string.Equals(Tasks[index].ProjectGroupMeta, entries[index].Group.Meta, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rebuilds the flat task rows and the expandable project rows together, so the
    /// two views can never disagree about order or membership. Expansion is restored
    /// from the project-keyed set, which is why a snapshot rebuild cannot lose it.
    /// </summary>
    private void RebuildRows((AgentTask Task, ProjectGroup Group)[] entries)
    {
        Tasks.Clear();
        Projects.Clear();
        _lastActivity.Clear();
        // First-appearance expansion defaults and a new attention episode are decided
        // before the rows exist, so a rebuilt row can never override a user's choice.
        ApplyFirstAppearanceDefaults(entries);
        for (var index = 0; index < entries.Length; index++)
        {
            var (task, group) = entries[index];
            var row = new TaskRowViewModel(task, CanNavigate(task), NavigationHint(task), group.Key, group.Label, group.Meta,
                group.ProjectId, !IsProjectCollapsed(group.ProjectId));
            row.HistoryToggled += OnTaskHistoryToggled;
            row.IsHistoryOpen = _openTaskHistory.Contains(task.Key);
            Tasks.Add(row);
            _lastActivity.Add(task.LastActivityAt);
        }

        var start = 0;
        while (start < entries.Length)
        {
            var key = entries[start].Group.Key;
            var end = start;
            while (end < entries.Length && string.Equals(entries[end].Group.Key, key, StringComparison.Ordinal))
            {
                end++;
            }

            var rows = new TaskRowViewModel[end - start];
            for (var index = start; index < end; index++)
            {
                rows[index - start] = Tasks[index];
            }

            var group = entries[start].Group;
            var project = new ProjectRowViewModel(group.Project, group.Key, rows, group.Working, group.NeedsHuman, group.Partial, group.Harnesses);
            project.SetExpanded(!IsProjectCollapsed(group.ProjectId));
            project.HistoryToggled += OnProjectHistoryToggled;
            project.IsHistoryOpen = _openProjectHistory.Contains(group.ProjectId);
            Projects.Add(project);
            start = end;
        }

        HasTasks = Tasks.Count > 0;
        Raise(nameof(AnyProjectHistoryOpen));
        Raise(nameof(ShowHistoryLegend));
    }

    // ---- expansion defaults and history disclosure state -------------------------

    private readonly HashSet<ProjectId> _knownProjects = new();
    private readonly HashSet<ProjectId> _attentionEpisodes = new();
    private readonly HashSet<ProjectId> _userCollapsedInEpisode = new();
    private readonly HashSet<ProjectId> _openProjectHistory = new();
    private readonly HashSet<TaskKey> _openTaskHistory = new();

    /// <summary>
    /// First appearance expands attention/active projects and collapses settled ones;
    /// afterwards a user's choice is preserved. A genuinely new attention episode
    /// (needs-you appears again for a project that did not have one) reveals that
    /// project once without focus, without changing the selected view and without
    /// scrolling. The reveal is skipped when the user collapsed everything themselves.
    /// </summary>
    private void ApplyFirstAppearanceDefaults((AgentTask Task, ProjectGroup Group)[] entries)
    {
        var handled = new HashSet<ProjectId>();
        foreach (var (_, group) in entries)
        {
            if (!handled.Add(group.ProjectId))
            {
                continue;
            }

            var active = group.NeedsHuman > 0 || group.Working > 0;
            if (_knownProjects.Add(group.ProjectId))
            {
                if (!active)
                {
                    _collapsedProjects.Add(group.ProjectId);
                }

                if (group.NeedsHuman > 0)
                {
                    _attentionEpisodes.Add(group.ProjectId);
                }

                continue;
            }

            if (group.NeedsHuman > 0)
            {
                if (_attentionEpisodes.Add(group.ProjectId) && !_userCollapsedInEpisode.Contains(group.ProjectId) && !_allCollapsed)
                {
                    _collapsedProjects.Remove(group.ProjectId);
                }
            }
            else
            {
                _attentionEpisodes.Remove(group.ProjectId);
                _userCollapsedInEpisode.Remove(group.ProjectId);
            }
        }
    }

    private bool IsProjectHistoryOpen(ProjectId projectId) => _openProjectHistory.Contains(projectId);

    private void OnProjectHistoryToggled(object? sender, EventArgs e)
    {
        if (sender is not ProjectRowViewModel project)
        {
            return;
        }

        if (project.IsHistoryOpen)
        {
            _openProjectHistory.Add(project.ProjectId);
        }
        else
        {
            _openProjectHistory.Remove(project.ProjectId);
        }

        OnProjectHistoryChanged();
    }

    private void OnTaskHistoryToggled(object? sender, EventArgs e)
    {
        if (sender is not TaskRowViewModel task)
        {
            return;
        }

        if (task.IsHistoryOpen)
        {
            _openTaskHistory.Add(task.Key);
        }
        else
        {
            _openTaskHistory.Remove(task.Key);
        }

        RefreshActivityNow();
    }

    private static (AgentTask Task, ProjectGroup Group)[] GroupRows(IReadOnlyList<AgentTask> tasks, IReadOnlyList<AgentTask> allTasks)
    {
        var groups = ProjectGroups(tasks, allTasks);
        var entries = new (AgentTask, ProjectGroup)[tasks.Count];
        for (var index = 0; index < tasks.Count; index++)
        {
            entries[index] = (tasks[index], groups[index]);
        }

        return entries;
    }

    /// <summary>
    /// Deterministic order: by project group key, then the selected within-project
    /// order (attention rank and task number, or recency in the Recent view). Age
    /// never participates in the default order, so rows cannot shuffle every tick.
    /// </summary>
    private static void SortRows((AgentTask Task, ProjectGroup Group)[] entries, bool recent)
    {
        Array.Sort(entries, (left, right) =>
        {
            var byGroup = string.CompareOrdinal(left.Group.Key, right.Group.Key);
            if (byGroup != 0)
            {
                return byGroup;
            }

            if (recent)
            {
                var byActivity = right.Task.LastActivityAt.CompareTo(left.Task.LastActivityAt);
                if (byActivity != 0)
                {
                    return byActivity;
                }
            }

            var byRank = TaskRowViewModel.Rank(left.Task).CompareTo(TaskRowViewModel.Rank(right.Task));
            if (byRank != 0)
            {
                return byRank;
            }

            var byNumber = left.Task.Number.CompareTo(right.Task.Number);
            return byNumber != 0
                ? byNumber
                : string.CompareOrdinal(left.Task.Key.ConversationId, right.Task.Key.ConversationId);
        });
    }

    /// <summary>
    /// Project grouping for the visible rows. The key is sortable so the projects
    /// themselves order attention first, then active work, then recent history, with
    /// Unassigned last. The opaque project identity is the final tie-break, so two
    /// distinct projects that share a display name stay separate and rows never
    /// shuffle between ticks. Labels and summaries never contain a root path.
    /// </summary>
    private static ProjectGroup[] ProjectGroups(IReadOnlyList<AgentTask> ordered, IReadOnlyList<AgentTask> allTasks)
    {
        // Counts and coverage describe the project's monitored members, not only the
        // rows the current filter shows, so a filtered view cannot claim full health.
        var stats = new Dictionary<ProjectId, ProjectStats>();
        foreach (var task in allTasks)
        {
            var project = task.AssignedProject;
            if (!stats.TryGetValue(project.Id, out var entry))
            {
                entry = new ProjectStats();
                stats[project.Id] = entry;
            }

            entry.Total++;
            if (task.State == AgentTaskState.Working && !task.IsStale)
            {
                entry.Working++;
            }

            if (task.NeedsHuman)
            {
                entry.NeedsHuman++;
            }

            if (task.IsStale || task.IsProvisional)
            {
                entry.Partial = true;
            }

            // Short badge labels: the header must stay readable at the minimum width
            // (the full name remains available through the header tooltip).
            entry.Harnesses.Add(ShortHarness(task.Key.HarnessId));
        }

        var result = new ProjectGroup[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            var project = ordered[index].AssignedProject;
            var entry = stats[project.Id];
            var rank = project.IsUnassigned ? 9
                : entry.NeedsHuman > 0 ? 0
                : entry.Working > 0 ? 1
                : 2;
            var key = project.IsUnassigned
                ? "9|~unassigned"
                : rank.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + project.DisplayName + "|" + project.Id;
            var harnesses = entry.Harnesses.OrderBy(static item => item, StringComparer.Ordinal).ToArray();
            result[index] = new ProjectGroup(
                key,
                project.IsUnassigned ? "Unassigned" : project.DisplayName,
                project.Id,
                project,
                entry.Working,
                entry.NeedsHuman,
                entry.Partial,
                harnesses);
        }

        return result;
    }

    private static string ShortHarness(string harnessId) => HarnessIds.Normalize(harnessId) switch
    {
        HarnessIds.Codex => "Codex",
        HarnessIds.Dsh => "dsh",
        HarnessIds.Claude => "Claude",
        var other => other,
    };

    private sealed record ProjectGroup(
        string Key,
        string Label,
        ProjectId ProjectId,
        ProjectDescriptor Project,
        int Working,
        int NeedsHuman,
        bool Partial,
        IReadOnlyList<string> Harnesses)
    {
        public string Meta =>
            Working.ToString(System.Globalization.CultureInfo.InvariantCulture) + " working · " +
            NeedsHuman.ToString(System.Globalization.CultureInfo.InvariantCulture) + " needs you · " +
            string.Join(", ", Harnesses) + (Partial ? " · partial" : string.Empty);
    }

    private sealed class ProjectStats
    {
        public int Working;

        public int NeedsHuman;

        public int Total;

        public bool Partial;

        public HashSet<string> Harnesses { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Navigation availability is decided from normalized identity only, and it is
    /// harness-qualified: the codex:// route is a Codex adapter capability, so no
    /// other harness is ever offered it.
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

    // ---- project expansion -------------------------------------------------------

    private readonly HashSet<ProjectId> _collapsedProjects = new();
    private bool _allCollapsed;

    /// <summary>True while at least one project is collapsed.</summary>
    public bool AnyProjectCollapsed => _allCollapsed || _collapsedProjects.Count > 0;

    public bool AllProjectsExpanded => Projects.Count > 0 && !AnyProjectCollapsed;

    /// <summary>Project click toggles that project's task rows; observation is untouched.</summary>
    public void ToggleProject(ProjectRowViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        SetProjectExpanded(project.ProjectId, !project.IsExpanded);
    }

    /// <summary>
    /// Expansion is remembered per ProjectId, not per row instance, so it survives
    /// every snapshot rebuild and never restarts monitoring, history or voice.
    /// </summary>
    public void SetProjectExpanded(ProjectId projectId, bool expanded)
    {
        if (expanded)
        {
            if (_allCollapsed)
            {
                _allCollapsed = false;
                foreach (var project in Projects)
                {
                    if (!project.ProjectId.Equals(projectId))
                    {
                        _collapsedProjects.Add(project.ProjectId);
                    }
                }
            }

            _collapsedProjects.Remove(projectId);
        }
        else
        {
            _collapsedProjects.Add(projectId);
            if (_attentionEpisodes.Contains(projectId))
            {
                // The user closed a project that is asking for attention: the
                // once-per-episode reveal must not undo that choice.
                _userCollapsedInEpisode.Add(projectId);
            }
        }

        ApplyProjectExpansion();
    }

    public void SetAllProjectsExpanded(bool expanded)
    {
        _allCollapsed = !expanded;
        _collapsedProjects.Clear();
        if (!expanded)
        {
            foreach (var project in Projects)
            {
                _collapsedProjects.Add(project.ProjectId);
                if (_attentionEpisodes.Contains(project.ProjectId))
                {
                    _userCollapsedInEpisode.Add(project.ProjectId);
                }
            }
        }

        ApplyProjectExpansion();
    }

    private bool IsProjectCollapsed(ProjectId projectId) => _allCollapsed || _collapsedProjects.Contains(projectId);

    private void ApplyProjectExpansion()
    {
        foreach (var project in Projects)
        {
            project.SetExpanded(!IsProjectCollapsed(project.ProjectId));
        }

        foreach (var row in Tasks)
        {
            row.SetProjectExpanded(!IsProjectCollapsed(row.ProjectGroupProjectId));
        }

        Raise(nameof(AnyProjectCollapsed));
        Raise(nameof(AllProjectsExpanded));
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
