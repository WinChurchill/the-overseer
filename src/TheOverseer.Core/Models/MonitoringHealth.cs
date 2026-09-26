namespace TheOverseer.Core.Models;

/// <summary>
/// Monitoring health is independent of task state. It exists so that a broken
/// monitor can never be displayed as "zero tasks".
/// </summary>
public enum MonitoringHealthLevel
{
    Healthy,
    Degraded,
    Unavailable,
}

/// <summary>Safe, stable reason codes for degraded/unavailable monitoring.</summary>
public static class HealthReasons
{
    public const string None = "none";
    public const string RootMissing = "root-missing";
    public const string RootInaccessible = "root-inaccessible";
    public const string RootEmpty = "root-empty";
    public const string DiscoveryCapped = "discovery-capped";
    public const string DiscoveryBacklog = "discovery-backlog";
    public const string TrackedFileLimit = "tracked-file-limit";
    public const string FileLocked = "file-locked";
    public const string FileMissing = "file-missing";
    public const string FileTruncated = "file-truncated";
    public const string FileReplaced = "file-replaced";
    public const string FileMalformed = "file-malformed";
    public const string RecordOversize = "record-oversize";
    public const string HydrationBudget = "hydration-budget-exhausted";
    public const string ReadFailure = "read-failure";
    public const string WatcherFailed = "watcher-failed";
    public const string WatcherOverflow = "watcher-overflow";
    public const string StaleTasks = "stale-tasks";
    public const string ProvisionalTasks = "provisional-tasks";
    public const string IncompleteCoverage = "coverage-incomplete";
    public const string TitleSourceUnavailable = "title-source-unavailable";
    public const string ActivitySourceUnavailable = "activity-source-unavailable";
    public const string ConfigRecovered = "config-recovered";
    public const string LogDegraded = "log-degraded";
    public const string CycleFailure = "cycle-failure";
    public const string Cancelled = "cancelled";
}

public sealed record MonitoringHealth(
    MonitoringHealthLevel Level,
    IReadOnlyList<string> Reasons,
    bool CoverageComplete,
    DateTimeOffset? LastSuccessfulReadAt,
    DateTimeOffset ObservedAt)
{
    public static MonitoringHealth Unavailable(IReadOnlyList<string> reasons, DateTimeOffset now) =>
        new(MonitoringHealthLevel.Unavailable, reasons, false, null, now);

    public static MonitoringHealth Healthy(DateTimeOffset now, DateTimeOffset? lastReadAt) =>
        new(MonitoringHealthLevel.Healthy, Array.Empty<string>(), true, lastReadAt ?? now, now);

    public bool IsHealthy => Level == MonitoringHealthLevel.Healthy;

    public bool CountsAreAuthoritative => Level != MonitoringHealthLevel.Unavailable && CoverageComplete;

    public string ReasonsDisplay => Reasons.Count == 0 ? HealthReasons.None : string.Join(',', Reasons);
}

public enum CapabilitySupport
{
    /// <summary>Implemented and structurally verified against observed data or tests.</summary>
    Supported,

    /// <summary>The installed Codex version does not persist this signal at all.</summary>
    Unsupported,

    /// <summary>The optional source exists in principle but was not readable in this run.</summary>
    Unavailable,

    /// <summary>Implemented and contract-tested, but never observed in a live run.</summary>
    NotExercised,

    /// <summary>Observed in real recorded data during this run.</summary>
    LiveObserved,
}

public sealed record CapabilityStatus(CapabilitySupport Support, string Code, string Note);

