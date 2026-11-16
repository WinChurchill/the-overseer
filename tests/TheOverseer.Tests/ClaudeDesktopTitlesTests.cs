using TheOverseer.App.Monitoring;

namespace TheOverseer.Tests;

/// <summary>Claude desktop sidebar titles for Claude rows, read-only and bounded.</summary>
public sealed class ClaudeDesktopTitlesTests : IDisposable
{
    private const string Id = "local_0818420e-ff5b-4789-a3be-05bc084bc1c6";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "overseer-titles-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;

    public ClaudeDesktopTitlesTests()
    {
        _folder = Path.Combine(_root, "account-1", "org-1");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ClaudeDesktopTitles Reader() => new(() => new[] { _root });

    private void Write(string id, string json) => File.WriteAllText(Path.Combine(_folder, id + ".json"), json);

    [Fact]
    public void ReadsOnlyTheTitleAndCleansIt()
    {
        Write(Id, """{"sessionId":"x","title":"Ship ‮the release","promptAppendSnapshot":{"secret":"never shown"}}""");
        Assert.Equal("Ship the release", Reader().TitleFor(Id));
    }

    [Fact]
    public void UpdatesWhenTheFileChanges()
    {
        Write(Id, """{"title":"First"}""");
        var reader = Reader();
        Assert.Equal("First", reader.TitleFor(Id));
        Write(Id, """{"title":"Renamed"}""");
        File.SetLastWriteTimeUtc(Path.Combine(_folder, Id + ".json"), DateTime.UtcNow.AddSeconds(5));
        Assert.Equal("Renamed", reader.TitleFor(Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0215a7ef-f523-4c22-956a-75b1a26ffbc0")]
    [InlineData("local_../../secrets")]
    [InlineData("local_abc\\..\\x")]
    public void InvalidIdsNeverTouchTheDisk(string? id) => Assert.Null(Reader().TitleFor(id));

    [Fact]
    public void MissingMalformedOrOversizedFilesFallBack()
    {
        var reader = Reader();
        Assert.Null(reader.TitleFor("local_missing"));
        Write("local_broken", "{not json");
        Assert.Null(reader.TitleFor("local_broken"));
        Write("local_notitle", """{"title":42}""");
        Assert.Null(reader.TitleFor("local_notitle"));
        Write("local_big", "{\"title\":\"x\",\"pad\":\"" + new string('a', ClaudeDesktopTitles.MaximumFileBytes) + "\"}");
        Assert.Null(reader.TitleFor("local_big"));
    }

    [Fact]
    public void LongTitlesAreClipped()
    {
        Write(Id, "{\"title\":\"" + new string('t', 400) + "\"}");
        var title = Reader().TitleFor(Id)!;
        Assert.Equal(ClaudeDesktopTitles.MaximumTitleChars, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }
}
