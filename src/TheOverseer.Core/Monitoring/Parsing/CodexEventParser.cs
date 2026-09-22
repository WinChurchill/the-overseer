using System.Globalization;
using System.Text.Json;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Parsing;

public enum ParseOutcome
{
    Parsed,
    Ignored,
    Malformed,
    Oversize,
    UnknownSchema,
}

public readonly record struct ParseContext(
    string ProfileId,
    string ConversationId,
    int SourceFileId,
    long SourceOffset,
    int SourceOrder,
    int SourceGeneration,
    bool IsHydration,
    DateTimeOffset ObservedAt,
    int MaxRecordChars);

public readonly record struct ParseResult(RawCodexEvent? Event, ParseOutcome Outcome);

/// <summary>
/// Structural rollout record parser. Records are treated as data, never as
/// instructions: message text, reasoning text, prompts, tool arguments, tool
/// output and command lines are never retained. Only structural fields
/// (record type, turn/call/request identity, item category, status) become
/// normalized events, plus a transient narrow command category.
/// </summary>
public static class CodexEventParser
{
    private const int MaximumIdentifierChars = 128;

    private static readonly HashSet<string> FailureReasonCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "failure", "error", "stream_error", "internal_error",
    };

    public static ParseResult ParseLine(string line, in ParseContext context)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new ParseResult(null, ParseOutcome.Ignored);
        }

        if (line.Length > context.MaxRecordChars)
        {
            return new ParseResult(null, ParseOutcome.Oversize);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return new ParseResult(null, ParseOutcome.Malformed);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeNode) ||
                typeNode.ValueKind != JsonValueKind.String)
            {
                return new ParseResult(null, ParseOutcome.UnknownSchema);
            }

            var recordType = typeNode.GetString() ?? string.Empty;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return new ParseResult(null, ParseOutcome.Ignored);
            }

            return recordType switch
            {
                "session_meta" => ParseSessionMeta(payload, in context),
                "turn_context" => ParseTurnContext(payload, in context),
                "event_msg" => ParseEventMessage(payload, in context),
                "response_item" => ParseResponseItem(payload, in context),
                "token_usage_record" => Build(RawCodexEventKind.TokenUsage, in context, payload),
                "compacted" => Build(RawCodexEventKind.HistoryCompacted, in context, payload),
                _ => new ParseResult(null, ParseOutcome.UnknownSchema),
            };
        }
    }

    private static ParseResult ParseSessionMeta(JsonElement payload, in ParseContext context) =>
        Build(RawCodexEventKind.SessionIdentity, in context, payload);

    private static ParseResult ParseTurnContext(JsonElement payload, in ParseContext context) =>
        Build(RawCodexEventKind.TurnContext, in context, payload);

    private static ParseResult ParseEventMessage(JsonElement payload, in ParseContext context)
    {
        var kindText = GetString(payload, "type", 64);
        return kindText switch
        {
            "task_started" => Build(RawCodexEventKind.TaskStarted, in context, payload),
            "task_complete" => Build(RawCodexEventKind.TaskCompleted, in context, payload),
            "turn_aborted" => Build(RawCodexEventKind.TurnAborted, in context, payload),
            "error" or "stream_error" or "task_failed" => Build(RawCodexEventKind.TaskFailed, in context, payload),
            "item_completed" => ParseItemCompleted(payload, in context),
            "token_count" => Build(RawCodexEventKind.TokenUsage, in context, payload),
            "thread_settings_applied" => new ParseResult(null, ParseOutcome.Ignored),
            "user_message" => Build(RawCodexEventKind.ItemActivity, in context, payload, ToolCallCategory.Message, null),
            "agent_message" => Build(RawCodexEventKind.ItemActivity, in context, payload, ToolCallCategory.Message, AgentActionKind.Thinking),
            "agent_reasoning" => Build(RawCodexEventKind.ItemActivity, in context, payload, ToolCallCategory.Reasoning, AgentActionKind.Thinking),
            "request_user_input" => BuildInputRequest(payload, in context),
            "exec_approval_request" or "apply_patch_approval_request" or "request_permissions"
                => BuildPermissionRequest(payload, in context),
            _ => new ParseResult(null, ParseOutcome.UnknownSchema),
        };
    }

    private static ParseResult ParseItemCompleted(JsonElement payload, in ParseContext context)
    {
        if (!payload.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return new ParseResult(null, ParseOutcome.Malformed);
        }

        var itemType = GetString(item, "type", 64);
        return itemType switch
        {
            "CommandExecution" => BuildCommandExecution(payload, item, in context),
            "FileChange" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.FileChange, AgentActionKind.EditingFiles, "file-change"),
            "McpToolCall" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.McpToolCall, ClassifyMcp(item)),
            "Extension" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.Extension, ClassifyExtension(item)),
            "Reasoning" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.Reasoning, AgentActionKind.Thinking, "reasoning"),
            "Plan" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.Plan, AgentActionKind.Thinking, "plan"),
            "AgentMessage" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.Message, AgentActionKind.Thinking, "agent-message"),
            "UserMessage" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.Message, null),
            "ContextCompaction" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.None, AgentActionKind.Thinking, "context-compaction"),
            _ => new ParseResult(null, ParseOutcome.UnknownSchema),
        };
    }

    private static ParseResult ParseResponseItem(JsonElement payload, in ParseContext context)
    {
        var kindText = GetString(payload, "type", 64);
        return kindText switch
        {
            "function_call" or "custom_tool_call" => BuildToolCall(payload, in context),
            "function_call_output" or "custom_tool_call_output" => BuildToolCompletion(payload, in context),
            "reasoning" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.Reasoning, AgentActionKind.Thinking, "reasoning"),
            "message" => Build(RawCodexEventKind.ItemActivity, in context, payload,
                ToolCallCategory.Message, AgentActionKind.Thinking, "message"),
            "compaction" => Build(RawCodexEventKind.HistoryCompacted, in context, payload),
            _ => new ParseResult(null, ParseOutcome.UnknownSchema),
        };
    }

    private static ParseResult BuildToolCall(JsonElement payload, in ParseContext context)
    {
        var name = NormalizeToolName(GetString(payload, "name", 64));
        var status = GetString(payload, "status", 32);
        var completed = status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("failed", StringComparison.OrdinalIgnoreCase);

        // An opaque wrapper's payload is never evaluated or searched. Known
        // wrapper names map to the generic command label; anything else stays
        // unclassified so the classifier can fall back to turn state.
        var isCommandWrapper = name is "exec" or "shell" or "localshell" or "containerexec";
        return Build(
            completed ? RawCodexEventKind.ToolCallCompleted : RawCodexEventKind.ToolCallStarted,
            in context,
            payload,
            ToolCallCategory.OpaqueWrapper,
            isCommandWrapper ? AgentActionKind.RunningCommand : null,
            "tool-" + (name.Length == 0 ? "unknown" : name));
    }

    private static ParseResult BuildToolCompletion(JsonElement payload, in ParseContext context) =>
        Build(RawCodexEventKind.ToolCallCompleted, in context, payload, ToolCallCategory.OpaqueWrapper, null, "tool-output");

    private static ParseResult BuildPermissionRequest(JsonElement payload, in ParseContext context) =>
        Build(RawCodexEventKind.PermissionRequested, in context, payload);

    private static ParseResult BuildInputRequest(JsonElement payload, in ParseContext context)
    {
        var blocking = true;
        if (payload.TryGetProperty("isBlocking", out var blockingNode))
        {
            blocking = blockingNode.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => true,
            };
        }

        // Only the blocking flag and correlation identity are retained; the
        // question text is never read.
        return Build(RawCodexEventKind.InputRequested, in context, payload, isBlocking: blocking);
    }

    private static ParseResult BuildCommandExecution(JsonElement payload, JsonElement item, in ParseContext context)
    {
        // The command text is transient: inspected for a narrow category and
        // then dropped. Never retained, logged or exported.
        var signal = ClassifyParsedCommand(item);
        return Build(
            RawCodexEventKind.ItemActivity,
            in context,
            payload,
            ToolCallCategory.CommandExecution,
            signal?.Action ?? AgentActionKind.RunningCommand,
            signal?.DetailCode ?? "opaque-command");
    }

    private static CommandSignal? ClassifyParsedCommand(JsonElement item)
    {
        if (item.TryGetProperty("parsed_cmd", out var parsed) && parsed.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in parsed.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("cmd", out var cmdNode) &&
                    cmdNode.ValueKind == JsonValueKind.String)
                {
                    var signal = CommandClassifier.Classify(cmdNode.GetString());
                    if (signal is not null)
                    {
                        return signal;
                    }
                }
            }
        }

        if (item.TryGetProperty("command", out var commandNode) && commandNode.ValueKind == JsonValueKind.String)
        {
            return CommandClassifier.Classify(commandNode.GetString());
        }

        return null;
    }

    private static AgentActionKind ClassifyMcp(JsonElement item)
    {
        var tool = GetString(item, "tool", 64);
        var server = GetString(item, "server", 64);
        if (ContainsToken(tool, "search") || ContainsToken(tool, "web") || ContainsToken(tool, "fetch") ||
            ContainsToken(server, "search"))
        {
            return AgentActionKind.Searching;
        }

        return AgentActionKind.Thinking;
    }

    private static AgentActionKind ClassifyExtension(JsonElement item)
    {
        var kind = GetString(item, "kind", 64);
        return kind.StartsWith("web.", StringComparison.OrdinalIgnoreCase)
            ? AgentActionKind.Searching
            : AgentActionKind.Thinking;
    }

    private static bool ContainsToken(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static ParseResult Build(
        RawCodexEventKind kind,
        in ParseContext context,
        JsonElement payload,
        ToolCallCategory tool = ToolCallCategory.None,
        AgentActionKind? action = null,
        string? detailCode = null,
        bool? isBlocking = null)
    {
        var turnId = GetString(payload, "turn_id", MaximumIdentifierChars);
        var callId = GetString(payload, "call_id", MaximumIdentifierChars);
        var requestId = kind is RawCodexEventKind.PermissionRequested or RawCodexEventKind.InputRequested
            ? FirstNonEmpty(GetString(payload, "request_id", MaximumIdentifierChars), callId, GetString(payload, "id", MaximumIdentifierChars))
            : null;

        var eventTime = ReadTimestamp(payload, "timestamp") ??
                        ReadTimestamp(payload, "completed_at") ??
                        ReadTimestamp(payload, "started_at") ??
                        ReadTimestamp(payload, "completed_at_ms") ??
                        ReadTimestamp(payload, "started_at_ms");

        var outcome = kind switch
        {
            RawCodexEventKind.TaskCompleted => TurnOutcome.Completed,
            RawCodexEventKind.TurnAborted => TurnOutcome.Aborted,
            RawCodexEventKind.TaskFailed => TurnOutcome.Failed,
            _ => (TurnOutcome?)null,
        };

        var reasonCode = kind switch
        {
            RawCodexEventKind.TurnAborted => NormalizeReason(GetString(payload, "reason", 64)),
            RawCodexEventKind.TaskFailed => NormalizeReason(GetString(payload, "reason", 64)),
            _ => null,
        };

        if (kind == RawCodexEventKind.TaskCompleted)
        {
            // An explicit error object on a completion record is terminal failure
            // evidence, not a successful completion.
            if (payload.TryGetProperty("error", out var errorNode) && errorNode.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                kind = RawCodexEventKind.TaskFailed;
                outcome = TurnOutcome.Failed;
                reasonCode = "completion-error";
            }
        }

        var evt = new RawCodexEvent(
            kind,
            context.ProfileId,
            context.ConversationId,
            turnId,
            callId.Length == 0 ? null : callId,
            requestId,
            tool,
            action,
            detailCode,
            isBlocking,
            outcome,
            reasonCode,
            context.ObservedAt,
            eventTime,
            context.SourceFileId,
            context.SourceOffset,
            context.SourceOrder,
            context.SourceGeneration,
            context.IsHydration);

        return new ParseResult(evt, ParseOutcome.Parsed);
    }

    private static string NormalizeReason(string reason)
    {
        if (reason.Length == 0)
        {
            return "unknown";
        }

        return FailureReasonCodes.Contains(reason) ? "failed" : "cancelled";
    }

    private static string NormalizeToolName(string name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null!;
    }

    private static string GetString(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var node))
        {
            return string.Empty;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            var value = node.GetString() ?? string.Empty;
            return value.Length <= maximumLength ? value : value[..maximumLength];
        }

        if (node.ValueKind is JsonValueKind.Number && node.TryGetInt64(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return string.Empty;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var node))
        {
            return null;
        }

        try
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!node.TryGetInt64(out var raw))
                    {
                        return null;
                    }

                    // Rollout lifecycle records use seconds; *_at_ms uses
                    // milliseconds. Anything beyond year ~5138 seconds is ms.
                    return raw >= 100_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                        : DateTimeOffset.FromUnixTimeSeconds(raw);
                case JsonValueKind.String:
                    var text = node.GetString();
                    return DateTimeOffset.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var parsed)
                        ? parsed
                        : null;
                default:
                    return null;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
