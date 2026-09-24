using System.Collections.ObjectModel;
using System.ComponentModel;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// One discovered harness source. Enablement is the only editable property and it
/// maps onto the existing additive <c>disabledHarnessSources</c> preference; no new
/// registry, discovery mechanism or source of truth is introduced.
/// </summary>
public sealed class ProjectSourceRow : INotifyPropertyChanged
{
    private bool _enabled;

    public ProjectSourceRow(string harnessId, string sourceInstanceId, string detail, bool enabled)
    {
        HarnessId = HarnessIds.Normalize(harnessId);
        SourceInstanceId = sourceInstanceId;
        Detail = detail;
        _enabled = enabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string HarnessId { get; }

    public string SourceInstanceId { get; }

    public string Name => HarnessIds.DisplayName(HarnessId) + " · " + SourceInstanceId;

    /// <summary>Observed tasks and coverage, never a path.</summary>
    public string Detail { get; }

    /// <summary>Stable "harness/sourceInstance" key used by the config record.</summary>
    public string Key => HarnessId + "/" + SourceInstanceId;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        }
    }

    public string StateText => _enabled ? "Enabled" : "Disabled";
}

/// <summary>One project bucket: a name and its observed task counts.</summary>
public sealed record ProjectBucketRow(string Name, string Detail);

/// <summary>
/// Bounded "Projects &amp; sources…" surface. It only projects values the controller
/// already holds (observed tasks, project grouping and the persisted disabled-source
/// list); the view performs no discovery and no I/O.
/// </summary>
public sealed class ProjectSourcesViewModel
{
    public ProjectSourcesViewModel(
        IEnumerable<ProjectSourceRow> sources,
        IEnumerable<ProjectBucketRow> projects,
        string note)
    {
        Sources = new ObservableCollection<ProjectSourceRow>(sources);
        Projects = projects.ToArray();
        Note = note;
        foreach (var source in Sources)
        {
            source.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(ProjectSourceRow.Enabled))
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            };
        }
    }

    public event EventHandler? Changed;

    public ObservableCollection<ProjectSourceRow> Sources { get; }

    public IReadOnlyList<ProjectBucketRow> Projects { get; }

    public string Note { get; }

    public bool HasSources => Sources.Count > 0;

    /// <summary>Additive replacement for the persisted disabled-source list.</summary>
    public IReadOnlyList<string> DisabledHarnessSources =>
        Sources.Where(static source => !source.Enabled).Select(static source => source.Key)
            .Distinct(StringComparer.Ordinal).ToArray();
}
