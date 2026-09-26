using System.ComponentModel;
using System.Runtime.CompilerServices;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// One expandable project row: the HUD's primary grouping. The row owns the project
/// name, harness badges, working/needs-you counts, its compact last-hour strip and
/// the task rows revealed when it is expanded.
///
/// Ordering uses a sortable key (attention, then active, then recent) with the
/// project identity as the final tie-break, so two projects with the same display
/// name stay separate and rows never shuffle between ticks. Expansion is supplied by
/// the view model from a set keyed on ProjectId, so a snapshot update that rebuilds
/// these rows cannot lose it and cannot restart observation.
/// </summary>
public sealed class ProjectRowViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<ActivityStripBin> _bins = Array.Empty<ActivityStripBin>();
    private string _binsSignature = string.Empty;
    private string _stripText = ActivityStripText.Describe(Array.Empty<ActivityStripBin>());
    private bool _isExpanded = true;
    private bool _isHistoryOpen;
    private bool _isPartial;
    private int _working;
    private int _needsHuman;
    private string _badges = string.Empty;

    public ProjectRowViewModel(
        ProjectDescriptor project,
        string key,
        IReadOnlyList<TaskRowViewModel> tasks,
        int working,
        int needsHuman,
        bool partial,
        IReadOnlyList<string> harnesses)
    {
        ProjectId = project.Id;
        Key = key;
        Name = project.IsUnassigned ? "Unassigned" : project.DisplayName;
        Tasks = tasks;
        _working = working;
        _needsHuman = needsHuman;
        _isPartial = partial;
        _badges = string.Join(", ", harnesses);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectId ProjectId { get; }

    /// <summary>Sortable group key: rank, display name, then opaque identity.</summary>
    public string Key { get; }

    /// <summary>Project display name. Never a local root path.</summary>
    public string Name { get; }

    /// <summary>Task rows of this project, in the view's row order.</summary>
    public IReadOnlyList<TaskRowViewModel> Tasks { get; }

    /// <summary>Last-hour strip: one bounded bin per minute of the rolling hour.</summary>
    public IReadOnlyList<ActivityStripBin> Bins => _bins;

    /// <summary>Working members of this project, from the monitor's own task states.</summary>
    public int WorkingCount => _working;

    /// <summary>Members currently asking for the user.</summary>
    public int NeedsHumanCount => _needsHuman;

    /// <summary>
    /// True while this project's hour history is disclosed. The legend and axis are
    /// shared and appear with the first open history area; closing it stops the
    /// geometry projection without touching observation, recording or voice.
    /// </summary>
    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        set
        {
            if (Set(ref _isHistoryOpen, value))
            {
                Raise(nameof(HistoryAutomationName));
                Raise(nameof(HeaderTooltip));
                HistoryToggled?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when the user discloses or closes this project's history.</summary>
    public event EventHandler? HistoryToggled;

    public string HistoryAutomationName =>
        (_isHistoryOpen ? "Hide" : "Show") + " the last hour for project " + Name;

    /// <summary>Plain-text strip summary for the tooltip and automation name.</summary>
    public string StripText
    {
        get => _stripText;
        private set => Set(ref _stripText, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (Set(ref _isExpanded, value))
            {
                Raise(nameof(ToggleGlyph));
                Raise(nameof(ToggleTooltip));
                Raise(nameof(ToggleAutomationName));
            }
        }
    }

    public string ToggleGlyph => _isExpanded ? "\u25BE" : "\u25B8";

    public string CountsText => _working.ToString(System.Globalization.CultureInfo.InvariantCulture) + " working · " +
        _needsHuman.ToString(System.Globalization.CultureInfo.InvariantCulture) + " needs you";

    /// <summary>Counts and harness badges; partial coverage is appended, never hidden.</summary>
    public string SummaryText => _isPartial
        ? CountsText + (_badges.Length > 0 ? " · " + _badges : string.Empty) + " · partial"
        : CountsText + (_badges.Length > 0 ? " · " + _badges : string.Empty);

    public string ToggleTooltip => (_isExpanded ? "Collapse " : "Expand ") + Name +
        " · " + SummaryText + " · " + StripText;

    public string ToggleAutomationName => (_isExpanded ? "Collapse project " : "Expand project ") + Name +
        " (" + SummaryText + ")";

    /// <summary>Header tooltip: counts, coverage, strip detail and the click action.</summary>
    public string HeaderTooltip => Name + " · " + SummaryText +
        (_isHistoryOpen ? "\n" + StripText : string.Empty) +
        "\n" + (_isExpanded ? "Click to collapse this project's tasks." : "Click to expand this project's tasks.") +
        "\n" + (_isHistoryOpen ? "History is open." : "Use History to show the last hour.");

    public bool IsPartial => _isPartial;

    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    /// <summary>Applies the strip only when it actually changed.</summary>
    public void UpdateBins(IReadOnlyList<ActivityStripBin> bins)
    {
        var signature = Signature(bins);
        if (string.Equals(signature, _binsSignature, StringComparison.Ordinal))
        {
            return;
        }

        _bins = bins;
        _binsSignature = signature;
        Raise(nameof(Bins));
        StripText = ActivityStripText.Describe(bins);
    }

    /// <summary>Refreshes the counts/coverage of an existing row in place.</summary>
    public void UpdateSummary(int working, int needsHuman, bool partial, IReadOnlyList<string> harnesses)
    {
        var badges = string.Join(", ", harnesses);
        if (_working == working && _needsHuman == needsHuman && _isPartial == partial &&
            string.Equals(_badges, badges, StringComparison.Ordinal))
        {
            return;
        }

        _working = working;
        _needsHuman = needsHuman;
        _isPartial = partial;
        _badges = badges;
        Raise(nameof(CountsText));
        Raise(nameof(SummaryText));
        Raise(nameof(IsPartial));
        Raise(nameof(ToggleTooltip));
        Raise(nameof(ToggleAutomationName));
        Raise(nameof(HeaderTooltip));
    }

    /// <summary>
    /// Per-bin change signature: category, evidence, coverage, mixed and task count.
    /// Cheap and allocation-light, and it never raises on an unchanged tick.
    /// </summary>
    private static string Signature(IReadOnlyList<ActivityStripBin> bins)
    {
        if (bins.Count == 0)
        {
            return string.Empty;
        }

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

/// <summary>
/// One legend entry for the shared hour axis. The legend is text as well as colour,
/// so the strip is readable without relying on the palette.
/// </summary>
public sealed record ActivityLegendEntry(ActivityCategory Category, string Label, string Meaning)
{
    /// <summary>Screen-reader text for this legend entry.</summary>
    public string Description => Label + " — " + Meaning;
}
