namespace TheOverseer.Core.Monitoring.Sessions;

public sealed record SessionFileCandidate(
    string FullName,
    string ProfileId,
    long Length,
    DateTime LastWriteUtc,
    string HintCode);

public sealed record DiscoverySlice(
    IReadOnlyList<SessionFileCandidate> Candidates,
    bool SweepCompleted,
    bool BudgetExhausted,
    bool RootMissing,
    bool RootInaccessible,
    int DirectoriesVisited,
    int EntriesScanned)
{
    public static DiscoverySlice Empty { get; } = new(
        Array.Empty<SessionFileCandidate>(), true, false, false, false, 0, 0);
}

/// <summary>
/// Bounded, fair session enumeration. Adapted from codex-monitor-hud
/// src-dotnet/CodexMonitorHud.Core/Sessions/SessionDiscovery.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT). The upstream
/// unbounded recursive enumeration with per-directory ToArray() was replaced
/// with explicit directory/entry budgets, reparse-point refusal, and
/// continuation state so a large history is never scanned in one pass and
/// never restarts from the newest directory on every cycle.
/// </summary>
public sealed class BoundedSessionEnumerator : IDisposable
{
    private readonly string _sessionsRoot;
    private readonly string _profileId;
    private readonly MonitorOptions _options;
    private readonly PriorityQueue<DirectoryCursor, string> _pending = new(
        Comparer<string>.Create(static (left, right) => string.CompareOrdinal(right, left)));
    private bool _inaccessible;
    private int _visited;

    public BoundedSessionEnumerator(SessionProfile profile, MonitorOptions options)
    {
        _sessionsRoot = profile.SessionsRoot;
        _profileId = profile.Id;
        _options = options;
        Restart();
    }

    public bool SweepCompleted => _pending.Count == 0;
    public int PendingDirectories => _pending.Count;
    public int VisitedDirectories => _visited;

    public void Restart()
    {
        Dispose();
        _inaccessible = false;
        _visited = 0;
        if (Directory.Exists(_sessionsRoot) && !IsReparsePoint(_sessionsRoot))
            _pending.Enqueue(new DirectoryCursor(_sessionsRoot), _sessionsRoot);
        else if (Directory.Exists(_sessionsRoot))
            _inaccessible = true;
    }

    public DiscoverySlice NextSlice()
    {
        var candidates = new List<SessionFileCandidate>();
        var directories = 0;
        var entries = 0;
        while (_pending.Count > 0 && directories < _options.DiscoveryDirectoriesPerSlice &&
               entries < _options.DiscoveryEntriesPerSlice && candidates.Count < _options.DiscoveryCandidatesPerSlice)
        {
            var cursor = _pending.Dequeue();
            directories++;
            var finished = false;
            try
            {
                if (cursor.Directories is null && !cursor.ChildrenComplete)
                {
                    cursor.Directories = Directory.EnumerateDirectories(cursor.Path).GetEnumerator();
                    _visited++;
                }
                while (!cursor.ChildrenComplete && entries < _options.DiscoveryEntriesPerSlice &&
                       _pending.Count < Math.Max(1, _options.DiscoveryPendingDirectories - 1))
                {
                    if (!cursor.Directories!.MoveNext())
                    {
                        cursor.Directories.Dispose();
                        cursor.Directories = null;
                        cursor.ChildrenComplete = true;
                        break;
                    }
                    entries++;
                    var child = cursor.Directories.Current;
                    if (!Path.GetFileName(child).StartsWith('.') && !IsReparsePoint(child))
                        _pending.Enqueue(new DirectoryCursor(child), child);
                }
                if (cursor.ChildrenComplete)
                {
                    cursor.Files ??= Directory.EnumerateFiles(cursor.Path).GetEnumerator();
                    while (entries < _options.DiscoveryEntriesPerSlice && candidates.Count < _options.DiscoveryCandidatesPerSlice)
                    {
                        if (!cursor.Files.MoveNext())
                        {
                            finished = true;
                            break;
                        }
                        entries++;
                        var path = cursor.Files.Current;
                        if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || IsReparsePoint(path)) continue;
                        var candidate = SessionFileProbe.TryStat(path, _profileId, "enumerated");
                        if (candidate is not null) candidates.Add(candidate);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _inaccessible = true;
                finished = true;
            }
            if (finished) cursor.Dispose();
            else _pending.Enqueue(cursor, cursor.Path);
        }
        return new DiscoverySlice(candidates, SweepCompleted, !SweepCompleted,
            !Directory.Exists(_sessionsRoot), _inaccessible, directories, entries);
    }

    public void Dispose()
    {
        while (_pending.TryDequeue(out var cursor, out _)) cursor.Dispose();
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    private sealed class DirectoryCursor(string path) : IDisposable
    {
        public string Path { get; } = path;
        public IEnumerator<string>? Directories;
        public IEnumerator<string>? Files;
        public bool ChildrenComplete;
        public void Dispose()
        {
            Directories?.Dispose();
            Files?.Dispose();
        }
    }
}
public static class SessionFileProbe
{
    /// <summary>
    /// Returns true when the file cannot be opened for shared read, which is a
    /// lock diagnostic and never proof of execution state.
    /// </summary>
    public static bool IsReadBlocked(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1,
                FileOptions.None);
            return false;
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static SessionFileCandidate? TryStat(string path, string profileId, string hintCode)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return new SessionFileCandidate(info.FullName, profileId, info.Length, info.LastWriteTimeUtc, hintCode);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}