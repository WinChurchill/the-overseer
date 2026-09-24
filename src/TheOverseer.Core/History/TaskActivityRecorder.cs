using TheOverseer.Core.Models;

namespace TheOverseer.Core.History;

/// <summary>
/// Maps normalized task state into observed activity intervals. It records only
/// what the snapshot actually reported at that instant: an active turn without a
/// structured action is <see cref="ActivityEvidence.Inferred"/> thinking, an
/// explicit plan action is planning, tool work is working, attention is waiting
/// and terminal states are markers. Unknown observation is never written as idle.
/// </summary>
public static class TaskActivityRecorder
{
    /// <summary>One snapshot records one observation per task.</summary>
    public static void Record(TaskActivityHistory history, MonitoringSnapshot snapshot)
    {
        if (history is null || snapshot is null)
        {
            return;
        }

        foreach (var task in snapshot.Tasks)
        {
            var (category, evidence, detail, isMarker) = Classify(task);
            history.Record(
                task.Key,
                task.AssignedProject.Id,
                task.Key.HarnessId,
                category,
                evidence,
                detail,
                snapshot.GeneratedAt,
                isMarker);

            // Completion and failure are markers over the action timeline, so the
            // underlying work interval is not erased by the terminal state.
            if (isMarker)
            {
                continue;
            }

            if (task.State == AgentTaskState.Done)
            {
                history.Record(task.Key, task.AssignedProject.Id, task.Key.HarnessId, ActivityCategory.Complete, EvidenceKindToActivity(task.Evidence), "task-complete", snapshot.GeneratedAt, isMarker: true);
            }
            else if (task.State == AgentTaskState.Error)
            {
                history.Record(task.Key, task.AssignedProject.Id, task.Key.HarnessId, ActivityCategory.Error, EvidenceKindToActivity(task.Evidence), task.AttentionCode ?? "task-error", snapshot.GeneratedAt, isMarker: true);
            }
        }
    }

    /// <summary>Displayed category for one task, with evidence strength.</summary>
    public static (ActivityCategory Category, ActivityEvidence Evidence, string Detail, bool IsMarker) Classify(AgentTask task)
    {
        if (task.IsStale || task.IsProvisional || task.Evidence == EvidenceKind.None)
        {
            // No trustworthy observation: coverage stays unknown, never idle.
            return (ActivityCategory.Unknown, ActivityEvidence.Unknown, "no-observation", false);
        }

        return task.State switch
        {
            AgentTaskState.NeedsApproval => (ActivityCategory.Waiting, ActivityEvidence.Explicit, task.AttentionCode ?? "approval", false),
            AgentTaskState.NeedsInput => (ActivityCategory.Waiting, ActivityEvidence.Explicit, task.AttentionCode ?? "input", false),
            AgentTaskState.Error => (ActivityCategory.Error, EvidenceKindToActivity(task.Evidence), task.AttentionCode ?? "task-error", true),
            AgentTaskState.Done => (ActivityCategory.Complete, EvidenceKindToActivity(task.Evidence), "task-complete", true),
            AgentTaskState.Idle => (ActivityCategory.Idle, EvidenceKindToActivity(task.Evidence), "settled", false),
            _ => ClassifyWorking(task),
        };
    }

    private static (ActivityCategory Category, ActivityEvidence Evidence, string Detail, bool IsMarker) ClassifyWorking(AgentTask task)
    {
        var detail = string.IsNullOrEmpty(task.Action.DetailCode)
            ? task.Action.Kind.ToString().ToLowerInvariant()
            : task.Action.DetailCode!;

        // Thinking is the inferred active-turn fallback unless the action itself is
        // explicit; there is no separate plan action kind, so a plan detail code is
        // planning only when the classifier reported it explicitly. It stays a
        // point observation, never sustained planning duration.
        if (task.Action.Kind == AgentActionKind.Thinking)
        {
            var planning = detail.Contains("plan", StringComparison.OrdinalIgnoreCase) &&
                           task.Action.Evidence is EvidenceKind.Explicit or EvidenceKind.Structured;
            return planning
                ? (ActivityCategory.Planning, ActivityEvidence.Explicit, detail, false)
                : (ActivityCategory.Thinking, EvidenceKindToActivity(task.Action.Evidence), detail, false);
        }

        return task.Action.Kind switch
        {
            AgentActionKind.ReadingFiles or AgentActionKind.EditingFiles or AgentActionKind.RunningCommand
                or AgentActionKind.RunningTests or AgentActionKind.Searching =>
                (ActivityCategory.Working, EvidenceKindToActivity(task.Action.Evidence), detail, false),
            AgentActionKind.WaitingForApproval or AgentActionKind.WaitingForInput =>
                (ActivityCategory.Waiting, EvidenceKindToActivity(task.Action.Evidence), detail, false),
            _ => (ActivityCategory.Working, EvidenceKindToActivity(task.Evidence), detail, false),
        };
    }

    private static ActivityEvidence EvidenceKindToActivity(EvidenceKind evidence) => evidence switch
    {
        EvidenceKind.Explicit => ActivityEvidence.Explicit,
        EvidenceKind.Structured => ActivityEvidence.Explicit,
        EvidenceKind.Inferred => ActivityEvidence.Inferred,
        EvidenceKind.Timing => ActivityEvidence.Inferred,
        _ => ActivityEvidence.Unknown,
    };
}
