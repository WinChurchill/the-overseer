namespace TheOverseer.Core.Models;

/// <summary>
/// Normalized structural event derived from one bounded source record.
/// No recorded prose, command line, tool argument or tool output is retained.
/// </summary>
public enum RawCodexEventKind
{
    Unknown,
    SessionIdentity,
    TurnContext,
    TaskStarted,
    TaskCompleted,
    TurnAborted,
    TaskFailed,
    ToolCallStarted,
    ToolCallCompleted,
    ItemActivity,
    TokenUsage,
    HistoryCompacted,
    PermissionRequested,
    PermissionResolved,
    InputRequested,
    InputResolved,
}

/// <summary>Structural tool/activity category observed in a durable record.</summary>
public enum ToolCallCategory
{
    None,
    Reasoning,
    Message,
    Plan,
    CommandExecution,
    FileChange,
    Search,
    McpToolCall,
    Extension,
    OpaqueWrapper,
}

public enum TurnOutcome
{
    Completed,
    Aborted,
    Failed,
}

/// <summary>
/// One normalized record. Source position identifies the record so duplicate
/// replay cannot produce duplicate transitions.
/// </summary>
public sealed record RawCodexEvent(
    RawCodexEventKind Kind,
    string ProfileId,
    string ConversationId,
    string TurnId,
    string? CallId,
    string? RequestId,
    ToolCallCategory Tool,
    AgentActionKind? Action,
    string? ActionDetailCode,
    bool? IsBlocking,
    TurnOutcome? Outcome,
    string? ReasonCode,
    DateTimeOffset ObservedAt,
    DateTimeOffset? EventTime,
    int SourceFileId,
    long SourceOffset,
    int SourceOrder,
    int SourceGeneration,
    bool IsHydration)
{
    public TaskKey Key => new(ProfileId, ConversationId);

    public static RawCodexEvent Unknown(string profileId, string conversationId, DateTimeOffset observedAt) =>
        new(RawCodexEventKind.Unknown, profileId, conversationId, string.Empty, null, null,
            ToolCallCategory.None, null, null, null, null, null, observedAt, null, 0, 0, 0, 0, false);
}
