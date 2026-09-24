namespace TheOverseer.Core.Models;

/// <summary>
/// Stable harness identifiers. A harness is the agent runtime that produced a
/// session; it is not a model provider and not a source profile. DeepSeek used
/// as a model inside Codex is still Codex.
/// </summary>
public static class HarnessIds
{
    public const string Codex = "codex";
    public const string Dsh = "dsh";
    public const string Claude = "claude";

    public static IReadOnlyList<string> All { get; } = new[] { Codex, Dsh, Claude };

    public static string Normalize(string? harnessId)
    {
        if (string.IsNullOrWhiteSpace(harnessId))
        {
            return Codex;
        }

        var trimmed = harnessId.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }

    public static string DisplayName(string harnessId) => Normalize(harnessId) switch
    {
        Codex => "Codex",
        Dsh => "DeepSeek Harness",
        Claude => "Claude Code",
        var other => other,
    };
}

/// <summary>
/// One configured, enabled or disabled observation source: a harness plus the
/// stable instance id that owns a group of sessions. Ids survive configuration
/// reordering, so history, dedupe and voice stay stable.
/// </summary>
public sealed record SourceInstance(
    string HarnessId,
    string SourceInstanceId,
    string DisplayName,
    bool IsEnabled = true,
    bool IsOptional = false)
{
    public string HarnessDisplayName => HarnessIds.DisplayName(HarnessId);

    /// <summary>Safe diagnostic label: no paths, no session content.</summary>
    public string Describe() => HarnessId + ":" + SourceInstanceId + (IsEnabled ? "" : " (disabled)");
}

/// <summary>
/// Result of one adapter pass plus the coverage the adapter can honestly claim.
/// A source-local failure is reported here and never rewritten as zero work.
/// </summary>
public sealed record SourceSnapshot(
    SourceInstance Source,
    DateTimeOffset ObservedAt,
    MonitoringHealthLevel Health,
    IReadOnlyList<string> HealthReasons,
    bool CoverageComplete,
    IReadOnlyList<AgentTask> Tasks,
    IReadOnlyList<AttentionEvent> Transitions,
    MonitorCapabilities Capabilities,
    string AdapterCode)
{
    public bool IsEnabled => Source.IsEnabled;

    public static SourceSnapshot Empty(SourceInstance source, DateTimeOffset now, string reason, string adapterCode) => new(
        source,
        now,
        MonitoringHealthLevel.Unavailable,
        new[] { reason },
        false,
        Array.Empty<AgentTask>(),
        Array.Empty<AttentionEvent>(),
        MonitorCapabilities.Initial,
        adapterCode);
}
