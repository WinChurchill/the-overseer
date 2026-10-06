using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

public sealed partial class HudViewModel : INotifyPropertyChanged
{
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

}
