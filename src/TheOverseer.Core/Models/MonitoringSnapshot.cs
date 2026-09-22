namespace TheOverseer.Core.Models;

/// <summary>
/// A bounded count. <see cref="IsKnown"/> is false when the monitor cannot
/// honestly produce a number; <see cref="IsComplete"/> is false when the number
/// is a lower bound because coverage is partial.
/// </summary>
public readonly record struct CountValue(bool IsKnown, bool IsComplete, int Value)
{
    public static CountValue Unknown { get; } = new(false, false, 0);

    public static CountValue Known(int value) => new(true, true, value);

    public static CountValue Partial(int knownAtLeast) => new(true, false, knownAtLeast);

    public string ToDisplay(string noun) =>
        !IsKnown ? noun + " unknown"
        : IsComplete ? Value + " " + noun
        : Value + " " + noun + " (partial)";
}

public sealed record MonitoringCounts(
    CountValue Working,
    CountValue NeedsHuman,
    CountValue NeedsApproval,
    CountValue NeedsInput,
    CountValue Error,
    CountValue Done,
    CountValue Idle,
    CountValue Total)
{
    public static MonitoringCounts Unknown { get; } = new(
        CountValue.Unknown, CountValue.Unknown, CountValue.Unknown, CountValue.Unknown,
        CountValue.Unknown, CountValue.Unknown, CountValue.Unknown, CountValue.Unknown);

    /// <summary>Summary line used by the debug harness and later by the HUD.</summary>
    public string ToSummary() => Working.ToDisplay("working") + " \u00b7 " + NeedsHuman.ToDisplay("needs you");
}

/// <summary>
/// Immutable normalized monitoring output. The UI, history and voice layers are
/// later plans and only consume this record.
/// </summary>
public sealed record MonitoringSnapshot(
    DateTimeOffset GeneratedAt,
    MonitoringHealth Health,
    MonitoringCounts Counts,
    IReadOnlyList<AgentTask> Tasks,
    IReadOnlyList<AttentionEvent> Transitions,
    MonitorCapabilities Capabilities,
    LiveObservationStats Observation)
{
    public static MonitoringSnapshot Empty(DateTimeOffset now) => new(
        now,
        MonitoringHealth.Unavailable(new[] { HealthReasons.RootMissing }, now),
        MonitoringCounts.Unknown,
        Array.Empty<AgentTask>(),
        Array.Empty<AttentionEvent>(),
        MonitorCapabilities.Initial,
        LiveObservationStats.Empty);
}
