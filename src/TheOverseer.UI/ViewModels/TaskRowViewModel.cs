using System.ComponentModel;
using System.Runtime.CompilerServices;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// One task row projected from a normalized AgentTask. Identity is the TaskKey,
/// so a row is never keyed by a path, title or list position.
/// </summary>
public sealed class TaskRowViewModel : INotifyPropertyChanged
{
    private string _ageText = "now";
    private string _detailText = string.Empty;
    private DateTimeOffset? _startedAt;
    private string _ageHint = string.Empty;

    private IReadOnlyList<ActivityStripBin> _projectBins = Array.Empty<ActivityStripBin>();
    private IReadOnlyList<ActivityStripBin> _taskBins = Array.Empty<ActivityStripBin>();
    private string _projectBinsSignature = string.Empty;
    private string _taskBinsSignature = string.Empty;
    private string _projectStripText = ActivityStripText.Describe(Array.Empty<ActivityStripBin>());
    private string _taskStripText = ActivityStripText.Describe(Array.Empty<ActivityStripBin>());
    private bool _isProjectExpanded = true;
    private bool _isHistoryOpen;

    public TaskRowViewModel(
        AgentTask task,
        bool canNavigate,
        string navigationHint,
        string projectGroupKey = "",
        string projectGroupLabel = "",
        string projectGroupMeta = "",
        ProjectId projectGroupProjectId = default,
        bool projectExpanded = true)
    {
        ProjectGroupKey = projectGroupKey;
        ProjectGroupLabel = projectGroupLabel;
        ProjectGroupMeta = projectGroupMeta;
        ProjectGroupProjectId = projectGroupProjectId;
        _isProjectExpanded = projectExpanded;
        Key = task.Key;
        Number = task.Number;
        Label = HudText.Label(task);
        StateText = HudText.State(task.State);
        ActionText = task.Action.Label;
        HarnessText = HudText.Harness(task);
        MarksText = HudText.Marks(task);
        TaskState = task.State;
        NeedsHuman = task.NeedsHuman;
        IsStale = task.IsStale;
        IsProvisional = task.IsProvisional;
        CanNavigate = canNavigate;
        NavigationHint = navigationHint;
        Surface = task.Surface;
        TurnId = task.TurnId;
        Update(task);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaskKey Key { get; }

    /// <summary>
    /// Sortable project grouping key for this row. Empty means the row is not part
    /// of a project group (used by tests and by views that do not group).
    /// </summary>
    public string ProjectGroupKey { get; }

    /// <summary>Project display name shown in the project header (never a path).</summary>
    public string ProjectGroupLabel { get; }

    /// <summary>Project id for this row's group; used to look up its activity strip.</summary>
    public ProjectId ProjectGroupProjectId { get; }

    /// <summary>Bounded group summary: counts and harness badges, plus coverage.</summary>
    public string ProjectGroupMeta { get; }

    /// <summary>
    /// Last-hour project activity strip. The expandable project row renders these
    /// bins; unknown coverage is rendered distinctly from confirmed idle.
    /// </summary>
    public IReadOnlyList<ActivityStripBin> ProjectBins => _projectBins;

    /// <summary>Last-hour activity strip for this task.</summary>
    public IReadOnlyList<ActivityStripBin> TaskBins => _taskBins;

    /// <summary>Plain-text project strip summary (tooltip/automation fallback).</summary>
    public string ProjectStripText
    {
        get => _projectStripText;
        private set => Set(ref _projectStripText, value);
    }

    /// <summary>Plain-text task strip summary.</summary>
    public string TaskStripText
    {
        get => _taskStripText;
        private set => Set(ref _taskStripText, value);
    }

    /// <summary>
    /// Whether this row's project is expanded. Expansion lives in the view model
    /// keyed by ProjectId; the row only mirrors it so snapshot rebuilds keep it.
    /// </summary>
    public bool IsProjectExpanded
    {
        get => _isProjectExpanded;
        private set
        {
            if (_isProjectExpanded == value)
            {
                return;
            }

            _isProjectExpanded = value;
            Raise(nameof(IsProjectExpanded));
            Raise(nameof(RowVisibility));
        }
    }

    public System.Windows.Visibility RowVisibility =>
        _isProjectExpanded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public void SetProjectExpanded(bool expanded) => IsProjectExpanded = expanded;

    /// <summary>
    /// Updates the strips only when they actually changed. The signature covers
    /// category, evidence, coverage, mixed and task count so an inferred or partial
    /// bin that becomes confirmed is redrawn.
    /// </summary>
    public void UpdateActivity(IReadOnlyList<ActivityStripBin> projectBins, IReadOnlyList<ActivityStripBin> taskBins)
    {
        var projectSignature = Signature(projectBins);
        if (!string.Equals(projectSignature, _projectBinsSignature, StringComparison.Ordinal))
        {
            _projectBins = projectBins;
            _projectBinsSignature = projectSignature;
            ProjectStripText = ActivityStripText.Describe(projectBins);
            Raise(nameof(ProjectBins));
        }

        var taskSignature = Signature(taskBins);
        if (!string.Equals(taskSignature, _taskBinsSignature, StringComparison.Ordinal))
        {
            _taskBins = taskBins;
            _taskBinsSignature = taskSignature;
            TaskStripText = ActivityStripText.Describe(taskBins);
            Raise(nameof(TaskBins));
            Raise(nameof(DetailText));
        }
    }

    private static string Signature(IReadOnlyList<ActivityStripBin> bins)
    {
        if (bins.Count == 0)
        {
            return string.Empty;
        }

        // Five characters per bin: category, evidence, coverage, concurrency, mixed.
        var buffer = new char[bins.Count * 5];
        for (var index = 0; index < bins.Count; index++)
        {
            var bin = bins[index];
            var offset = index * 5;
            buffer[offset] = (char)('a' + (int)bin.Category);
            buffer[offset + 1] = (char)('a' + (int)bin.Evidence);
            buffer[offset + 2] = (char)('a' + (int)bin.Coverage);
            buffer[offset + 3] = (char)('a' + Math.Min(bin.ConcurrentTasks, 25));
            buffer[offset + 4] = bin.IsMixed ? 'm' : 'n';
        }

        return new string(buffer);
    }

    public int Number { get; }

    public string Label { get; private set; }

    public string StateText { get; private set; }

    public string ActionText { get; private set; }

    /// <summary>Harness badge (and source instance when it differs), never a path.</summary>
    public string HarnessText { get; private set; }

    public string MarksText { get; private set; }

    public AgentTaskState TaskState { get; private set; }

    public ClientSurface Surface { get; }

    public string TurnId { get; private set; }

    public bool NeedsHuman { get; private set; }

    public bool IsStale { get; private set; }

    public bool IsProvisional { get; private set; }

    public bool CanNavigate { get; }

    /// <summary>Honest route wording for this task's harness.</summary>
    public string NavigationHint { get; }

    public string NumberText => "#" + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Uses the same visual status palette as the overall HUD.</summary>
    public string StatusKind => IsStale || IsProvisional ? "uncertain"
        : NeedsHuman || TaskState is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput ? "attention"
        : TaskState == AgentTaskState.Error ? "error"
        : TaskState == AgentTaskState.Working ? "working" : "idle";

    /// <summary>
    /// One concise metadata line: the harness badge plus the single most useful
    /// status word. Attention rows name the blocking state; ordinary rows name what
    /// the agent is doing. Marks (stale, provisional, cancelled, failed) are always
    /// kept. The full state, timestamps and evidence stay in the tooltip/detail text.
    /// </summary>
    public string RowSubtitle
    {
        get
        {
            var attention = NeedsHuman || TaskState is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput;
            var status = attention || ActionText.Length == 0 ? StateText : ActionText;
            return MarksText.Length > 0
                ? HarnessText + " · " + status + " · " + MarksText
                : HarnessText + " · " + status;
        }
    }

    /// <summary>
    /// True while this row's own hour strip is disclosed. History is a separate
    /// control: revealing it never activates the task and never changes monitoring.
    /// </summary>
    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        set
        {
            if (_isHistoryOpen == value)
            {
                return;
            }

            _isHistoryOpen = value;
            Raise(nameof(IsHistoryOpen));
            Raise(nameof(HistoryAutomationName));
            HistoryToggled?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the user discloses or closes this row's history.</summary>
    public event EventHandler? HistoryToggled;

    public string HistoryAutomationName =>
        (_isHistoryOpen ? "Hide" : "Show") + " the last hour for task #" +
        Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Label;

    public string AgeText
    {
        get => _ageText;
        private set => Set(ref _ageText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    public string AgeHint { get => _ageHint; private set => Set(ref _ageHint, value); }

    /// <summary>Ordering rank: attention first, then work, then settled rows.</summary>
    public static int Rank(AgentTask task) => task.NeedsHuman && task.State == AgentTaskState.Working ? 1 : task.State switch
    {
        AgentTaskState.NeedsApproval => 0,
        AgentTaskState.NeedsInput => 1,
        AgentTaskState.Error => 2,
        AgentTaskState.Working => 3,
        AgentTaskState.Idle => 4,
        _ => 5,
    };

    public void Update(AgentTask task)
    {
        Label = HudText.Label(task);
        StateText = HudText.State(task.State);
        ActionText = task.Action.Label;
        HarnessText = HudText.Harness(task);
        MarksText = HudText.Marks(task);
        TaskState = task.State;
        NeedsHuman = task.NeedsHuman;
        IsStale = task.IsStale;
        IsProvisional = task.IsProvisional;
        TurnId = task.TurnId;
        _startedAt = task.StartedAt;
        Raise(nameof(Label));
        Raise(nameof(StateText));
        Raise(nameof(ActionText));
        Raise(nameof(HarnessText));
        Raise(nameof(MarksText));
        Raise(nameof(TaskState));
        Raise(nameof(NeedsHuman));
        Raise(nameof(IsStale));
        Raise(nameof(IsProvisional));
        Raise(nameof(TurnId));
        Raise(nameof(RowSubtitle));
        Raise(nameof(NumberText));
        Raise(nameof(StatusKind));
    }

    /// <summary>Cheap relative-age refresh; no monitor I/O and no reordering.</summary>
    public void UpdateAge(DateTimeOffset now, DateTimeOffset lastActivity)
    {
        var active = TaskState is AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval;
        var relative = HudText.Relative(now - lastActivity);
        // "now" already carries the sense of the suffix; "now ago" is not a label.
        var freshness = relative == "now" ? "now" : relative + " ago";
        AgeText = active ? _startedAt is { } start ? HudText.Relative(now - start) : "—" : freshness;
        AgeHint = active
            ? (_startedAt is null ? "Turn start was not observed." : "Elapsed this turn: " + AgeText + ".") +
              " Last activity: " + freshness + (IsStale ? ". Activity is stale; current work is unconfirmed." : ".")
            : "Last activity: " + freshness + ".";
        DetailText = "#" + Number.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                     " · " + HarnessText +
                     " · " + StateText +
                     " · " + ActionText +
                     " · " + AgeHint +
                     (MarksText.Length > 0 ? " · " + MarksText : string.Empty) +
                     "\n" + NavigationHint +
                     "\nThis task's hour: " + _taskStripText;
    }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (!string.Equals(field, value, StringComparison.Ordinal))
        {
            field = value;
            Raise(name);
        }
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
