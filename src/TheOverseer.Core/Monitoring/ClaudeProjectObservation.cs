using System.Security.Cryptography;
using System.Text;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Core.Monitoring;

/// <summary>Allowlisted status metadata from one visible provider project.</summary>
public sealed record ClaudeProjectWorker(string ThreadId, string Group, bool Disconnected = false)
{
    public string Title { get; init; } = "";

    public bool HasSameStatus(ClaudeProjectWorker other) => ThreadId == other.ThreadId &&
        Group == other.Group && Disconnected == other.Disconnected;

    public static string DisplayTitle(string? title)
    {
        if (title is null || title.Length > 1024 || title.Any(c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)) return "";
        title = title.Trim();
        if (title.Length <= 160) return title;
        var length = char.IsHighSurrogate(title[158]) ? 158 : 159;
        return title[..length] + "…";
    }
}
public sealed record ClaudeProjectObservation(string Scope, string ProjectId, string ProjectName,
    DateTimeOffset ObservedAt, IReadOnlyList<ClaudeProjectWorker> Workers, bool MembershipComplete);
public sealed record ClaudeProjectRead(IReadOnlyList<ClaudeProjectObservation> Projects, string Reason);

/// <summary>
/// Folds displayed project status, without pretending an open pane covers an
/// account. It owns no UI automation, I/O, project navigation or voice playback.
/// </summary>
public sealed class ClaudeProjectTracker
{
    private readonly SourceInstance _source;
    private readonly Dictionary<TaskKey, AgentTask> _tasks = new();
    private readonly AttentionDetector _attention = new(new MonitorOptions());
    private DateTimeOffset _lastRead;
    private bool _attached;

    public ClaudeProjectTracker(SourceInstance source) => _source = source;

