using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace TheOverseer.Core.Monitoring.Sessions;

public sealed record TailReadResult(
    IReadOnlyList<string> Lines,
    long WindowBytes,
    bool WindowTruncated,
    bool OversizeDiscarded,
    int BytesRead);

/// <summary>
/// Backward chunked tail reader used for bounded initial hydration. Adapted from
/// codex-monitor-hud src-dotnet/CodexMonitorHud.Core/Sessions/BoundedTailReader.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT). The upstream
/// accounting projection, allowance fields and very large per-line ceiling were
/// removed; records are returned oldest-to-newest inside an explicit window.
/// </summary>
public static class TailWindowReader
{
    private const int ChunkBytes = 64 * 1024;

    public static TailReadResult ReadTailLines(string path, long windowBytes, int maxLines, int maxLineBytes)
    {
        var limit = Math.Max(1, maxLines);
        var lineCeiling = Math.Max(4096, maxLineBytes);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ChunkBytes,
            FileOptions.RandomAccess);

        var length = stream.Length;
        var window = Math.Min(Math.Max(1, windowBytes), length);
        var earliest = length - window;
        var position = length;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var reversedLine = new List<byte>(1024);
        var reversedLines = new List<string>(Math.Min(limit, 2048));
        var discardingOversize = false;
        var oversize = false;
        var bytesRead = 0;
        try
        {
            while (position > earliest && reversedLines.Count < limit)
            {
                var readStart = Math.Max(earliest, position - buffer.Length);
                var count = checked((int)(position - readStart));
                stream.Position = readStart;
                var read = 0;
                while (read < count)
                {
                    var next = stream.Read(buffer, read, count - read);
                    if (next == 0)
                    {
                        break;
                    }

                    read += next;
                }

                bytesRead += read;
                for (var index = read - 1; index >= 0 && reversedLines.Count < limit; index--)
                {
                    if (buffer[index] == (byte)'\n')
                    {
                        if (discardingOversize)
                        {
                            discardingOversize = false;
                        }
                        else
                        {
                            AddReversedLine(reversedLine, reversedLines);
                        }
                    }
                    else if (!discardingOversize)
                    {
                        reversedLine.Add(buffer[index]);
                        if (reversedLine.Count > lineCeiling)
                        {
                            reversedLine.Clear();
                            discardingOversize = true;
                            oversize = true;
                        }
                    }
                }

                position = readStart;
            }

            // A window that starts mid-record must drop that oldest partial
            // record; at file start the first line is complete even without a
            // trailing newline.
            if (position == 0 && !discardingOversize && reversedLines.Count < limit)
            {
                AddReversedLine(reversedLine, reversedLines);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        reversedLines.Reverse();
        return new TailReadResult(reversedLines, window, earliest > 0, oversize, bytesRead);
    }

    private static void AddReversedLine(List<byte> reversedLine, List<string> lines)
    {
        if (reversedLine.Count == 0)
        {
            return;
        }

        var span = CollectionsMarshal.AsSpan(reversedLine);
        span.Reverse();
        if (span.Length > 0 && span[^1] == (byte)'\r')
        {
            span = span[..^1];
        }

        var line = Encoding.UTF8.GetString(span).TrimStart('\uFEFF').Trim();
        if (line.Length > 0)
        {
            lines.Add(line);
        }

        reversedLine.Clear();
    }
}
