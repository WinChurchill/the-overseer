namespace TheOverseer.Core.Monitoring;

/// <summary>
/// Bounded-resource and cadence defaults for Plan 1. Every value is clamped by
/// <see cref="Clamped"/> so neither configuration nor tests can remove a bound.
/// </summary>
public sealed record MonitorOptions
{
    public static MonitorOptions Default { get; } = new();

    // --- tracked files and per-pass byte budgets -------------------------
    public int MaxTrackedFiles { get; init; } = 64;
    public int MaxWatcherHintPaths { get; init; } = 256;
    public int PerFileBytesPerPass { get; init; } = 256 * 1024;
    public int GlobalBytesPerPass { get; init; } = 4 * 1024 * 1024;
    public int MaxPendingRecordBytes { get; init; } = 1024 * 1024;

    // --- hydration -------------------------------------------------------
    public int MaxIdentityHeaderBytes { get; init; } = 64 * 1024;
    public int HydrationInitialWindowBytes { get; init; } = 1024 * 1024;
    public int HydrationMaxScanBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxHydrationRecords { get; init; } = 8192;
    public int MaxRecordChars { get; init; } = 1024 * 1024;

    // --- discovery -------------------------------------------------------
    public int DiscoveryDirectoriesPerSlice { get; init; } = 128;
    public int DiscoveryEntriesPerSlice { get; init; } = 2048;
    public int DiscoveryPendingDirectories { get; init; } = 2048;
    public int DiscoveryCandidatesPerSlice { get; init; } = 256;

    // --- caches ----------------------------------------------------------
    public int MaxTitleCacheEntries { get; init; } = 512;
    public int TitleReadBytesPerPass { get; init; } = 256 * 1024;
    public int MaxTurnHistoryPerTask { get; init; } = 64;
    public int MaxCorrelatedRequestsPerTask { get; init; } = 64;
    public int MaxTrackedConversations { get; init; } = 512;
    public int MaxSeenRecordMarkers { get; init; } = 512;
    public int MaxReleasedNumbers { get; init; } = 512;
    public int MaxAttentionHistory { get; init; } = 64;

    // --- cadence ---------------------------------------------------------
    public TimeSpan DebounceInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan KnownFilePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StaleLease { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan FailureBackoffInitial { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan FailureBackoffMax { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a tracked file may be absent from disk before it is released. A file
    /// that disappears (rotation, cleanup, a move) must not keep the source degraded
    /// and its task unobservable forever; if it reappears it is admitted again.
    /// </summary>
    public TimeSpan MissingFileRetention { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Number release cooldown. A released number is reused for the same
    /// conversation when the conversation reappears.
    /// </summary>
    public TimeSpan NumberReleaseCooldown { get; init; } = TimeSpan.FromSeconds(120);

    public MonitorOptions Clamped() => this with
    {
        MaxTrackedFiles = Clamp(MaxTrackedFiles, 1, 1024),
        MaxWatcherHintPaths = Clamp(MaxWatcherHintPaths, 16, 4096),
        PerFileBytesPerPass = Clamp(PerFileBytesPerPass, 4096, 4 * 1024 * 1024),
        GlobalBytesPerPass = Clamp(GlobalBytesPerPass, 64 * 1024, 64 * 1024 * 1024),
        MaxPendingRecordBytes = Clamp(MaxPendingRecordBytes, 64 * 1024, 8 * 1024 * 1024),
        MaxIdentityHeaderBytes = Clamp(MaxIdentityHeaderBytes, 4096, 1024 * 1024),
        HydrationInitialWindowBytes = Clamp(HydrationInitialWindowBytes, 64 * 1024, 8 * 1024 * 1024),
        HydrationMaxScanBytes = Clamp(HydrationMaxScanBytes, 256 * 1024, 64 * 1024 * 1024),
        MaxHydrationRecords = Clamp(MaxHydrationRecords, 256, 65536),
        MaxRecordChars = Clamp(MaxRecordChars, 64 * 1024, 8 * 1024 * 1024),
        DiscoveryDirectoriesPerSlice = Clamp(DiscoveryDirectoriesPerSlice, 8, 4096),
        DiscoveryEntriesPerSlice = Clamp(DiscoveryEntriesPerSlice, 64, 65536),
        DiscoveryPendingDirectories = Clamp(DiscoveryPendingDirectories, 64, 16384),
        DiscoveryCandidatesPerSlice = Clamp(DiscoveryCandidatesPerSlice, 8, 1024),
        MaxTitleCacheEntries = Clamp(MaxTitleCacheEntries, 32, 8192),
        TitleReadBytesPerPass = Clamp(TitleReadBytesPerPass, 4096, 4 * 1024 * 1024),
        MaxTurnHistoryPerTask = Clamp(MaxTurnHistoryPerTask, 8, 1024),
        MaxCorrelatedRequestsPerTask = Clamp(MaxCorrelatedRequestsPerTask, 8, 1024),
        MaxTrackedConversations = Clamp(MaxTrackedConversations, 16, 8192),
        MaxSeenRecordMarkers = Clamp(MaxSeenRecordMarkers, 32, 8192),
        MaxReleasedNumbers = Clamp(MaxReleasedNumbers, 32, 4096),
        MaxAttentionHistory = Clamp(MaxAttentionHistory, 4, 1024),
        DebounceInterval = Clamp(DebounceInterval, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5)),
        KnownFilePollInterval = Clamp(KnownFilePollInterval, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30)),
        ReconcileInterval = Clamp(ReconcileInterval, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)),
        StaleLease = Clamp(StaleLease, TimeSpan.FromMinutes(1), TimeSpan.FromHours(24)),
        FailureBackoffInitial = Clamp(FailureBackoffInitial, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)),
        FailureBackoffMax = Clamp(FailureBackoffMax, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30)),
        MissingFileRetention = Clamp(MissingFileRetention, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30)),
        NumberReleaseCooldown = Clamp(NumberReleaseCooldown, TimeSpan.Zero, TimeSpan.FromMinutes(30)),
    };

    private static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        return value > maximum ? maximum : value;
    }
}
