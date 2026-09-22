using System.Text;
using TheOverseer.Core.Monitoring.Sessions;

namespace TheOverseer.Tests;

public sealed class JsonlReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));

    public JsonlReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string FilePath(string name = "rollout.jsonl") => Path.Combine(_directory, name);

    [Fact]
    public void ReadsAppendedRecordsAndRemembersOffset()
    {
        var path = FilePath();
        File.WriteAllText(path, "{\"a\":1}\n{\"a\":2}\n");
        var reader = new IncrementalJsonlReader();

        var first = reader.ReadAppended(path, 4096);
        Assert.Equal(new[] { "{\"a\":1}", "{\"a\":2}" }, first.Lines);
        Assert.False(first.HasUnreadData);
        Assert.Equal(new FileInfo(path).Length, reader.Offset);

        File.AppendAllText(path, "{\"a\":3}\n");
        var second = reader.ReadAppended(path, 4096);
        Assert.Equal(new[] { "{\"a\":3}" }, second.Lines);
    }

    [Fact]
    public void PreservesPartialUtf8AndJsonAcrossReads()
    {
        var path = FilePath();
        var entity = "{\"text\":\"caf\u00e9 \u4e2d\u6587\"}";
        var bytes = Encoding.UTF8.GetBytes(entity + "\n");
        // Split inside the multi-byte sequence and inside the JSON object.
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.Write(bytes, 0, 12);
        }

        var reader = new IncrementalJsonlReader();
        var partial = reader.ReadAppended(path, 4096);
        Assert.Empty(partial.Lines);

        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            stream.Write(bytes, 12, bytes.Length - 12);
        }

        var complete = reader.ReadAppended(path, 4096);
        Assert.Single(complete.Lines);
        Assert.Equal(entity, complete.Lines[0]);
    }

    [Fact]
    public void HandlesCrlfBomAndFinalRecordWithoutNewline()
    {
        var path = FilePath();
        File.WriteAllText(path, "\uFEFF{\"a\":1}\r\n{\"a\":2}");
        var reader = new IncrementalJsonlReader();
        var result = reader.ReadAppended(path, 4096);
        Assert.Equal(new[] { "{\"a\":1}", "{\"a\":2}" }, result.Lines);
    }

    [Fact]
    public void DoesNotEmitIncompleteTrailingObject()
    {
        var path = FilePath();
        File.WriteAllText(path, "{\"a\":1}\n{\"a\":");
        var reader = new IncrementalJsonlReader();
        var result = reader.ReadAppended(path, 4096);
        Assert.Equal(new[] { "{\"a\":1}" }, result.Lines);
        Assert.True(reader.Offset > 0);

        File.AppendAllText(path, "2}\n");
        var next = reader.ReadAppended(path, 4096);
        Assert.Equal(new[] { "{\"a\":2}" }, next.Lines);
    }

    [Fact]
    public void DiscardsOversizeRecordAndRecovers()
    {
        var path = FilePath();
        var reader = new IncrementalJsonlReader(maxPendingBytes: 4096);
        using (var stream = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
        {
            stream.Write("{\"big\":\"" + new string('x', 20_000) + "\"}\n");
            stream.Write("{\"a\":1}\n");
        }

        var result = reader.ReadAppended(path, 64 * 1024);
        Assert.True(result.OversizeDiscarded);
        Assert.Equal(new[] { "{\"a\":1}" }, result.Lines);
    }

    [Fact]
    public void ResetsOnTruncation()
    {
        var path = FilePath();
        File.WriteAllText(path, "{\"a\":1}\n{\"a\":2}\n");
        var reader = new IncrementalJsonlReader();
        _ = reader.ReadAppended(path, 4096);
        var generation = reader.Generation;

        File.WriteAllText(path, "{\"b\":1}\n");
        var result = reader.ReadAppended(path, 4096);
        Assert.True(result.Truncated);
        Assert.True(reader.Generation > generation);
        Assert.Equal(new[] { "{\"b\":1}" }, result.Lines);
    }

    [Fact]
    public void RespectsByteBudgetAndReportsBacklog()
    {
        var path = FilePath();
        var builder = new StringBuilder();
        for (var index = 0; index < 400; index++)
        {
            builder.Append("{\"index\":").Append(index).Append(", \"padding\":\"").Append(new string('p', 200)).Append("\"}\n");
        }

        File.WriteAllText(path, builder.ToString());
        var reader = new IncrementalJsonlReader();
        var first = reader.ReadAppended(path, 8192);
        Assert.True(first.HasUnreadData);
        Assert.True(first.BudgetExhausted);
        Assert.True(first.Lines.Count is > 0 and < 400);

        var second = reader.ReadAppended(path, 8192);
        Assert.True(second.Lines.Count > 0);
        Assert.NotEqual(first.Lines[0], second.Lines[0]);
    }

    [Fact]
    public void SeekToSkipsAlreadyHydratedBytes()
    {
        var path = FilePath();
        File.WriteAllText(path, "{\"a\":1}\n");
        var reader = new IncrementalJsonlReader();
        reader.SeekTo(new FileInfo(path).Length);
        Assert.Empty(reader.ReadAppended(path, 4096).Lines);

        File.AppendAllText(path, "{\"a\":2}\n");
        Assert.Equal(new[] { "{\"a\":2}" }, reader.ReadAppended(path, 4096).Lines);
    }
}
