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
public sealed class BoundedSessionEnumerator
{
    private readonly string _sessionsRoot;
    private readonly string _profileId;
    private readonly int _directoriesPerSlice;
    private readonly int _entriesPerSlice;
    private readonly int _pendingCap;
    private readonly int _candidatesPerSlice;
    private readonly PriorityQueue<string, string> _pending;
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _fileResume = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _directoryResume = new(StringComparer.OrdinalIgnoreCase);
    private bool _sweepCompleted;

    public BoundedSessionEnumerator(SessionProfile profile, MonitorOptions options)
    {
        _sessionsRoot = profile.SessionsRoot;
        _profileId = profile.Id;
        _directoriesPerSlice = options.DiscoveryDirectoriesPerSlice;
        _entriesPerSlice = options.DiscoveryEntriesPerSlice;
        _pendingCap = options.DiscoveryPendingDirectories;
        _candidatesPerSlice = options.DiscoveryCandidatesPerSlice;
        _pending = new PriorityQueue<string, string>(
            Comparer<string>.Create(static (left, right) => string.CompareOrdinal(right, left)));
        Restart();
    }

    public bool SweepCompleted => _sweepCompleted;

    public int PendingDirectories => _pending.Count;

    public int VisitedDirectories => _visited.Count;

    /// <summary>Restarts the sweep; used by reconciliation and by restart recovery.</summary>
    public void Restart()
    {
        _pending.Clear();
        _visited.Clear();
        _fileResume.Clear();
        _directoryResume.Clear();
        _sweepCompleted = false;
        if (Directory.Exists(_sessionsRoot))
        {
            _pending.Enqueue(_sessionsRoot, _sessionsRoot);
        }
        else
        {
            _sweepCompleted = true;
        }
    }

    public DiscoverySlice NextSlice()
    {
        if (_sweepCompleted)
        {
            return DiscoverySlice.Empty with { RootMissing = !Directory.Exists(_sessionsRoot) };
        }

        if (!Directory.Exists(_sessionsRoot))
        {
            _sweepCompleted = true;
            return DiscoverySlice.Empty with { RootMissing = true };
        }

        var candidates = new List<SessionFileCandidate>();
        var directoriesVisited = 0;
        var entriesScanned = 0;
        var rootInaccessible = false;
        var budgetExhausted = false;

        while (directoriesVisited < _directoriesPerSlice && entriesScanned < _entriesPerSlice)
        {
            if (!_pending.TryDequeue(out var directory, out _))
            {
                break;
            }

            if (_visited.Contains(directory))
            {
                continue;
            }

            directoriesVisited++;

            // Child directories first: they are cheap to reach and keep the
            // newest date directories discoverable early.
            var directoryOffset = _directoryResume.GetValueOrDefault(directory);
            var directoriesCompleted = true;
            var seenDirectories = 0;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    seenDirectories++;
                    if (seenDirectories <= directoryOffset)
                    {
                        continue;
                    }

                    if (entriesScanned >= _entriesPerSlice)
                    {
                        directoriesCompleted = false;
                        break;
                    }

                    entriesScanned++;
                    var name = Path.GetFileName(child);
                    if (name.Length == 0 || name[0] == '.')
                    {
                        continue;
                    }

                    if (IsReparsePoint(child))
                    {
                        continue;
                    }

                    if (_pending.Count >= _pendingCap)
                    {
                        budgetExhausted = true;
                        directoriesCompleted = false;
                        break;
                    }

                    _pending.Enqueue(child, child);
                }
            }
            catch (UnauthorizedAccessException)
            {
                rootInaccessible = true;
            }
            catch (IOException)
            {
                rootInaccessible = true;
            }

            if (!directoriesCompleted)
            {
                _directoryResume[directory] = seenDirectories - 1;
                _pending.Enqueue(directory, directory);
                budgetExhausted = true;
                break;
            }

            _directoryResume.Remove(directory);

            var fileOffset = _fileResume.GetValueOrDefault(directory);
            var filesCompleted = true;
            var seenFiles = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    seenFiles++;
                    if (seenFiles <= fileOffset)
                    {
                        continue;
                    }

                    if (entriesScanned >= _entriesPerSlice || candidates.Count >= _candidatesPerSlice)
                    {
                        filesCompleted = false;
                        break;
                    }

                    entriesScanned++;
                    var candidate = TryCreateCandidate(file, "enumerated");
                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                rootInaccessible = true;
            }
            catch (IOException)
            {
                rootInaccessible = true;
            }

            if (!filesCompleted)
            {
                // The rest of this directory is resumed by a later slice instead
                // of being skipped until a full rescan.
                _fileResume[directory] = seenFiles - 1;
                _pending.Enqueue(directory, directory);
                budgetExhausted = true;
                break;
            }

            _fileResume.Remove(directory);
            _visited.Add(directory);
        }

        if (_pending.Count == 0)
        {
            _sweepCompleted = true;
        }

        return new DiscoverySlice(
            candidates,
            _sweepCompleted,
            budgetExhausted || _pending.Count > 0,
            false,
            rootInaccessible,
            directoriesVisited,
            entriesScanned);
    }

    private SessionFileCandidate? TryCreateCandidate(string path, string hintCode)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return new SessionFileCandidate(info.FullName, _profileId, info.Length, info.LastWriteTimeUtc, hintCode);
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

    public static bool IsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
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