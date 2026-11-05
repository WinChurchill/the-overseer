using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.State;

public sealed partial class AgentStateReducer
{
    private AgentAction UpdateAction(MutableTask task)
    {
        var activeTools = new List<ActiveToolView>(task.ActiveTools.Count);
        foreach (var tool in task.ActiveTools.Values)
        {
            if (tool.TurnId.Length > 0 && task.TurnId.Length > 0 &&
                !string.Equals(tool.TurnId, task.TurnId, StringComparison.Ordinal))
            {
                continue;
            }

            activeTools.Add(new ActiveToolView(tool.Category, tool.Action, tool.DetailCode, tool.Order));
        }

        var hasActivity = task.LastActivity is not null &&
                          (task.LastActivity.TurnId.Length == 0 || task.TurnId.Length == 0 ||
                           string.Equals(task.LastActivity.TurnId, task.TurnId, StringComparison.Ordinal));

        var context = new ClassifierContext(
            task.State,
            task.Requests.Values.Any(static request => request.IsPermission),
            task.Requests.Values.Any(static request => !request.IsPermission && request.IsBlocking),
            activeTools,
            hasActivity ? task.LastActivity!.Action : null,
            hasActivity ? task.LastActivity!.DetailCode : null,
            hasActivity ? task.LastActivity!.Category : ToolCallCategory.None,
            hasActivity,
            task.CancellationObserved);

        return _classifier.Classify(context);
    }

    private AgentTask ToImmutable(MutableTask task, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? task.LastActivityAt;
        bool IsFresh(RequestRecord request) => now >= request.Since && now - request.Since <= _options.StaleLease;
        var asyncInputs = task.Requests.Values.Where(static request => !request.IsPermission && !request.IsBlocking).ToArray();
        // Closing a desktop async prompt has no rollout event. Retain its ID for
        // a correlated reply; unrelated work cannot renew its current-wait claim.
        return new(
        task.Key,
        task.Number,
        task.ConversationId,
        task.ProfileId,
        task.Surface,
        task.Label,
        task.TurnId,
        task.State,
        task.Action,
        task.Evidence,
        task.FirstSeenAt,
        task.LastActivityAt,
        task.StateSince,
        task.StartedAt,
        task.CompletedAt,
        task.IsStale,
        !task.IdentityResolved,
        task.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput || asyncInputs.Any(IsFresh) || task.RequestOverflowed,
        task.CancellationObserved,
        task.FailureCode is not null,
        task.AttentionCode,
        task.SourceFiles.Count,
        task.WorkspaceRoot,
        task.Project)
    {
        IsStateUncertain = task.RequestOverflowed,
        HasUnconfirmedInput = asyncInputs.Any(request => !IsFresh(request)),
    };
    }

    private sealed class MutableTask
    {
        public TaskKey Key;
        public int Number;
        public string ProfileId = string.Empty;
        public string ConversationId = string.Empty;
        public ClientSurface Surface = ClientSurface.Unknown;
        public string Label = string.Empty;
        public bool IdentityResolved;
        public string WorkspaceRoot = string.Empty;
        public ProjectDescriptor? Project;
        public string TurnId = string.Empty;
        public DateTimeOffset TurnStartedAt;
        public DateTimeOffset TurnLastActivityAt;
        public int TurnOrder;
        public AgentTaskState State;
        public EvidenceKind Evidence;
        public AgentAction Action = AgentAction.Idle;
        public string StateCauseCode = string.Empty;
        public DateTimeOffset FirstSeenAt;
        public DateTimeOffset LastActivityAt;
        public DateTimeOffset StateSince;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
        public bool HasExplicitLifecycle;
        public bool CancellationObserved;
        public string? FailureCode;
        public string? AttentionCode;
        public bool NonBlockingInputPending;
        public bool RequestOverflowed;
        public bool IsStale;
        public bool HasActivityInCurrentTurn;
        public int ActivityOrder;
        public readonly Dictionary<string, RequestRecord> Requests = new(StringComparer.Ordinal);
        public readonly Dictionary<string, TurnRecord> Turns = new(StringComparer.Ordinal);
        public readonly Queue<string> TurnSequence = new();
        public readonly Dictionary<string, ToolRecord> ActiveTools = new(StringComparer.Ordinal);
        public readonly Queue<string> ToolOrder = new();
        public readonly HashSet<string> SeenMarkers = new(StringComparer.Ordinal);
        public readonly Queue<string> SeenOrder = new();
        public readonly HashSet<int> SourceFiles = new();
        public ActivityRecord? LastActivity;
        public int TurnLimit = 64;
        public int RequestLimit = 64;
        public int ToolLimit = 32;
        public int SeenMarkerLimit = 512;
    }

    private sealed class RequestRecord
    {
        public string RequestId = string.Empty;
        public bool IsPermission;
        public bool IsBlocking;
        public string TurnId = string.Empty;
        public DateTimeOffset Since;
        public int? QuestionCount;
        public readonly HashSet<int> AnsweredQuestions = new();
    }

    private sealed class TurnRecord
    {
        public string TurnId = string.Empty;
        public DateTimeOffset LastActivityAt;
        public bool Terminal;
        public TurnOutcome? Outcome;
        public int Order;
    }

    private sealed class ToolRecord
    {
        public string CallId = string.Empty;
        public ToolCallCategory Category;
        public AgentActionKind? Action;
        public string? DetailCode;
        public string TurnId = string.Empty;
        public int Order;
    }

    private sealed class ActivityRecord
    {
        public ToolCallCategory Category;
        public AgentActionKind? Action;
        public string? DetailCode;
        public string TurnId = string.Empty;
        public int Order;
    }}
