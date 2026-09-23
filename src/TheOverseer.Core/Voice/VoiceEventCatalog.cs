namespace TheOverseer.Core.Voice;

public sealed record VoiceEventDefinition(VoiceEventKind Kind, string Code, string Name, string Trigger, string Script, bool DefaultEnabled = true);

/// <summary>One shared recording list for settings, manifests and notification policy.</summary>
public static class VoiceEventCatalog
{
    public static IReadOnlyList<VoiceEventDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new VoiceEventDefinition(VoiceEventKind.PermissionRequired, "permission_required", "Approval needed", "Once when an agent needs approval. Requires observable approval events.", "Your approval is needed."),
        new VoiceEventDefinition(VoiceEventKind.InputRequired, "input_required", "Input needed", "Once when an agent requests an answer. Requires observable input events.", "A task needs your input."),
        new VoiceEventDefinition(VoiceEventKind.TaskError, "task_error", "Task failed", "On a new failure; user cancellation stays silent.", "A task ran into a problem."),
        new VoiceEventDefinition(VoiceEventKind.TaskCompleted, "task_complete", "Task finished", "Completions within two seconds are grouped.", "Task complete."),
        new VoiceEventDefinition(VoiceEventKind.AllTasksCompleted, "all_tasks_complete", "All tasks finished", "Only when complete monitoring confirms it. Replaces individual completion cues.", "All tasks are finished."),
        new VoiceEventDefinition(VoiceEventKind.MonitoringLost, "monitoring_lost", "Monitoring lost", "Once after monitoring is unavailable for 10 seconds.", "Monitoring connection lost."),
        new VoiceEventDefinition(VoiceEventKind.MonitoringRestored, "monitoring_restored", "Monitoring restored", "Only after a loss announcement successfully played.", "Monitoring is back online."),
        new VoiceEventDefinition(VoiceEventKind.StillWaiting, "still_waiting", "Still waiting", "One reminder after five minutes of a newly observed, unresolved request.", "A task is still waiting for you.", false),
        new VoiceEventDefinition(VoiceEventKind.SystemOnline, "system_online", "System online", "Once at launch when monitoring becomes available. Never on unmute or resume.", "System online.", false),
    });

    public static VoiceEventDefinition For(VoiceEventKind kind) => All.First(item => item.Kind == kind);
}
