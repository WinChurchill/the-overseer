using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Optional read-only enrichment through the Windows system SQLite library.
/// Adapted from codex-monitor-hud
/// src-dotnet/CodexMonitorHud.App/WindowsSessionActivitySource.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT) with a hard query
/// deadline through the SQLite progress handler, path containment validation,
/// and explicit availability results: a missing library, missing schema or lock
/// is never reported as an empty success.
///
/// Only non-content metadata columns are selected. Timestamps are activity
/// hints: they can locate candidates but never prove execution state.
/// </summary>
internal sealed class WindowsSessionActivitySource : IRuntimeActivitySource
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteBusyTimeoutMs = 75;
    private const int QueryDeadlineMs = 250;
    private const int ProgressHandlerOps = 2000;

    private static readonly Native.ProgressHandler ProgressHandlerInstance = OnProgress;
    private static long _deadlineTicks;

    public RuntimeActivityResult Read(SessionProfile profile, DateTimeOffset cutoff, int maximumRows)
    {
        var databasePath = profile.StateDatabasePath;
        if (!OperatingSystem.IsWindows())
        {
            return RuntimeActivityResult.Unavailable("platform-unsupported");
        }

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return RuntimeActivityResult.Unavailable("not-configured");
        }

        if (!File.Exists(databasePath))
        {
            return RuntimeActivityResult.Unavailable("database-missing");
        }

        if (!IsUnderRoot(profile, databasePath))
        {
            return RuntimeActivityResult.Unavailable("path-outside-root");
        }

        var database = IntPtr.Zero;
        var statement = IntPtr.Zero;
        try
        {
            if (Native.sqlite3_open_v2(Utf8(databasePath), out database, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk ||
                database == IntPtr.Zero)
            {
                return RuntimeActivityResult.Unavailable("open-failed");
            }

            _ = Native.sqlite3_busy_timeout(database, SqliteBusyTimeoutMs);
            Interlocked.Exchange(ref _deadlineTicks, Stopwatch.GetTimestamp() + (QueryDeadlineMs * Stopwatch.Frequency / 1000));
            Native.sqlite3_progress_handler(database, ProgressHandlerOps, ProgressHandlerInstance, IntPtr.Zero);

            var cutoffMilliseconds = cutoff.ToUnixTimeMilliseconds();
            var rowLimit = Math.Clamp(maximumRows, 1, 128);
            // Agent-created chats and ChatGPT handoffs are user-facing peers.
            // Genuine subagents/reviews remain excluded; timestamps are hints only.
            var sql = "SELECT id, rollout_path, updated_at_ms FROM threads " +
                      "WHERE archived = 0 AND thread_source IN ('user', 'agent_created_thread', 'chatgpt_handoff') AND updated_at_ms >= " +
                      cutoffMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      " ORDER BY updated_at_ms DESC LIMIT " +
                      rowLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Native.sqlite3_prepare_v2(database, Utf8(sql), -1, out statement, IntPtr.Zero) != SqliteOk ||
                statement == IntPtr.Zero)
            {
                return RuntimeActivityResult.Unavailable("schema-unavailable");
            }

            var results = new List<RuntimeActivityHint>(rowLimit);
            while (true)
            {
                var step = Native.sqlite3_step(statement);
                if (step == SqliteDone)
                {
                    break;
                }

                if (step == 9 /* SQLITE_ABORT from the deadline handler */)
                {
                    return RuntimeActivityResult.Unavailable("deadline-exceeded");
                }

                if (step != SqliteRow)
                {
                    return RuntimeActivityResult.Unavailable("query-failed");
                }

                var sessionId = ReadText(statement, 0);
                var rolloutPath = ReadText(statement, 1);
                var updatedAtMilliseconds = Native.sqlite3_column_int64(statement, 2);
                if (sessionId.Length == 0 || rolloutPath.Length == 0 || updatedAtMilliseconds <= 0)
                {
                    continue;
                }

                if (!IsUnderRoot(profile, rolloutPath))
                {
                    continue;
                }

                DateTimeOffset updatedAt;
                try
                {
                    updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMilliseconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                results.Add(new RuntimeActivityHint(profile.Id, sessionId, rolloutPath, updatedAt));
            }

            return new RuntimeActivityResult(RuntimeActivityAvailability.Available, results, "ok");
        }
        catch (DllNotFoundException)
        {
            return RuntimeActivityResult.Unavailable("sqlite-library-missing");
        }
        catch (EntryPointNotFoundException)
        {
            return RuntimeActivityResult.Unavailable("sqlite-library-incompatible");
        }
        catch (BadImageFormatException)
        {
            return RuntimeActivityResult.Unavailable("sqlite-library-incompatible");
        }
        catch (IOException)
        {
            return RuntimeActivityResult.Unavailable("io-error");
        }
        catch (UnauthorizedAccessException)
        {
            return RuntimeActivityResult.Unavailable("access-denied");
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                _ = Native.sqlite3_finalize(statement);
            }

            if (database != IntPtr.Zero)
            {
                Native.sqlite3_progress_handler(database, 0, null!, IntPtr.Zero);
                _ = Native.sqlite3_close_v2(database);
            }
        }
    }

    private static int OnProgress(IntPtr argument) =>
        Stopwatch.GetTimestamp() > Interlocked.Read(ref _deadlineTicks) ? 1 : 0;

    private static bool IsUnderRoot(SessionProfile profile, string path)
    {
        try
        {
            var root = Path.GetFullPath(profile.RootPath);
            var full = Path.GetFullPath(path);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');

    private static string ReadText(IntPtr statement, int column)
    {
        var pointer = Native.sqlite3_column_text(statement, column);
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }

    private static class Native
    {
        internal delegate int ProgressHandler(IntPtr argument);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sqlite3_progress_handler(IntPtr database, int operations, ProgressHandler handler, IntPtr argument);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(IntPtr database);
    }
}
