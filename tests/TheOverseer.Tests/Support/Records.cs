using System.Text.Json;

namespace TheOverseer.Tests.Support;

/// <summary>
/// Synthetic rollout record builders. Only structural fields the parser is
/// allowed to read are emitted; no real conversation content is used anywhere.
/// </summary>
public static class Records
{
    public const long BaseSeconds = 1_767_323_045; // 2026-01-02T03:04:05Z

    public static string SessionMeta(
        string conversationId,
        string cwd = @"C:\work\demo",
        string originator = "Codex Desktop",
        object? source = null,
        string? threadSource = "user",
        string? parentThreadId = null,
        string historyMode = "paginated",
        string cliVersion = "0.155.1",
        bool subagent = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = conversationId,
            ["session_id"] = conversationId,
            ["timestamp"] = "2026-01-02T03:04:05Z",
            ["cwd"] = cwd,
            ["originator"] = originator,
            ["source"] = source ?? (subagent ? new { subagent = new { other = "guardian" } } : "vscode"),
            ["history_mode"] = historyMode,
            ["cli_version"] = cliVersion,
            ["model_provider"] = "openai",
        };
        if (threadSource is not null)
        {
            payload["thread_source"] = threadSource;
        }

        if (parentThreadId is not null)
        {
            payload["parent_thread_id"] = parentThreadId;
        }

        return JsonSerializer.Serialize(new { type = "session_meta", payload });
    }

    public static string TaskStarted(string turnId, long? startedAtSeconds = null) =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "task_started",
                turn_id = turnId,
                started_at = startedAtSeconds ?? BaseSeconds,
                collaboration_mode_kind = "default",
            },
        });

    public static string TaskComplete(string turnId, long? completedAtSeconds = null, string? lastAgentMessage = "synthetic") =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "task_complete",
                turn_id = turnId,
                last_agent_message = lastAgentMessage,
                started_at = completedAtSeconds ?? BaseSeconds,
                completed_at = completedAtSeconds ?? BaseSeconds,
                duration_ms = 1000,
            },
        });

    public static string TurnAborted(string turnId, long? completedAtSeconds = null, string reason = "interrupted") =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "turn_aborted",
                turn_id = turnId,
                reason,
                started_at = completedAtSeconds ?? BaseSeconds,
                completed_at = completedAtSeconds ?? BaseSeconds,
                duration_ms = 500,
            },
        });

    public static string TaskFailed(string turnId, string reason = "stream_error") =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "stream_error",
                turn_id = turnId,
                reason,
                started_at = BaseSeconds,
            },
        });

    public static string ItemCompleted(
        string turnId,
        string itemType,
        string? itemId = null,
        long? startedAtMs = null,
        long? completedAtMs = null,
        string? command = null,
        string? parsedCommandType = null,
        string? status = null,
        string? mcpServer = null,
        string? mcpTool = null,
        string? extensionKind = null)
    {
        var item = new Dictionary<string, object?>
        {
            ["type"] = itemType,
            ["id"] = itemId ?? "item-1",
        };

        switch (itemType)
        {
            case "CommandExecution":
                item["command"] = command ?? "dotnet build";
                item["parsed_cmd"] = new[] { new { type = parsedCommandType ?? "unknown", cmd = command ?? "dotnet build" } };
                item["status"] = status ?? "completed";
                item["exit_code"] = 0;
                item["source"] = "unified_exec_startup";
                break;
            case "FileChange":
                item["status"] = status ?? "completed";
                item["changes"] = new[] { new { path = "src/file.cs", kind = "update" } };
                break;
            case "McpToolCall":
                item["server"] = mcpServer ?? "codex_app";
                item["tool"] = mcpTool ?? "search";
                item["status"] = status ?? "completed";
                break;
            case "Extension":
                item["kind"] = extensionKind ?? "web.search";
                item["query"] = "synthetic";
                item["action"] = "search";
                break;
            case "Reasoning":
                item["summary_text"] = "synthetic reasoning";
                break;
            case "AgentMessage":
                item["content"] = "synthetic agent message";
                break;
            case "UserMessage":
                item["content"] = "synthetic user message";
                break;
        }

        return JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "item_completed",
                thread_id = "thread",
                turn_id = turnId,
                item,
                started_at_ms = startedAtMs ?? 1_767_323_045_000,
                completed_at_ms = completedAtMs ?? 1_767_323_046_000,
            },
        });
    }

    public static string CustomToolCall(string turnId, string callId, string name = "exec", string status = "completed") =>
        JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "custom_tool_call",
                id = callId,
                status,
                call_id = callId,
                name,
                input = "REMOVED-IN-TEST",
                internal_chat_message_metadata_passthrough = new { turn_id = turnId },
            },
        });

    public static string CustomToolCallOutput(string turnId, string callId, string output = "REMOVED-IN-TEST") =>
        JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "custom_tool_call_output",
                id = callId,
                call_id = callId,
                output,
                internal_chat_message_metadata_passthrough = new { turn_id = turnId },
            },
        });

    public static string Reasoning(string turnId) =>
        JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "reasoning",
                id = "reasoning-1",
                summary = Array.Empty<object>(),
                encrypted_content = "REMOVED-IN-TEST",
                internal_chat_message_metadata_passthrough = new { turn_id = turnId },
            },
        });

    public static string TokenCount() =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new { total_token_usage = new { total_tokens = 1234 } },
            },
        });

    public static string PermissionRequest(string turnId, string requestId = "req-1") =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "exec_approval_request",
                turn_id = turnId,
                call_id = requestId,
                request_id = requestId,
                command = "REMOVED-IN-TEST",
            },
        });

    public static string InputRequest(string turnId, bool isBlocking, string requestId = "input-1") =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "request_user_input",
                turn_id = turnId,
                call_id = requestId,
                request_id = requestId,
                isBlocking,
                question = "REMOVED-IN-TEST",
            },
        });

    public static string TurnContext(string turnId) =>
        JsonSerializer.Serialize(new
        {
            type = "turn_context",
            payload = new
            {
                turn_id = turnId,
                cwd = @"C:\work\demo",
                approval_policy = "on-request",
                sandbox_policy = new { type = "workspace-write" },
                model = "synthetic-model",
            },
        });

    public static string Malformed() => "{\"type\": \"event_msg\", \"payload\": {";
}
