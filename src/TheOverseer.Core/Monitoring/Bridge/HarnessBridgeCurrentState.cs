using System.Globalization;
using System.Text.Json;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Bridge;

public enum BridgeCurrentStateOutcome
{
    Accepted,
    Malformed,
    UnsupportedSchema,
    WrongSource,
    Ignored,
}

/// <summary>One session entry of a producer's authoritative current-state document.</summary>
public sealed record BridgeCurrentSessionState(
    string SessionId,
    string ParentSessionId,
    string ProjectRoot,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? FirstSeenUtc,
    DateTimeOffset? LastObservedUtc,
    string OpenTurnId,
    bool PlanMode,
    string PendingApprovalRequestId,
    string Category,
    bool Disposed,
    long LastSequence,
    string LastEventId,
    long Records)
{
    public IReadOnlyList<string> PendingInputRequestIds { get; init; } = Array.Empty<string>();
    public bool InputStateComplete { get; init; } = true;
}

/// <summary>
/// A producer's atomically published current-state document
/// (<c>current.json</c>). It is the authoritative resynchronization point for one
/// source epoch: whatever the consumer missed by attaching late, by a dropped
/// record or by a rotated event file is summarized here for the sessions the
/// producer currently knows about. No prompt, transcript, tool argument or
/// credential can appear in it; only bounded structural metadata is parsed.
/// </summary>
public sealed record HarnessBridgeCurrentState(
    int SchemaVersion,
    string HarnessId,
    string SourceInstanceId,
    string SourceEpoch,
    string ProducerVersion,
    DateTimeOffset UpdatedAtUtc,
    long EvictedSessions,
    IReadOnlyList<BridgeCurrentSessionState> Sessions)
{
    public BridgeCurrentStateParseResult Validate(SourceInstance source)
    {
        if (!string.Equals(HarnessId, source.HarnessId, StringComparison.Ordinal))
        {
            return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.WrongSource, null, "snapshot-harness-mismatch");
        }

        return !string.Equals(SourceInstanceId, source.SourceInstanceId, StringComparison.Ordinal)
            ? new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.WrongSource, null, "snapshot-instance-mismatch")
            : new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Accepted, this, "ok");
    }
}

public sealed record BridgeCurrentStateParseResult(
    BridgeCurrentStateOutcome Outcome,
    HarnessBridgeCurrentState? Document,
    string ReasonCode);

/// <summary>Bounded parser for the producer current-state document.</summary>
public static class HarnessBridgeCurrentStateParser
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Hard read bound. The shipped producer caps its own tracked sessions, so this
    /// is generous for a complete document and still bounded for a hostile one.
    /// </summary>
    public const int MaximumDocumentChars = 512 * 1024;
    public const int MaximumSessions = 4096;
    public const int MaximumIdentifierChars = 128;
    public const int MaximumProjectRootChars = 512;
    public const int MaximumPendingInputs = 32;

    public static BridgeCurrentStateParseResult Parse(string? json, SourceInstance source)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Ignored, null, "snapshot-empty");
        }

        if (json.Length > MaximumDocumentChars)
        {
            return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-too-large");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-malformed");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-not-an-object");
            }

            var schema = Int(root, "schemaVersion");
            if (schema is null)
            {
                return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-missing-schema");
            }

            if (schema != SchemaVersion)
            {
                return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.UnsupportedSchema, null, "snapshot-unsupported-schema");
            }

            var updated = Time(root, "updatedAtUtc");
            if (updated is null)
            {
                return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-missing-updated-at");
            }

            var harnessId = HarnessIds.Normalize(Identifier(root, "harnessId"));
            var sourceInstanceId = Identifier(root, "sourceInstanceId");
            if (harnessId.Length == 0 || sourceInstanceId.Length == 0)
            {
                return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-missing-identity");
            }

            var sessions = new List<BridgeCurrentSessionState>();
            if (root.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (sessions.Count >= MaximumSessions)
                    {
                        return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-too-many-sessions");
                    }

                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var sessionId = Identifier(entry, "sessionId");
                    if (sessionId.Length == 0)
                    {
                        continue;
                    }

                    var inputs = new List<string>();
                    if (entry.TryGetProperty("pendingInputRequestIds", out var pending))
                    {
                        if (pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() > MaximumPendingInputs)
                            return new(BridgeCurrentStateOutcome.Malformed, null, "snapshot-invalid-inputs");
                        foreach (var value in pending.EnumerateArray())
                        {
                            var id = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                            if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.Length > MaximumIdentifierChars || inputs.Contains(id, StringComparer.Ordinal))
                                return new(BridgeCurrentStateOutcome.Malformed, null, "snapshot-invalid-inputs");
                            inputs.Add(id);
                        }
                    }
                    if (entry.TryGetProperty("inputStateComplete", out var inputCoverage) && inputCoverage.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return new(BridgeCurrentStateOutcome.Malformed, null, "snapshot-invalid-input-coverage");

                    sessions.Add(new BridgeCurrentSessionState(
                        sessionId,
                        Identifier(entry, "parentSessionId"),
                        Bounded(entry, "projectRoot", MaximumProjectRootChars) ?? string.Empty,
                        Time(entry, "createdAtUtc"),
                        Time(entry, "firstSeenUtc"),
                        Time(entry, "lastObservedUtc"),
                        Identifier(entry, "openTurnId"),
                        Bool(entry, "planMode"),
                        Identifier(entry, "pendingApprovalRequestId"),
                        Identifier(entry, "category"),
                        Bool(entry, "disposed"),
                        Long(entry, "lastSequence") ?? -1,
                        Identifier(entry, "lastEventId"),
                        Long(entry, "records") ?? 0)
                    {
                        PendingInputRequestIds = inputs,
                        InputStateComplete = !entry.TryGetProperty("inputStateComplete", out _) || inputCoverage.GetBoolean(),
                    });
                }
            }

            var state = new HarnessBridgeCurrentState(
                schema.Value,
                harnessId,
                sourceInstanceId,
                Identifier(root, "sourceEpoch"),
                Identifier(root, "producerVersion"),
                updated.Value,
                Long(root, "evictedSessions") ?? 0,
                sessions);

            return state.Validate(source);
        }
    }

    private static string Identifier(JsonElement root, string name) =>
        Bounded(root, name, MaximumIdentifierChars) ?? string.Empty;

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

    private static long? Long(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.TryGetDouble(out var real) && !double.IsNaN(real) && !double.IsInfinity(real)
            ? (long)Math.Clamp(real, long.MinValue, long.MaxValue)
            : null;
    }

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
