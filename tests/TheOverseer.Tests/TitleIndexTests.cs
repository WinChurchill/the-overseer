using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class TitleIndexTests : IDisposable
{
    private readonly SyntheticCodexHome _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void MissingIndexIsUnavailableAndNeverThrows()
    {
        var index = new SessionTitleIndex(512, 4096);
        Assert.False(index.Refresh(_home.IndexPath));
        Assert.Equal(TitleIndexAvailability.Unavailable, index.Status.Availability);
        Assert.Equal(string.Empty, index.GetTitle("anything"));
    }

    [Fact]
    public void ReadingIsIncrementalAcrossAppends()
    {
        _home.WriteIndex(new[] { ("id-1", "First task") });
        var index = new SessionTitleIndex(512, 4096);
        Assert.True(index.Refresh(_home.IndexPath));
        Assert.Equal("First task", index.GetTitle("id-1"));

        _home.WriteIndex(new[] { ("id-2", "Second task") });
        Assert.True(index.Refresh(_home.IndexPath));
        Assert.Equal("Second task", index.GetTitle("id-2"));
        Assert.Equal("First task", index.GetTitle("id-1"));
    }

    [Fact]
    public void ReplacementResetsCachedTitles()
    {
        _home.WriteIndex(new[] { ("id-1", "Old title") });
        var index = new SessionTitleIndex(512, 4096);
        Assert.True(index.Refresh(_home.IndexPath));

        File.WriteAllLines(_home.IndexPath, new[]
        {
            "{\"id\":\"id-9\",\"thread_name\":\"Replaced title\",\"updated_at\":\"2026-01-02T03:04:05Z\"}",
        });
        File.SetLastWriteTimeUtc(_home.IndexPath, DateTime.UtcNow.AddSeconds(5));
        Assert.True(index.Refresh(_home.IndexPath));
        Assert.Equal("Replaced title", index.GetTitle("id-9"));
        Assert.Equal(string.Empty, index.GetTitle("id-1"));
    }

    [Fact]
    public void CacheIsBoundedEvenWithPrioritizedIds()
    {
        _home.WriteIndex(Enumerable.Range(0, 200).Select(index => ("id-" + index, "Title " + index)));
        var index = new SessionTitleIndex(32, 64 * 1024);
        Assert.True(index.Refresh(_home.IndexPath));
        Assert.True(index.EntryCount <= 32, "cache must stay bounded, actual " + index.EntryCount);
    }

    [Fact]
    public void LongTitlesAreClamped()
    {
        _home.WriteIndex(new[] { ("id-long", new string('t', 400)) });
        var index = new SessionTitleIndex(512, 4096);
        Assert.True(index.Refresh(_home.IndexPath));
        var title = index.GetTitle("id-long");
        Assert.True(title.Length <= 65, "title length " + title.Length);
        Assert.EndsWith("\u2026", title);
    }

    [Fact]
    public void MalformedLinesDoNotEraseGoodTitles()
    {
        File.WriteAllLines(_home.IndexPath, new[]
        {
            "{\"id\":\"id-1\",\"thread_name\":\"Good\",\"updated_at\":\"2026-01-02T03:04:05Z\"}",
            "{\"id\": broken",
        });
        var index = new SessionTitleIndex(512, 4096);
        index.Refresh(_home.IndexPath);
        Assert.Equal("Good", index.GetTitle("id-1"));
    }
}
