using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public TaskRowViewModel(AgentTask task, bool canNavigate, string navigationHint)
    {
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
