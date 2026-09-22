using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TheOverseer.Core.Monitoring.Sessions;

/// <summary>
/// Watcher hint source. Adapted from codex-monitor-hud
/// src-dotnet/CodexMonitorHud.Core/Sessions/SessionChangeTracker.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT). Hints are bounded
/// and overflow is reported instead of being silently dropped; watcher creation
/// failure is a typed diagnostic and never fatal because known files are polled
/// and reconciled independently.
/// </summary>
public sealed class SessionChangeTracker : IDisposable
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, byte> _hints;
    private readonly Channel<bool> _signal;
    private readonly int _maxHints;
    private int _overflowed;
    private int _structural;

    public SessionChangeTracker(string watchRoot, int maxHints)
    {
        _maxHints = Math.Max(16, maxHints);
        _hints = new ConcurrentDictionary<string, byte>(PathComparer);
        _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        if (string.IsNullOrWhiteSpace(watchRoot) || !Directory.Exists(watchRoot))
        {
            FailureCode = "watch-root-missing";
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(watchRoot, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnStructural;
            _watcher.Deleted += OnStructural;
            _watcher.Renamed += OnStructural;
            _watcher.Error += OnError;
            FailureCode = null;
        }
        catch (ArgumentException)
        {
            FailureCode = "watch-root-invalid";
        }
        catch (IOException)
        {
            FailureCode = "watch-creation-failed";
        }
        catch (UnauthorizedAccessException)
        {
            FailureCode = "watch-access-denied";
        }
        catch (PlatformNotSupportedException)
        {
            FailureCode = "watch-unsupported";
        }
    }

    public bool Available => _watcher is not null;

    public string? FailureCode { get; private set; }

    public bool HasPendingHints => !_hints.IsEmpty;

    public IReadOnlyList<string> DrainHints()
    {
        if (_hints.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>(Math.Min(_hints.Count, _maxHints));
        foreach (var pair in _hints)
        {
            if (paths.Count >= _maxHints)
            {
                break;
            }

            if (_hints.TryRemove(pair.Key, out _))
            {
                paths.Add(pair.Key);
            }
        }

        return paths;
    }

    public bool ConsumeOverflow() => Interlocked.Exchange(ref _overflowed, 0) == 1;

    public bool ConsumeStructuralChange() => Interlocked.Exchange(ref _structural, 0) == 1;

    public void RequestReconcile()
    {
        Interlocked.Exchange(ref _structural, 1);
        Signal();
    }

    /// <summary>Waits for a hint or the timeout; cancellation is never swallowed.</summary>
    public async Task<bool> WaitForChangeAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (_watcher is null)
        {
            await Task.Delay(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
            return HasPendingHints;
        }

        var wait = _signal.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var delay = Task.Delay(timeout, timeProvider, cancellationToken);
        var completed = await Task.WhenAny(wait, delay).ConfigureAwait(false);
        if (completed == wait)
        {
            _ = wait.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            if (await wait.ConfigureAwait(false))
            {
                while (_signal.Reader.TryRead(out _))
                {
                }

                return true;
            }

            return false;
        }

        _ = wait.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        return HasPendingHints;
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnStructural;
            _watcher.Deleted -= OnStructural;
            _watcher.Renamed -= OnStructural;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }

        _signal.Writer.TryComplete();
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        Track(args.FullPath);
        Signal();
    }

    private void OnStructural(object sender, FileSystemEventArgs args)
    {
        Track(args.FullPath);
        Interlocked.Exchange(ref _structural, 1);
        Signal();
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        Interlocked.Exchange(ref _overflowed, 1);
        Interlocked.Exchange(ref _structural, 1);
        Signal();
    }

    private void Track(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (_hints.Count >= _maxHints)
        {
            Interlocked.Exchange(ref _overflowed, 1);
            return;
        }

        _hints.TryAdd(path, 0);
    }

    private void Signal() => _signal.Writer.TryWrite(true);
}