    public SourceSnapshot Apply(ClaudeProjectRead read, DateTimeOffset now, int maximumSessions)
    {
        maximumSessions = Math.Clamp(maximumSessions, 1, SourceBudget.DefaultAggregateSessions);
        var seen = new Dictionary<TaskKey, ClaudeProjectWorker>();
        var transitions = new List<TaskTransition>();
        var reasons = new HashSet<string>(StringComparer.Ordinal) { "projects-visible-overviews-only", "projects-account-coverage-unverified" };
        if (read.Reason != "ok") reasons.Add(read.Reason);
        var readAny = false;
        var acceptedAt = _lastRead;
        foreach (var project in read.Projects.Take(8))
        {
            if (!ValidId(project.Scope) || !ValidId(project.ProjectId) || project.ObservedAt > now ||
                project.ObservedAt < _lastRead || now - project.ObservedAt > TimeSpan.FromSeconds(10))
            { reasons.Add("projects-observation-rejected"); continue; }
            if (project.ObservedAt > acceptedAt) acceptedAt = project.ObservedAt;
            var projectHash = Hash(project.Scope, project.ProjectId);
            var name = project.ProjectName.Length <= 80 && !project.ProjectName.Any(char.IsControl)
                ? project.ProjectName : "Claude project";
            var descriptor = new ProjectDescriptor(ProjectId.FromKey("project:claude:" + projectHash),
                name, "", ProjectResolution.Explicit);
            foreach (var worker in project.Workers.Take(Math.Clamp(maximumSessions, 1, SourceBudget.DefaultAggregateSessions) + 1))
            {
                if (!ValidId(worker.ThreadId)) { reasons.Add("projects-worker-identity-rejected"); continue; }
                var key = TaskKey.For(HarnessIds.Claude, _source.SourceInstanceId, Hash(project.Scope, project.ProjectId, worker.ThreadId));
                if (seen.TryGetValue(key, out var observed))
                {
                    // Mirrored loaded views can repeat the same observation.
                    // Different status/disconnection evidence remains uncertain.
                    if (observed.HasSameStatus(worker))
                    {
                        if (ClaudeProjectWorker.DisplayTitle(observed.Title).Length == 0 && ClaudeProjectWorker.DisplayTitle(worker.Title) is { Length: > 0 } duplicateTitle &&
                            _tasks.TryGetValue(key, out var mirrored)) _tasks[key] = mirrored with { Label = duplicateTitle };
                        continue;
                    }
                    reasons.Add("projects-duplicate-observation");
                    if (_tasks.TryGetValue(key, out var duplicate)) _tasks[key] = duplicate with { IsStateUncertain = true };
                    continue;
                }
                seen.Add(key, worker);
                _tasks.TryGetValue(key, out var old);
                if (old is null && _tasks.Count >= maximumSessions)
                {
                    var removable = _tasks.FirstOrDefault(pair => pair.Value.IsStale && !pair.Value.HasAttention);
                    if (removable.Value is not null) _tasks.Remove(removable.Key);
                    else { reasons.Add("projects-session-limit"); continue; }
                }
                var mapped = Map(worker.Group);
                var uncertain = mapped is null || worker.Disconnected;
                var state = mapped ?? old?.State ?? AgentTaskState.Idle;
                if (uncertain && old?.HasAttention == true) state = old.State;
                var changed = old is null || old.State != state;
                var label = ClaudeProjectWorker.DisplayTitle(worker.Title);
                if (label.Length == 0) label = old?.Label is { Length: > 0 } saved ? saved :
                    worker.ThreadId.Length > 8 ? worker.ThreadId[^8..] : worker.ThreadId;
                var action = state switch
                {
                    AgentTaskState.Working => AgentActionKind.Thinking,
                    AgentTaskState.NeedsInput => AgentActionKind.WaitingForInput,
                    _ => AgentActionKind.Idle,
                };
                var task = new AgentTask(key, old?.Number ?? 0, key.ConversationId, _source.SourceInstanceId,
                    ClientSurface.Desktop, label,
                    "displayed-status", state, new AgentAction(action, EvidenceKind.Structured, "project-displayed-status"),
                    EvidenceKind.Structured, old?.FirstSeenAt ?? project.ObservedAt,
                    changed ? project.ObservedAt : old!.LastActivityAt,
                    changed ? project.ObservedAt : old!.StateSince,
                    state == AgentTaskState.Working ? old?.StartedAt ?? project.ObservedAt : old?.StartedAt,
                    null, worker.Disconnected, false, state == AgentTaskState.NeedsInput,
                    false, false, state == AgentTaskState.NeedsInput ? "project-attention-needed" : null,
                    0, "", descriptor) { IsStateUncertain = uncertain };
                _tasks[key] = task;
                readAny = true;
                // Newly discovered workers and returns from a missing pane are
                // baseline. Closed/collapsed panes cannot replay attention later.
                if (changed && old is not null && old.HasCurrentState && task.HasCurrentState && _attached)
                    transitions.Add(new(TaskTransitionKind.StateChanged, key, 0, old.State, state,
                        "project-status-changed", task.TurnId, false, project.ObservedAt));
            }
            if (!project.MembershipComplete) reasons.Add("projects-membership-incomplete");
        }
        foreach (var key in _tasks.Keys.ToArray())
            if (!seen.ContainsKey(key)) _tasks[key] = _tasks[key] with { IsStale = true, IsStateUncertain = true };
        var tasks = _tasks.Values.ToArray();
        var events = _attention.Detect(tasks, transitions, now, coverageComplete: false);
        if (acceptedAt > _lastRead)
        {
            _lastRead = acceptedAt;
            _attached = true;
            _attention.MarkHydrationComplete();
        }
        var capabilities = MonitorCapabilities.Initial with
        {
            LifecycleEvents = new(CapabilitySupport.Supported, "projects-displayed-status", "Status displayed by open Claude Projects overviews; no execution-liveness claim."),
            PermissionRequests = new(CapabilitySupport.Unsupported, "projects-approval-unspecified", "Waiting on you does not identify a permission request."),
            InputRequests = new(CapabilitySupport.Supported, "projects-attention-group", "Waiting on you / Ready for review groups require attention; no question body is read."),
            TaskFailureEvidence = new(CapabilitySupport.Unsupported, "projects-failure-unspecified", "Group membership alone does not establish failure or success."),
            SessionTitles = new(CapabilitySupport.Supported, "projects-displayed-title", "Dedicated displayed worker titles when exposed; opaque IDs remain the fallback. Message snippets are not read."),
        };
        return new(_source, now, readAny ? MonitoringHealthLevel.Degraded : MonitoringHealthLevel.Unavailable,
            reasons.Order(StringComparer.Ordinal).ToArray(), false, tasks, events, capabilities,
            "claude-projects-overview", _lastRead == default ? now : _lastRead);
    }

    private static AgentTaskState? Map(string group) => group switch
    {
        "Working" or "Landing" => AgentTaskState.Working,
        "Waiting on you" or "Ready for review" => AgentTaskState.NeedsInput,
        // Resolving a thread can be manual. It is not evidence of successful work.
        "Idle" or "Resolved" => AgentTaskState.Idle,
        _ => null,
    };

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static string Hash(params string[] parts) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(parts)))).ToLowerInvariant();
}
