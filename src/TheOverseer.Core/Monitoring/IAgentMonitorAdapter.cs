using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring;

/// <summary>
/// One observation source. A compiled internal contract: adapters are registered
/// in composition, never loaded at runtime. Each adapter owns its own health,
/// capabilities, freshness and tasks, and must publish source-local results so a
/// failure in one harness can never erase another harness's work.
/// </summary>
public interface IAgentMonitorAdapter : IAsyncDisposable
{
    /// <summary>Stable harness plus source-instance identity.</summary>
    SourceInstance Source { get; }

    /// <summary>Latest immutable source snapshot. Never null.</summary>
    SourceSnapshot Current { get; }

    /// <summary>Raised once per published source snapshot.</summary>
    event Action<SourceSnapshot>? SnapshotAvailable;

    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Hint that the source should re-read; adapters remain bounded.</summary>
    void RequestReconcile();
}
