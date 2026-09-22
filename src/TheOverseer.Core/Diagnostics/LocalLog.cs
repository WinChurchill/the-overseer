using System.Text;

namespace TheOverseer.Core.Diagnostics;

/// <summary>
/// Bounded rolling metadata log. Only stable codes, counters and numbers are
/// written by callers; control characters are sanitized and fields are bounded.
/// Sanitization is not redaction: callers must never supply sensitive text.
/// Failure to log never throws into the monitor.
/// </summary>
public sealed class LocalLog : IMonitorLog, IDisposable
{
    private const int MaximumCodeChars = 64;
    private const int MaximumDetailChars = 240;

    private readonly string _path;
    private readonly int _maxBytes;
    private readonly MonitorLogLevel _minimumLevel;
    private readonly object _gate = new();
    private bool _disposed;

    public LocalLog(string path, int maxBytes, MonitorLogLevel minimumLevel)
    {
        _path = path;
        _maxBytes = Math.Clamp(maxBytes, 64 * 1024, 16 * 1024 * 1024);
        _minimumLevel = minimumLevel;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (IOException)
        {
            IsDegraded = true;
        }
        catch (UnauthorizedAccessException)
        {
            IsDegraded = true;
        }
    }

    public bool IsDegraded { get; private set; }

    public static LocalLog? TryCreate(string path, int maxBytes, string level)
    {
        try
        {
            return new LocalLog(path, maxBytes, ParseLevel(level));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static MonitorLogLevel ParseLevel(string level) => level?.Trim().ToLowerInvariant() switch
    {
        "debug" => MonitorLogLevel.Debug,
        "warn" or "warning" => MonitorLogLevel.Warning,
        "error" => MonitorLogLevel.Error,
        _ => MonitorLogLevel.Info,
    };

    public void Write(MonitorLogLevel level, string code, string detail = "")
    {
        if (_disposed || level < _minimumLevel)
        {
            return;
        }

        var line = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) +
                   " " + level.ToString().ToLowerInvariant() +
                   " " + Sanitize(code, MaximumCodeChars);

        var safeDetail = Sanitize(detail, MaximumDetailChars);
        if (safeDetail.Length > 0)
        {
            line += " " + safeDetail;
        }

        line += Environment.NewLine;
        var bytes = Encoding.UTF8.GetByteCount(line);

        try
        {
            lock (_gate)
            {
                Roll(bytes + Encoding.UTF8.GetPreamble().Length);
                File.AppendAllText(_path, line, Encoding.UTF8);
                _ = bytes;
            }
        }
        catch (IOException)
        {
            IsDegraded = true;
        }
        catch (UnauthorizedAccessException)
        {
            IsDegraded = true;
        }
    }

    public void Dispose() => _disposed = true;

    private void Roll(int additionalBytes)
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length + additionalBytes <= _maxBytes)
        {
            return;
        }

        var backup = _path + ".1";
        // A failed rotation reaches Write's catch and skips the append.
        File.Move(_path, backup, overwrite: true);
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (builder.Length >= maximumLength)
            {
                break;
            }

            // Path separators are deliberately excluded so a caller mistake
            // cannot write a filesystem path into the log.
            var allowed = char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.' or ',' or ':' or '=';
            builder.Append(allowed ? character : '?');
        }

        return builder.ToString();
    }
}
