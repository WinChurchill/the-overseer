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
        HarnessIds.Dsh => "DSH",
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

}
