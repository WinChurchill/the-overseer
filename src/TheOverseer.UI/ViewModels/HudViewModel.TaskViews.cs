using System.Windows.Input;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

public sealed partial class HudViewModel
{
    private static readonly int[] RecentHourChoices = { 1, 2, 4, 8, 24 };
    private readonly DateTimeOffset _sessionStartedAt;
    private MonitoringSnapshot? _latestSnapshot;
    private DateTimeOffset _viewNow;
    private string _taskView;
    private int _recentHours;

    public string TaskView
    {
        get => _taskView;
        set
        {
            if (!Set(ref _taskView, HudSettings.NormalizeTaskView(value))) return;
            Raise(nameof(IsNowView));
            Raise(nameof(IsRecentView));
            Raise(nameof(IsSessionView));
            RefreshTaskView();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsNowView { get => TaskView == "now"; set { if (value) TaskView = "now"; } }
    public bool IsRecentView { get => TaskView == "recent"; set { if (value) TaskView = "recent"; } }
    public bool IsSessionView { get => TaskView == "session"; set { if (value) TaskView = "session"; } }
    public int RecentHours
    {
        get => _recentHours;
        set
        {
            if (!Set(ref _recentHours, Math.Clamp(value, 1, 24))) return;
            Raise(nameof(RecentWindowLabel));
            // The selector is bound to SelectedRecentHours, so a change made from the
            // header menu has to notify it too: otherwise the ComboBox keeps showing the
            // previous window while the list already uses the new one, and re-selecting
            // the displayed value raises no SelectionChanged to correct it.
            Raise(nameof(SelectedRecentHours));
            RefreshTaskView();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string RecentWindowLabel => RecentHours + "h ↻";
    public string TaskViewHint => TaskView switch
    {
        "recent" => "Tasks with observed activity in the last " + RecentHours + " hours. Counts above cover all monitored work.",
        "session" => "Tasks touched since OVERSEER started at " + _sessionStartedAt.ToLocalTime().ToString("t") + ". Counts above cover all monitored work.",
        _ => "Current work and unresolved attention, plus completions/errors from the last 10 minutes. Unconfirmed requests stay visible with a qualifier. Counts above cover all monitored work.",
    };

    public ICommand ShowCurrentTasksCommand { get; }
    public string StatusKind { get; private set; } = "unavailable";
    public string StatusHeadline { get; private set; } = "Starting monitor";
    public string StatusSymbol { get; private set; } = "?";
    public bool AttentionLimited { get; private set; } = true;
    public string AttentionDisclosure { get; private set; } = "Checking attention detection…";
    public string MonitoringDisclosure { get; private set; } = "Reading local task activity…";
    public string MonitoringBadgeText { get; private set; } = "Connecting…";
    public string StatusDetails => MonitoringDisclosure + "\n" + AttentionDisclosure + "\n" + HealthLine;

    private void RefreshTaskView()
    {
        if (_latestSnapshot is not null)
        {
            UpdateAges(_viewNow);
        }
        Raise(nameof(TaskViewHint));
        Raise(nameof(MetaStripLine));
        Raise(nameof(FooterHint));
    }

    /// <summary>
    /// Selection is view + filter, with one deliberate exception: the attention filter
    /// spans the whole snapshot instead of the selected time window. A pending request
    /// that is old, stale or outside the Recent window is still a request the user has
    /// to answer, and the summary must never lead to a filter that shows nothing.
    /// Stale and provisional rows keep their own marks, so uncertainty stays visible.
    /// Counts are never recomputed here: the summary keeps describing all monitored work.
    /// </summary>
    private IReadOnlyList<AgentTask> SelectTasks(IReadOnlyList<AgentTask> tasks, DateTimeOffset now) =>
        tasks.Where(task => MatchesFilter(task) && (MatchesView(task, now) || _taskFilter == "attention")).ToArray();

    private bool MatchesView(AgentTask task, DateTimeOffset now) => TaskView switch
    {
        "recent" => task.LastActivityAt >= now.AddHours(-RecentHours),
        "session" => task.LastActivityAt >= _sessionStartedAt,
        _ => IsCurrent(task, now),
    };

    private bool MatchesFilter(AgentTask task) => _taskFilter switch
    {
        "attention" => IsAttention(task),
        "working" => task.State == AgentTaskState.Working && task.HasCurrentState,
        _ => true,
    };

    private static bool IsCurrent(AgentTask task, DateTimeOffset now) =>
        task.HasAttention || task.HasUnconfirmedAttention || task.HasCurrentState &&
        (task.State is AgentTaskState.Working or AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput ||
         (task.IsTerminal && (task.CompletedAt ?? task.LastActivityAt) >= now.AddMinutes(-10)));

    private void UpdateVisualStatus(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        static bool Available(CapabilityStatus capability) => capability.Support is CapabilitySupport.Supported or CapabilitySupport.LiveObserved;
        AttentionLimited = !Available(snapshot.Capabilities.PermissionRequests) || !Available(snapshot.Capabilities.InputRequests);
        AttentionDisclosure = AttentionLimited
            ? "Approval/input detection limited — check Codex for requests."
            : "Approval/input detection available.";
        var reasons = snapshot.Health.Reasons;
        var limited = !snapshot.Counts.Working.IsComplete || !snapshot.Counts.Working.IsKnown || !snapshot.Health.CoverageComplete;
        MonitoringBadgeText = snapshot.Health.Level == MonitoringHealthLevel.Unavailable ? "Activity unavailable"
            : limited && AttentionLimited ? "Limited coverage · input detection limited"
            : limited ? "Limited coverage" : AttentionLimited ? "Input detection limited" : "Live monitoring";
        MonitoringDisclosure = snapshot.Health.Level == MonitoringHealthLevel.Unavailable
            ? "Cannot read task activity. Counts are unknown."
            : HudText.HasReason(reasons, HealthReasons.HydrationBudget) || HudText.HasReason(reasons, HealthReasons.DiscoveryBacklog)
                ? "Some task history is still unread. Counts may miss work."
                : HudText.HasReason(reasons, HealthReasons.StaleTasks)
                    ? "Some tasks have no fresh updates. Counts may miss work."
                    : !snapshot.Counts.Working.IsComplete || !snapshot.Counts.Working.IsKnown || !snapshot.Health.CoverageComplete
                        ? "Only some task activity is available. Counts may miss work."
                        : "Task activity is up to date.";
        if (snapshot.Health.Level == MonitoringHealthLevel.Unavailable)
        {
            StatusKind = "unavailable";
            StatusHeadline = "Monitor offline";
            StatusSymbol = string.Empty;
        }
        else if (snapshot.Counts.NeedsHuman.IsKnown && snapshot.Counts.NeedsHuman.Value > 0)
        {
            StatusKind = "attention";
            StatusHeadline = "Needs you";
            StatusSymbol = "!";
        }
        else if (snapshot.Tasks.Any(static task => task.HasUnconfirmedAttention))
        {
            StatusKind = "uncertain";
            StatusHeadline = "Attention unconfirmed";
            StatusSymbol = "?";
        }
        else if (snapshot.Tasks.Any(task => task.State == AgentTaskState.Error && IsCurrent(task, now)))
        {
            StatusKind = "error";
            StatusHeadline = "Task failed";
            StatusSymbol = "!";
        }
        else if (snapshot.Counts.Working.IsKnown && snapshot.Counts.Working.Value > 0)
        {
            StatusKind = "working";
            StatusHeadline = "Working";
            StatusSymbol = "▶";
        }
        else if (!snapshot.Counts.Working.IsKnown || !snapshot.Counts.Working.IsComplete)
        {
            StatusKind = "uncertain";
            StatusHeadline = "Limited visibility";
            StatusSymbol = string.Empty;
        }
        else
        {
            StatusKind = "idle";
            StatusHeadline = "Idle";
            StatusSymbol = "–";
        }
        HealthLabel = HudText.HealthLabel(snapshot.Health.Level, snapshot.Health.CoverageComplete);
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        Raise(nameof(LightToggleText));
        Raise(nameof(LightToggleTooltip));
        Raise(nameof(StatusKind));
        Raise(nameof(StatusHeadline));
        Raise(nameof(StatusSymbol));
        Raise(nameof(AttentionLimited));
        Raise(nameof(AttentionDisclosure));
        Raise(nameof(MonitoringDisclosure));
        Raise(nameof(MonitoringBadgeText));
        Raise(nameof(StatusDetails));
        Raise(nameof(HealthLabel));
        Raise(nameof(HealthDetails));
        Raise(nameof(HealthNextAction));
    }
}
