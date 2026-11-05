using TheOverseer.Core.Models;

namespace TheOverseer.Core.History;

/// <summary>
/// Maps normalized task state into observed activity intervals, on the producer
/// side and before any UI coalescing.
///
/// Observations carry the task's own last-activity instant, not the aggregate
/// snapshot time, so a composite refresh that re-publishes an unchanged task can
/// never extend a stale action. Terminal states and plan updates are recorded as
/// point markers with a stable identity, so repeated publications of the same
/// episode do not accumulate duplicates or invent planning duration.
/// </summary>
public static class TaskActivityRecorder
{
    /// <summary>One snapshot records at most one observation per task.</summary>
    public static void Record(TaskActivityHistory history, MonitoringSnapshot snapshot)
    {
        if (history is null || snapshot is null)
        {
            return;
        }

        foreach (var task in snapshot.Tasks)
        {
            var observedAt = ObservedAt(task, snapshot.GeneratedAt);
            var classification = Classify(task);

            history.Record(
                task.Key,
                task.AssignedProject.Id,
                task.Key.HarnessId,
                classification.Category,
                classification.Evidence,
                classification.Detail,
                observedAt,
                classification.IsMarker,
                classification.MarkerIdentity);
        }
    }

    /// <summary>Displayed classification for one task, with its evidence strength.</summary>
    public static (ActivityCategory Category, ActivityEvidence Evidence, string Detail, bool IsMarker, string MarkerIdentity) Classify(AgentTask task)
    {
        if (!task.HasCurrentState || task.Evidence == EvidenceKind.None)
        {
            // No trustworthy observation: coverage stays unknown, never idle.
            return (ActivityCategory.Unknown, ActivityEvidence.Unknown, "no-observation", false, string.Empty);
        }

        return task.State switch
        {
            AgentTaskState.NeedsApproval => (ActivityCategory.Waiting, ActivityEvidence.Explicit, task.AttentionCode ?? "approval", false, string.Empty),
            AgentTaskState.NeedsInput => (ActivityCategory.Waiting, ActivityEvidence.Explicit, task.AttentionCode ?? "input", false, string.Empty),
            AgentTaskState.Done => (
                ActivityCategory.Complete,
                EvidenceKindToActivity(task.Evidence),
                "task-complete",
                true,
                MarkerIdentity(task, "complete")),
            AgentTaskState.Error => (
                ActivityCategory.Error,
                EvidenceKindToActivity(task.Evidence),
                task.AttentionCode ?? "task-error",
                true,
                MarkerIdentity(task, "error:" + (task.AttentionCode ?? "task-error"))),
            AgentTaskState.Idle => (ActivityCategory.Idle, EvidenceKindToActivity(task.Evidence), "settled", false, string.Empty),
            _ => ClassifyWorking(task),
        };
    }

    /// <summary>
    /// The instant the task was actually observed. Upstream evidence time is used so
    /// an aggregate refresh cannot renew a stale action, and it is clamped to the
    /// snapshot time so a skewed future timestamp cannot extend an interval either.
    /// </summary>
    public static DateTimeOffset ObservedAt(AgentTask task, DateTimeOffset snapshotAt)
    {
        if (task.LastActivityAt == default)
        {
            return snapshotAt;
        }

        return task.LastActivityAt > snapshotAt ? snapshotAt : task.LastActivityAt;
    }

    private static (ActivityCategory Category, ActivityEvidence Evidence, string Detail, bool IsMarker, string MarkerIdentity) ClassifyWorking(AgentTask task)
    {
        var detail = string.IsNullOrEmpty(task.Action.DetailCode)
            ? task.Action.Kind.ToString().ToLowerInvariant()
            : task.Action.DetailCode!;

        // A plan update is a point marker: plan mode alone is not sustained
        // planning time, so it never opens or extends a planning interval.
        if (detail.Contains("plan", StringComparison.OrdinalIgnoreCase) &&
            task.Action.Evidence is EvidenceKind.Explicit or EvidenceKind.Structured)
        {
            return (ActivityCategory.Planning, ActivityEvidence.Explicit, detail, true, MarkerIdentity(task, "plan:" + detail));
        }

        if (task.Action.Kind == AgentActionKind.Thinking)
        {
            return (ActivityCategory.Thinking, EvidenceKindToActivity(task.Action.Evidence), detail, false, string.Empty);
        }

        return task.Action.Kind switch
        {
            AgentActionKind.ReadingFiles or AgentActionKind.EditingFiles or AgentActionKind.RunningCommand
                or AgentActionKind.RunningTests or AgentActionKind.Searching =>
                (ActivityCategory.Working, EvidenceKindToActivity(task.Action.Evidence), detail, false, string.Empty),
            AgentActionKind.WaitingForApproval or AgentActionKind.WaitingForInput =>
                (ActivityCategory.Waiting, EvidenceKindToActivity(task.Action.Evidence), detail, false, string.Empty),
            _ => (ActivityCategory.Working, EvidenceKindToActivity(task.Evidence), detail, false, string.Empty),
        };
    }

    private static string MarkerIdentity(AgentTask task, string suffix) =>
        task.TurnId + ":" + suffix;

    private static ActivityEvidence EvidenceKindToActivity(EvidenceKind evidence) => evidence switch
    {
        EvidenceKind.Explicit => ActivityEvidence.Explicit,
        EvidenceKind.Structured => ActivityEvidence.Explicit,
        EvidenceKind.Inferred => ActivityEvidence.Inferred,
        EvidenceKind.Timing => ActivityEvidence.Inferred,
        _ => ActivityEvidence.Unknown,
    };
}