/// <summary>
/// Explicit capability disclosure. Anything the installed Codex version cannot
/// expose is reported here instead of being guessed.
/// </summary>
public sealed record MonitorCapabilities(
    CapabilityStatus LifecycleEvents,
    CapabilityStatus StructuredActivity,
    CapabilityStatus PermissionRequests,
    CapabilityStatus InputRequests,
    CapabilityStatus TaskFailureEvidence,
    CapabilityStatus RuntimeActivitySource,
    CapabilityStatus SessionTitles,
    CapabilityStatus AttentionLiveVerification)
{
    /// <summary>
    /// Merges the capability sets of the readable sources. A capability is
    /// advertised when at least one readable source provides it, and the note names
    /// every source's own support so a partial capability is never presented as a
    /// uniform one. This replaces "the last healthy source wins", which silently
    /// dropped the other harnesses' capabilities.
    /// </summary>
    public static MonitorCapabilities Aggregate(
        IReadOnlyList<(SourceInstance Source, MonitorCapabilities Capabilities)> sources)
    {
        if (sources.Count == 0)
        {
            return Initial;
        }

        CapabilityStatus Merge(Func<MonitorCapabilities, CapabilityStatus> select)
        {
            var best = sources[0].Capabilities;
            var bestStatus = select(best);
            var notes = new List<string>(sources.Count);
            foreach (var (source, capabilities) in sources)
            {
                var status = select(capabilities);
                notes.Add(source.HarnessId + ":" + StatusCode(status.Support));
                if (Rank(status.Support) > Rank(bestStatus.Support))
                {
                    bestStatus = status;
                }
            }

            var note = notes.Count == 1 ? bestStatus.Note : string.Join(' ', notes) + " | " + bestStatus.Note;
            return bestStatus with { Note = note };
        }

        return new MonitorCapabilities(
            Merge(static capabilities => capabilities.LifecycleEvents),
            Merge(static capabilities => capabilities.StructuredActivity),
            Merge(static capabilities => capabilities.PermissionRequests),
            Merge(static capabilities => capabilities.InputRequests),
            Merge(static capabilities => capabilities.TaskFailureEvidence),
            Merge(static capabilities => capabilities.RuntimeActivitySource),
            Merge(static capabilities => capabilities.SessionTitles),
            Merge(static capabilities => capabilities.AttentionLiveVerification));
    }

    /// <summary>Strongest evidence of support wins when several sources disagree.</summary>
    private static int Rank(CapabilitySupport support) => support switch
    {
        CapabilitySupport.LiveObserved => 5,
        CapabilitySupport.Supported => 4,
        CapabilitySupport.NotExercised => 3,
        CapabilitySupport.Unavailable => 2,
        _ => 1,
    };

    private static string StatusCode(CapabilitySupport support) => support switch
    {
        CapabilitySupport.LiveObserved => "live",
        CapabilitySupport.Supported => "supported",
        CapabilitySupport.NotExercised => "not-exercised",
        CapabilitySupport.Unavailable => "unavailable",
        _ => "unsupported",
    };

    /// <summary>
    /// What an external metadata bridge (dsh observer or Claude hook bridge)
    /// actually provides. It is not the Codex capability set: the bridges DO export
    /// correlated approval/input requests and explicit turn outcomes, so publishing
    /// the Codex "unsupported" claims would under-report them. Capabilities a bridge
    /// cannot supply are stated as unsupported instead of being guessed.
    /// </summary>
    public static MonitorCapabilities ForBridge(string harnessId, bool continuousHeartbeat)
    {
        var source = HarnessIds.DisplayName(harnessId);
        var heartbeatNote = continuousHeartbeat
            ? source + ": periodic heartbeat while attached"
            : source + ": event-driven; absence of events proves neither idle nor disconnection";
        return new MonitorCapabilities(
            new CapabilityStatus(CapabilitySupport.Supported, "bridge-lifecycle", source + ": session/turn start and end records"),
            new CapabilityStatus(CapabilitySupport.Supported, "bridge-activity", source + ": tool start/end and plan markers, no content"),
            new CapabilityStatus(CapabilitySupport.Supported, "bridge-permission-requests", source + ": approval asked/decided correlated by request id"),
            new CapabilityStatus(CapabilitySupport.Supported, "bridge-input-requests", source + ": input asked/answered correlated by request id"),
            new CapabilityStatus(CapabilitySupport.Supported, "bridge-task-failure", source + ": explicit turn outcome"),
            new CapabilityStatus(CapabilitySupport.Unsupported, "bridge-runtime-activity", source + ": the bridge runs in its own host and cannot report machine activity"),
            new CapabilityStatus(CapabilitySupport.Unsupported, "bridge-session-titles", source + ": titles are conversation content and are never exported"),
            new CapabilityStatus(CapabilitySupport.Supported, "bridge-attention-live", heartbeatNote));
    }

    public static MonitorCapabilities Initial { get; } = new(
        new CapabilityStatus(CapabilitySupport.NotExercised, "rollout-lifecycle", "task_started/task_complete/turn_aborted from rollout records"),
        new CapabilityStatus(CapabilitySupport.NotExercised, "structured-activity", "item_completed and tool call records"),
        new CapabilityStatus(CapabilitySupport.Unsupported, "permission-requests", "approval events are transient in Codex, not rollout-persisted"),
        new CapabilityStatus(CapabilitySupport.Supported, "input-requests", "input tool-call metadata supported; transient event-only requests may be unavailable"),
        new CapabilityStatus(CapabilitySupport.Unsupported, "task-failure", "no explicit task-level failure record observed"),
        new CapabilityStatus(CapabilitySupport.Unavailable, "runtime-activity-source", "optional read-only state store enrichment not read yet"),
        new CapabilityStatus(CapabilitySupport.Unavailable, "session-titles", "official session index not read yet"),
        new CapabilityStatus(CapabilitySupport.NotExercised, "attention-live", "no live attention event observed; contracts tested synthetically"));
}

/// <summary>Counters describing what the run actually observed. Never fabricated.</summary>
public sealed record LiveObservationStats(
    int SessionMetaRecords,
    int TaskStartedRecords,
    int TaskCompletedRecords,
    int TurnAbortedRecords,
    int ItemCompletedRecords,
    int ToolCallRecords,
    int PermissionRequestRecords,
    int InputRequestRecords,
    int MalformedRecords,
    int OversizeRecords,
    int FilesHydrated,
    int FilesTracked,
    long BytesRead)
{
    public static LiveObservationStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public bool AnyLifecycleObserved =>
        TaskStartedRecords > 0 || TaskCompletedRecords > 0 || TurnAbortedRecords > 0;
}
