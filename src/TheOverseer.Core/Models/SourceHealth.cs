namespace TheOverseer.Core.Models;

/// <summary>
/// One reporting source's own health, evidence and capability set, carried inside a
/// normalized snapshot. It exists so a consumer can render source-specific detail
/// without parsing free-text reason notes and without querying the monitor: the
/// composite already knows this per source, and a healthy peer must never be able to
/// renew another source's displayed freshness.
///
/// Only structural identity, stable reason codes, timestamps and the existing typed
/// capability records are carried. No path, prompt, transcript, tool argument or
/// credential can appear here.
/// </summary>
public sealed record SourceHealth(
    string HarnessId,
    string SourceInstanceId,
    string DisplayName,
    MonitoringHealthLevel Level,
    IReadOnlyList<string> Reasons,
    bool CoverageComplete,
    DateTimeOffset? EvidenceAt,
    MonitorCapabilities Capabilities)
{
    /// <summary>True when this source produced an observation at all.</summary>
    public bool IsReadable => Level != MonitoringHealthLevel.Unavailable;

    /// <summary>Stable "harness/instance" identity, matching the settings key.</summary>
    public string Key => HarnessId + "/" + SourceInstanceId;

    /// <summary>Safe diagnostic label: identity and status codes only, never a path.</summary>
    public string Describe() =>
        Key + " " + Level + (CoverageComplete ? "" : " partial") +
        (Reasons.Count == 0 ? string.Empty : " [" + string.Join(',', Reasons) + "]");

    /// <summary>
    /// Uses this source's own evidence or observation time. A source that has never
    /// published has unknown freshness; the composite's clock is never substituted.
    /// </summary>
    public static SourceHealth From(SourceSnapshot snapshot) => new(
        snapshot.Source.HarnessId,
        snapshot.Source.SourceInstanceId,
        snapshot.Source.DisplayName,
        snapshot.Health,
        snapshot.HealthReasons,
        snapshot.CoverageComplete,
        snapshot.EffectiveEvidenceAt == default ? null : snapshot.EffectiveEvidenceAt,
        snapshot.Capabilities);
}
