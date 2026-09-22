namespace TheOverseer.Core.Monitoring;

/// <summary>
/// Non-content activity hint read from an optional local source.
/// It can locate a candidate and indicate recency. It is never execution state
/// and can never revive a terminal state or clear attention.
/// </summary>
public sealed record RuntimeActivityHint(string ProfileId, string ConversationId, string RolloutPath, DateTimeOffset UpdatedAt);

public enum RuntimeActivityAvailability
{
    Unavailable,
    Available,
    Failed,
    NotConfigured,
}

public sealed record RuntimeActivityResult(
    RuntimeActivityAvailability Availability,
    IReadOnlyList<RuntimeActivityHint> Hints,
    string ReasonCode)
{
    public static RuntimeActivityResult Unavailable(string reason) =>
        new(RuntimeActivityAvailability.Unavailable, Array.Empty<RuntimeActivityHint>(), reason);
}

/// <summary>Optional read-only enrichment. The default implementation is empty and says so.</summary>
public interface IRuntimeActivitySource
{
    RuntimeActivityResult Read(SessionProfile profile, DateTimeOffset cutoff, int maximumRows);
}

public sealed class NullRuntimeActivitySource : IRuntimeActivitySource
{
    public static NullRuntimeActivitySource Instance { get; } = new();

    private NullRuntimeActivitySource()
    {
    }

    public RuntimeActivityResult Read(SessionProfile profile, DateTimeOffset cutoff, int maximumRows) =>
        RuntimeActivityResult.Unavailable("not-configured");
}
