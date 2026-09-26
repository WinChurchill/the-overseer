namespace TheOverseer.Core.Models;

/// <summary>
/// One source's share of the aggregate monitoring budget. The aggregate is
/// deliberately NOT multiplied by the number of sources: a machine running three
/// harnesses must not read three times as much per pass. Each source receives an
/// equal share of the configured aggregate, without inflating small allowances.
/// </summary>
public sealed record SourceBudget(int BytesPerPass, int MaximumSessions, string Code)
{
    /// <summary>Aggregate byte allowance per monitoring pass across every source.</summary>
    public const int DefaultAggregateBytesPerPass = 4 * 1024 * 1024;

    /// <summary>Aggregate tracked-session allowance shared by every bridge source.</summary>
    public const int DefaultAggregateSessions = 1024;

    /// <summary>Default lower bound for the aggregate, before sharing.</summary>
    public const int MinimumBytesPerPass = 64 * 1024;

    /// <summary>Default lower bound for aggregate session capacity, before sharing.</summary>
    public const int MinimumSessions = 32;

    /// <summary>Most source instances the composite will monitor at once.</summary>
    public const int MaximumSourceInstances = 8;

    /// <summary>The aggregate allowance before it is divided between sources.</summary>
    public static SourceBudget Default { get; } = new(DefaultAggregateBytesPerPass, DefaultAggregateSessions, "aggregate");

    /// <summary>
    /// Fair share of one aggregate for <paramref name="sourceCount"/> sources.
    /// The result never exceeds the aggregate, so adding a source cannot raise the
    /// total work the machine performs per pass.
    /// </summary>
    public static SourceBudget Share(int aggregateBytesPerPass, int aggregateSessions, int sourceCount)
    {
        var count = Math.Clamp(sourceCount, 1, MaximumSourceInstances);
        ArgumentOutOfRangeException.ThrowIfLessThan(aggregateBytesPerPass, count);
        ArgumentOutOfRangeException.ThrowIfLessThan(aggregateSessions, count);
        var bytes = aggregateBytesPerPass / count;
        var sessions = Math.Min(aggregateSessions / count, DefaultAggregateSessions);
        return new SourceBudget(bytes, sessions, "share-1/" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public override string ToString() => Code;
}

/// <summary>
/// Implemented by adapters whose read work is bounded by the shared composite
/// budget rather than only by their own constants. The composite applies the share
/// before the adapter starts, so a budget change cannot race the reader.
/// </summary>
public interface IBudgetedMonitorAdapter
{
    void ApplyBudget(SourceBudget budget);
}
