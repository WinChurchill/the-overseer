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
        // Expansion defaults and attention episodes come from the whole snapshot, before
        // the filtered selection decides which rows are shown.
        if (ApplyProjectStateDefaults(snapshot.Tasks))
        {
            ApplyProjectExpansion();
        }

        // Select first (Now/Recent/This session), then group, then order the projects
        // attention -> active -> recent. Rows of one project stay contiguous so the
        // project order is the group order and cannot jitter between ticks.
        var entries = GroupRows(SelectTasks(snapshot.Tasks, now), snapshot.Tasks);
        SortRows(entries, IsRecentView);
        TaskCountLine = entries.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " shown · " +
            snapshot.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tracked";
        if (snapshot.Tasks.Count > 0 && snapshot.Health.Level != MonitoringHealthLevel.Unavailable)
            EmptyStateText = HasActiveFilter
                ? "No observed tasks match the " + (IsFilterAttention ? "Needs you" : "Working") + " filter. Try All."
                : TaskView switch
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
        for (var index = 0; index < entries.Length; index++)
        {
            var (task, group) = entries[index];
            var row = new TaskRowViewModel(task, CanNavigate(task), NavigationHint(task), group.Key, group.Label, group.Meta,
                group.ProjectId, !IsProjectCollapsed(group.ProjectId));
            row.HistoryToggled += OnTaskHistoryToggled;
            row.RestoreHistoryOpen(_openTaskHistory.Contains(task.Key));
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
            project.RestoreHistoryOpen(_openProjectHistory.Contains(group.ProjectId));
            Projects.Add(project);
            start = end;
        }

        HasTasks = Tasks.Count > 0;
        Raise(nameof(AnyProjectHistoryOpen));
        Raise(nameof(ShowHistoryLegend));
    }

    // ---- expansion defaults and history disclosure state -------------------------

}
