using System.Globalization;
using System.Text.Json;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Bridge;

public enum BridgeParseOutcome
{
    Accepted,
    Malformed,
    UnsupportedSchema,
    UnknownKind,
    Ignored,
}

public sealed record BridgeParseResult(BridgeParseOutcome Outcome, HarnessBridgeRecord? Record, string ReasonCode)
{
    public static BridgeParseResult Reject(BridgeParseOutcome outcome, string reason) => new(outcome, null, reason);
}

/// <summary>
/// One normalized metadata record from an external harness bridge. The schema is
/// the frozen envelope shared by the dsh observer and the Claude hook bridge; only
/// these keys are read, and unknown keys are ignored. No prompt, transcript, tool
/// argument, tool output or credential can appear here.
/// </summary>
public sealed record HarnessBridgeRecord(
    int SchemaVersion,
    string HarnessId,
    string SourceInstanceId,
    string SourceEpoch,
    string SessionId,
    long Sequence,
    string EventId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? EventAtUtc,
    string Kind,
    string? TurnId,
    string? CallId,
    string? RequestId,
    string? ProjectRoot,
    string Category,
    string Evidence,
    string? Outcome,
    bool Hydration,
    string ProducerVersion)
{
    public TaskKey Key => TaskKey.For(HarnessId, SourceInstanceId, SessionId);
}

/// <summary>Bounded, allocation-light parser for the bridge envelope.</summary>
public static class HarnessBridgeParser
{
    public const int SchemaVersion = 1;
    public const int MaximumRecordChars = 64 * 1024;
    public const int MaximumIdentifierChars = 128;
    public const int MaximumProjectRootChars = 512;

    private static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
    {
        "session/start", "session/end", "turn/start", "turn/end", "tool/start", "tool/end",
        "approval/asked", "approval/decided", "input/asked", "input/answered", "plan/mode",
        "heartbeat", "snapshot",
    };

    public static IReadOnlyCollection<string> Kinds => KnownKinds;

    public static BridgeParseResult Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return BridgeParseResult.Reject(BridgeParseOutcome.Ignored, "empty");
        }

        if (line.Length > MaximumRecordChars)
        {
            return BridgeParseResult.Reject(BridgeParseOutcome.Malformed, "record-too-large");
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return BridgeParseResult.Reject(BridgeParseOutcome.Malformed, "not-an-object");
            }

            var schema = Int(root, "schemaVersion");
            if (schema is null)
            {
                return BridgeParseResult.Reject(BridgeParseOutcome.Malformed, "missing-schema");
            }

            if (schema != SchemaVersion)
            {
                return BridgeParseResult.Reject(BridgeParseOutcome.UnsupportedSchema, "unsupported-schema");
            }

            if (!ValidIdentifiers(root))
                return BridgeParseResult.Reject(BridgeParseOutcome.Malformed, "invalid-identity");

            var harnessId = HarnessIds.Normalize(Identifier(root, "harnessId"));
            var sourceInstanceId = Identifier(root, "sourceInstanceId");
            var sessionId = Identifier(root, "sessionId");
            if (harnessId.Length == 0 || sourceInstanceId.Length == 0 || sessionId.Length == 0)
            {
                return BridgeParseResult.Reject(BridgeParseOutcome.Malformed, "missing-identity");
            }

            var kind = Identifier(root, "kind");
            if (!KnownKinds.Contains(kind))
            {
                return BridgeParseResult.Reject(BridgeParseOutcome.UnknownKind, "unknown-kind");
            }

            var observed = Time(root, "observedAtUtc");
            if (observed is null)
            {
                return BridgeParseResult.Reject(BridgeParseOutcome.Malformed, "missing-observed-at");
            }

            var record = new HarnessBridgeRecord(
                schema.Value,
                harnessId,
                sourceInstanceId,
                Identifier(root, "sourceEpoch"),
                sessionId,
                Long(root, "sequence") ?? 0,
                Identifier(root, "eventId"),
                observed.Value,
                Time(root, "eventAtUtc"),
                kind,
                IdentifierOrNull(root, "turnId"),
                IdentifierOrNull(root, "callId"),
                IdentifierOrNull(root, "requestId"),
                Bounded(root, "projectRoot", MaximumProjectRootChars),
                Category(root),
                Evidence(root),
                IdentifierOrNull(root, "outcome"),
                Bool(root, "hydration"),
                Identifier(root, "producerVersion"));

            return new BridgeParseResult(BridgeParseOutcome.Accepted, record, "ok");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // JSON parsing alone does not validate escaped UTF-16 strings.
            return BridgeParseResult.Reject(BridgeParseOutcome.Malformed, "malformed-json");
        }
    }

    private static string Category(JsonElement root)
    {
        var value = Identifier(root, "category");
        return value.Length == 0 ? "unknown" : value;
    }

    private static string Evidence(JsonElement root)
    {
        var value = Identifier(root, "evidence");
        return value.Length == 0 ? "unknown" : value;
    }

    private static string Identifier(JsonElement root, string name) =>
        Bounded(root, name, MaximumIdentifierChars) ?? string.Empty;

    internal static bool ValidIdentifiers(JsonElement root)
    {
        foreach (var name in new[] { "harnessId", "sourceInstanceId", "sourceEpoch", "sessionId", "eventId",
            "turnId", "callId", "requestId", "producerVersion", "parentSessionId", "openTurnId",
            "pendingApprovalRequestId", "lastEventId" })
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String) return false;
            var text = value.GetString()!;
            if (text.Length > MaximumIdentifierChars || text != text.Trim() || text.Any(char.IsControl)) return false;
        }
        return true;
    }

    private static string? IdentifierOrNull(JsonElement root, string name) =>
        Bounded(root, name, MaximumIdentifierChars);

    private static string? Bounded(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length > maximum ? trimmed[..maximum] : trimmed;
    }

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static DateTimeOffset? Time(JsonElement root, string name)
    {
        var text = Bounded(root, name, 64);
        if (text is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
