using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class DiscoveryTests : IDisposable
{
    private readonly SyntheticCodexHome _home = new();

    public void Dispose() => _home.Dispose();

    private SessionProfile Profile => new(
        "codex",
        "Codex",
        _home.Root,
        _home.SessionsRoot,
        _home.IndexPath,
        _home.StateDatabasePath);

    [Fact]
    public void MissingRootIsReportedNotThrown()
    {
        var profile = Profile with { SessionsRoot = Path.Combine(_home.Root, "does-not-exist") };
        var enumerator = new BoundedSessionEnumerator(profile, MonitorOptions.Default);
        var slice = enumerator.NextSlice();
        Assert.True(slice.RootMissing || slice.SweepCompleted);
        Assert.Empty(slice.Candidates);
    }

    [Fact]
    public void EnumeratesWithinDirectoryAndEntryCaps()
    {
        for (var index = 0; index < 6; index++)
        {
            _home.WriteRollout(
                "01a0c600-0000-0000-0000-00000000000" + index,
                new[] { Records.SessionMeta("01a0c600-0000-0000-0000-00000000000" + index) },
                datePath: "2026/01/0" + (index % 3 + 1));
        }

        var options = MonitorOptions.Default with
        {
            DiscoveryDirectoriesPerSlice = 1,
            DiscoveryEntriesPerSlice = 1,
            DiscoveryCandidatesPerSlice = 1,
        };
        var enumerator = new BoundedSessionEnumerator(Profile, options);
        var seen = new List<string>();
        var slices = 0;
        while (slices < 40)
        {
            var slice = enumerator.NextSlice();
            Assert.True(slice.DirectoriesVisited <= 1);
            Assert.True(slice.EntriesScanned <= 1);
            Assert.True(slice.Candidates.Count <= 1);
            seen.AddRange(slice.Candidates.Select(candidate => candidate.FullName));
            slices++;
            if (slice.SweepCompleted && enumerator.PendingDirectories == 0)
            {
                break;
            }
        }

        Assert.True(slices > 1, "bounded slices must require continuation");
        Assert.Equal(6, seen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SweepEventuallyCompletesAndIsIdempotent()
    {
        _home.WriteRollout("01a0c601-0000-0000-0000-000000000010", new[] { Records.SessionMeta("01a0c601-0000-0000-0000-000000000010") });
        var enumerator = new BoundedSessionEnumerator(Profile, MonitorOptions.Default);
        var first = enumerator.NextSlice();
        Assert.Single(first.Candidates);
        Assert.True(first.SweepCompleted);

        var second = enumerator.NextSlice();
        Assert.Empty(second.Candidates);
        Assert.True(second.SweepCompleted);

        enumerator.Restart();
        Assert.Single(enumerator.NextSlice().Candidates);
    }

    [Fact]
    public void NewestDateDirectoriesArePrioritized()
    {
        _home.WriteRollout("01a0c602-0000-0000-0000-000000000020", new[] { Records.SessionMeta("01a0c602-0000-0000-0000-000000000020") }, datePath: "2026/01/02");
        _home.WriteRollout("01a0c603-0000-0000-0000-000000000021", new[] { Records.SessionMeta("01a0c603-0000-0000-0000-000000000021") }, datePath: "2026/03/09");

        var options = MonitorOptions.Default with { DiscoveryDirectoriesPerSlice = 3, DiscoveryEntriesPerSlice = 64 };
        var enumerator = new BoundedSessionEnumerator(Profile, options);
        var order = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            var slice = enumerator.NextSlice();
            order.AddRange(slice.Candidates.Select(candidate => candidate.FullName));
            if (slice.SweepCompleted)
            {
                break;
            }
        }

        Assert.Equal(2, order.Count);
        Assert.Contains("01a0c603", order[0]);
    }

    [Fact]
    public void DotDirectoriesAreSkippedAndNormalDirectoriesAreNotReparsePoints()
    {
        var hidden = Path.Combine(_home.SessionsRoot, ".hidden", "2026", "01", "02");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "rollout-x.jsonl"), Records.SessionMeta("01a0c604-0000-0000-0000-000000000030"));

        var enumerator = new BoundedSessionEnumerator(Profile, MonitorOptions.Default);
        var candidates = new List<SessionFileCandidate>();
        for (var index = 0; index < 20; index++)
        {
            var slice = enumerator.NextSlice();
            candidates.AddRange(slice.Candidates);
            if (slice.SweepCompleted)
            {
                break;
            }
        }

        Assert.Empty(candidates);
        Assert.False(BoundedSessionEnumerator.IsReparsePoint(_home.SessionsRoot));
    }

    [Fact]
    public void CandidateStatIsBoundedAndSafeForMissingPaths()
    {
        Assert.Null(SessionFileProbe.TryStat(Path.Combine(_home.Root, "missing.jsonl"), "codex", "test"));
        var path = _home.WriteRollout("01a0c605-0000-0000-0000-000000000040", new[] { Records.SessionMeta("01a0c605-0000-0000-0000-000000000040") });
        var candidate = SessionFileProbe.TryStat(path, "codex", "test");
        Assert.NotNull(candidate);
        Assert.False(SessionFileProbe.IsReadBlocked(path));
    }
}
