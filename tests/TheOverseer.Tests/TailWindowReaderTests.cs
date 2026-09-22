using TheOverseer.Core.Monitoring.Sessions;

namespace TheOverseer.Tests;

public sealed class TailWindowReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));

    public TailWindowReaderTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public void ReadsOnlyTheBoundedTailWindow()
    {
        var path = Path.Combine(_directory, "rollout.jsonl");
        var lines = Enumerable.Range(0, 2000).Select(index => "{\"index\":" + index + ",\"pad\":\"" + new string('p', 100) + "\"}").ToArray();
        File.WriteAllLines(path, lines);

        var result = TailWindowReader.ReadTailLines(path, 16 * 1024, 8192, 1024 * 1024);
        Assert.True(result.WindowTruncated);
        Assert.True(result.Lines.Count > 0);
        Assert.EndsWith("\"}\n".TrimEnd('\n'), result.Lines[^1]);
        Assert.Contains("\"index\":1999", result.Lines[^1]);
        Assert.True(result.BytesRead <= 16 * 1024 + 64 * 1024);
    }

    [Fact]
    public void WholeSmallFileIsReturnedOldestToNewest()
    {
        var path = Path.Combine(_directory, "small.jsonl");
        File.WriteAllLines(path, new[] { "{\"a\":1}", "{\"a\":2}", "{\"a\":3}" });
        var result = TailWindowReader.ReadTailLines(path, 1024 * 1024, 8192, 1024);
        Assert.False(result.WindowTruncated);
        Assert.Equal(new[] { "{\"a\":1}", "{\"a\":2}", "{\"a\":3}" }, result.Lines);
    }

    [Fact]
    public void DiscardsOversizeRecordAndKeepsRelativeOrder()
    {
        var path = Path.Combine(_directory, "oversize.jsonl");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("{\"a\":1}");
            writer.WriteLine("{\"big\":\"" + new string('x', 5000) + "\"}");
            writer.WriteLine("{\"a\":2}");
        }

        var result = TailWindowReader.ReadTailLines(path, 1024 * 1024, 8192, 512);
        Assert.True(result.OversizeDiscarded);
        Assert.Equal(new[] { "{\"a\":1}", "{\"a\":2}" }, result.Lines);
    }

    [Fact]
    public void RespectsRecordCap()
    {
        var path = Path.Combine(_directory, "cap.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, 500).Select(index => "{\"i\":" + index + "}"));
        var result = TailWindowReader.ReadTailLines(path, 1024 * 1024, 10, 1024 * 1024);
        Assert.Equal(10, result.Lines.Count);
        Assert.Contains("\"i\":499", result.Lines[^1]);
    }
}
