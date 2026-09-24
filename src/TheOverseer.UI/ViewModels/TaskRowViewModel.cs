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

    private IReadOnlyList<ActivityCategory> _projectBins = Array.Empty<ActivityCategory>();
    private IReadOnlyList<ActivityCategory> _taskBins = Array.Empty<ActivityCategory>();
    private string _projectBinsSignature = string.Empty;
    private string _taskBinsSignature = string.Empty;

    public TaskRowViewModel(
        AgentTask task,
        bool canNavigate,
        string navigationHint,
        string projectGroupKey = "",
        string projectGroupLabel = "",
        string projectGroupMeta = "",
        ProjectId projectGroupProjectId = default)
    {
        ProjectGroupKey = projectGroupKey;
        ProjectGroupLabel = projectGroupLabel;
        ProjectGroupMeta = projectGroupMeta;
        ProjectGroupProjectId = projectGroupProjectId;
        Key = task.Key;
        Number = task.Number;
        Label = HudText.Label(task);
        StateText = HudText.State(task.State);
        ActionText = task.Action.Label;
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

    /// <summary>Project display name shown in the group header (never a path).</summary>
    public string ProjectGroupLabel { get; }

    /// <summary>Project id for this row's group; used to look up its activity strip.</summary>
    public ProjectId ProjectGroupProjectId { get; }

    /// <summary>Bounded group summary: counts and harness badges, plus coverage.</summary>
    public string ProjectGroupMeta { get; }

    /// <summary>
    /// Last-hour project activity strip (the group header renders the first row's
    /// bins). Unknown bins are rendered distinctly from confirmed idle.
    /// </summary>
    public IReadOnlyList<ActivityCategory> ProjectBins => _projectBins;

    /// <summary>Last-hour activity strip for this task.</summary>
    public IReadOnlyList<ActivityCategory> TaskBins => _taskBins;

    /// <summary>Updates the strips only when they actually changed.</summary>
    public void UpdateActivity(IReadOnlyList<ActivityCategory> projectBins, IReadOnlyList<ActivityCategory> taskBins)
    {
        var projectSignature = Signature(projectBins);
        if (!string.Equals(projectSignature, _projectBinsSignature, StringComparison.Ordinal))
        {
            _projectBins = projectBins;
            _projectBinsSignature = projectSignature;
            Raise(nameof(ProjectBins));
        }

        var taskSignature = Signature(taskBins);
        if (!string.Equals(taskSignature, _taskBinsSignature, StringComparison.Ordinal))
        {
            _taskBins = taskBins;
            _taskBinsSignature = taskSignature;
            Raise(nameof(TaskBins));
        }
    }

    private static string Signature(IReadOnlyList<ActivityCategory> bins)
    {
        if (bins.Count == 0)
        {
            return string.Empty;
        }

        // One character per bin: cheap, allocation-light change detection.
        var buffer = new char[bins.Count];
        for (var index = 0; index < bins.Count; index++)
        {
            buffer[index] = (char)('a' + (int)bins[index]);
        }

        return new string(buffer);
    }

    public int Number { get; }

    public string Label { get; private set; }

    public string StateText { get; private set; }

    public string ActionText { get; private set; }

    public string MarksText { get; private set; }

    public AgentTaskState TaskState { get; private set; }

    public ClientSurface Surface { get; }

    public string TurnId { get; private set; }

    public bool NeedsHuman { get; private set; }

    public bool IsStale { get; private set; }

    public bool IsProvisional { get; private set; }

    public bool CanNavigate { get; }

    public string NavigationHint { get; }

    public string NumberText => "#" + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Uses the same visual status palette as the overall HUD.</summary>
    public string StatusKind => IsStale || IsProvisional ? "uncertain"
        : NeedsHuman || TaskState is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput ? "attention"
        : TaskState == AgentTaskState.Error ? "error"
        : TaskState == AgentTaskState.Working ? "working" : "idle";

    /// <summary>State, action and marks on one compact metadata line.</summary>
    public string RowSubtitle => MarksText.Length > 0
        ? StateText + " · " + ActionText + " · " + MarksText
        : StateText + " · " + ActionText;

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
        var freshness = HudText.Relative(now - lastActivity) + " ago";
        AgeText = active ? _startedAt is { } start ? HudText.Relative(now - start) : "—" : freshness;
        AgeHint = active
            ? (_startedAt is null ? "Turn start was not observed." : "Elapsed this turn: " + AgeText + ".") +
              " Last activity: " + freshness + (IsStale ? ". Activity is stale; current work is unconfirmed." : ".")
            : "Last activity: " + freshness + ".";
        DetailText = "#" + Number.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                     " · " + StateText +
                     " · " + ActionText +
                     " · " + AgeHint +
                     (MarksText.Length > 0 ? " · " + MarksText : string.Empty);
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
