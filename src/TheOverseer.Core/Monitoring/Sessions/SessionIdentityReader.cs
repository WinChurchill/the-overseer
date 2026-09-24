using System.Text;
using System.Text.Json;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Sessions;

/// <summary>
/// Identity resolved from a rollout header. Only structural fields are read;
/// instructions, prompts and message text are never touched.
/// </summary>
public sealed record SessionIdentity(
    bool Found,
    string ConversationId,
    string WorkspaceLeaf,
    string WorkspaceRoot,
    ClientSurface Surface,
    string ThreadSourceCode,
    string HistoryModeCode,
    string CliVersionCode,
    bool IsExplicitInternal,
    bool HasParentThread,
    bool HasHistoryBase,
    string DiagnosticCode)
{
    public static SessionIdentity Empty(string diagnostic) => new(
        false, string.Empty, string.Empty, string.Empty, ClientSurface.Unknown, string.Empty, string.Empty, string.Empty,
        false, false, false, diagnostic);
}

/// <summary>
/// Byte-bounded rollout header reader. Adapted from codex-monitor-hud
/// src-dotnet/CodexMonitorHud.Core/Sessions/SessionIdentityReader.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT). The unbounded
/// StreamReader.ReadLine loop was replaced by a capped byte read, and
/// thread_source/parent_thread_id/history_mode are honored.
/// </summary>
public static class SessionIdentityReader
{
    private static readonly HashSet<string> InternalThreadSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "subagent",
        "guardian_review",
        "agent_created_thread",
        "chatgpt_handoff",
    };

    public static SessionIdentity Read(string path, int maxBytes)
    {
        var cap = Math.Max(4096, maxBytes);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            var buffer = new byte[Math.Min(cap, 64 * 1024)];
            var builder = new StringBuilder(Math.Min(cap, 64 * 1024));
            var consumed = 0;
            var reachedEnd = false;
            while (consumed < cap)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, cap - consumed));
                if (read <= 0)
                {
                    reachedEnd = true;
                    break;
                }

                consumed += read;
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var text = builder.ToString();
                var searchFrom = 0;
                while (true)
                {
                    var newline = text.IndexOf('\n', searchFrom);
                    if (newline < 0)
                    {
                        break;
                    }

                    var identity = ParseLine(text[searchFrom..newline].Trim());
                    if (identity.Found)
                    {
                        return identity;
                    }

                    searchFrom = newline + 1;
                }

                // Keep only the incomplete trailing record for the next chunk.
                builder.Clear();
                if (searchFrom < text.Length)
                {
                    builder.Append(text[searchFrom..]);
                }

                if (builder.Length >= cap)
                {
                    break;
                }
            }

            if (reachedEnd && builder.Length > 0)
            {
                var identity = ParseLine(builder.ToString().Trim());
                if (identity.Found)
                {
                    return identity;
                }
            }
        }
        catch (IOException)
        {
            return SessionIdentity.Empty("io-error");
        }
        catch (UnauthorizedAccessException)
        {
            return SessionIdentity.Empty("access-denied");
        }

        return SessionIdentity.Empty("session-meta-missing");
    }

    public static SessionIdentity ParseLine(string line)
    {
        if (line.Length == 0 || !line.Contains("\"session_meta\"", StringComparison.Ordinal))
        {
            return SessionIdentity.Empty("not-session-meta");
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal) ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return SessionIdentity.Empty("not-session-meta");
            }

            var id = GetString(payload, "id", 128);
            if (id.Length == 0)
            {
                id = GetString(payload, "session_id", 128);
            }

            var originator = GetString(payload, "originator", 64);
            var sourceName = string.Empty;
            var subagentSource = false;
            if (payload.TryGetProperty("source", out var source))
            {
                if (source.ValueKind == JsonValueKind.String)
                {
                    sourceName = Sanitize(source.GetString(), 64);
                }
                else if (source.ValueKind == JsonValueKind.Object)
                {
                    subagentSource = source.TryGetProperty("subagent", out _);
                }
            }

            var threadSource = Sanitize(GetString(payload, "thread_source", 64), 64);
            var historyMode = Sanitize(GetString(payload, "history_mode", 32), 32);
            var cliVersion = Sanitize(GetString(payload, "cli_version", 32), 32);
            // The workspace root is retained locally for project attribution; the
            // leaf stays the display fallback. Roots never reach logs, voice or
            // exports, and normalization rejects relative/device paths.
            var workspace = GetString(payload, "cwd", 512);
            var workspaceLeaf = PortableLeaf(workspace);
            var workspaceRoot = ProjectResolver.Normalize(workspace);
            var isInternal = subagentSource ||
                             (threadSource.Length > 0 && InternalThreadSources.Contains(threadSource));

            return new SessionIdentity(
                id.Length > 0,
                id,
                workspaceLeaf,
                workspaceRoot,
                GetClientSurface(originator, sourceName),
                threadSource,
                historyMode,
                cliVersion,
                isInternal,
                payload.TryGetProperty("parent_thread_id", out var parent) && parent.ValueKind == JsonValueKind.String,
                payload.TryGetProperty("history_base", out var historyBase) && historyBase.ValueKind == JsonValueKind.Object,
                id.Length > 0 ? "ok" : "missing-id");
        }
        catch (JsonException)
        {
            return SessionIdentity.Empty("malformed-header");
        }
        catch (InvalidOperationException)
        {
            return SessionIdentity.Empty("malformed-header");
        }
    }

    /// <summary>
    /// Filename-derived provisional identity. Rollout file names embed the
    /// conversation id as their first UUID; this is only used when the header
    /// could not be read, and the task is marked provisional.
    /// </summary>
    public static string TryDeriveConversationIdFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        for (var index = 0; index + 36 <= name.Length; index++)
        {
            if (IsUuid(name.AsSpan(index, 36)))
            {
                return name.Substring(index, 36).ToLowerInvariant();
            }
        }

        return string.Empty;
    }

    private static bool IsUuid(ReadOnlySpan<char> span)
    {
        for (var index = 0; index < span.Length; index++)
        {
            var character = span[index];
            if (index is 8 or 13 or 18 or 23)
            {
                if (character != '-')
                {
                    return false;
                }

                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    // The VS Code extension writes source=vscode for both surfaces, so the
    // distinct codex_vscode originator is checked first.
    private static ClientSurface GetClientSurface(string originator, string source)
    {
        if (originator.Equals("codex_vscode", StringComparison.OrdinalIgnoreCase) ||
            originator.Equals("Codex VS Code", StringComparison.OrdinalIgnoreCase))
        {
            return ClientSurface.VsCode;
        }

        if (originator.Equals("Codex Desktop", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("vscode", StringComparison.OrdinalIgnoreCase))
        {
            return ClientSurface.Desktop;
        }

        if (originator.Contains("codex-tui", StringComparison.OrdinalIgnoreCase) ||
            originator.Contains("codex_cli", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("cli", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("exec", StringComparison.OrdinalIgnoreCase))
        {
            return ClientSurface.Cli;
        }

        return ClientSurface.Unknown;
    }

    private static string GetString(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return Sanitize(node.GetString(), maximumLength);
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }

    private static string PortableLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('\\', '/');
        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var leaf = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        return leaf.Length <= 64 ? leaf : leaf[..64];
    }
}
