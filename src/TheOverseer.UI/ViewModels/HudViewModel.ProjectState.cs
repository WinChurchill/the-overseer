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
    private readonly HashSet<ProjectId> _knownProjects = new();
    private readonly HashSet<ProjectId> _attentionEpisodes = new();
    private readonly HashSet<ProjectId> _userCollapsedInEpisode = new();
    private readonly HashSet<ProjectId> _openProjectHistory = new();
    private readonly HashSet<TaskKey> _openTaskHistory = new();

    /// <summary>Per-project facts a project-state decision needs.</summary>
    private struct ProjectActivity
    {
        public bool Attention;

        public bool Working;

        public bool Active => Attention || Working;
    }

    /// <summary>
    /// Expansion defaults and attention episodes are derived from the <b>full</b>
    /// snapshot, never from the displayed subset. A filter or a time window can hide a
    /// request, and a project can lose every displayed row between two snapshots; both
    /// must still start and end an episode correctly. Tracking the union of the current
    /// task set and the tracked episode set is what makes "a request disappears, then a
    /// new turn asks again" a genuinely new episode that may reveal once.
    ///
    /// First appearance expands attention/active projects and collapses settled ones;
    /// afterwards a user's choice is preserved. The reveal is skipped while the user has
    /// collapsed that project inside the current episode, and while everything is
    /// collapsed, so a rebuild never fights the user. Returns true when an expansion
    /// decision changed, so the caller can apply it to the existing rows.
    /// </summary>
    private bool ApplyProjectStateDefaults(IReadOnlyList<AgentTask> allTasks)
    {
        var states = new Dictionary<ProjectId, ProjectActivity>();
        foreach (var task in allTasks)
        {
            var id = task.AssignedProject.Id;
            if (!states.TryGetValue(id, out var state))
            {
                state = new ProjectActivity();
                states[id] = state;
            }

            if (IsAttention(task))
            {
                state.Attention = true;
            }

            if (task.State == AgentTaskState.Working && !task.IsStale)
            {
                state.Working = true;
            }

            states[id] = state;
        }

        // Projects with an open episode are considered even when they currently have no
        // tasks at all: that absence is exactly what ends the episode.
        var considered = new HashSet<ProjectId>(_attentionEpisodes);
        foreach (var id in states.Keys)
        {
            considered.Add(id);
        }

        var changed = false;
        foreach (var id in considered)
        {
            var present = states.TryGetValue(id, out var state);
            var attention = present && state.Attention;

            if (_knownProjects.Add(id))
            {
                if (!present || !state.Active)
                {
                    changed |= _collapsedProjects.Add(id);
                }

                if (attention)
                {
                    _attentionEpisodes.Add(id);
                }

                continue;
            }

            if (attention)
            {
                if (_attentionEpisodes.Add(id) && !_userCollapsedInEpisode.Contains(id) && !_allCollapsed)
                {
                    changed |= _collapsedProjects.Remove(id);
                }
            }
            else if (_attentionEpisodes.Remove(id))
            {
                // The episode is over: the next request is a new one and may reveal once.
                _userCollapsedInEpisode.Remove(id);
            }
        }

        return changed;
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

        OnTaskHistoryChanged();
    }

}
