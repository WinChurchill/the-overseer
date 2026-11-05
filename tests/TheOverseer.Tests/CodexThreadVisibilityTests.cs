using System.Runtime.InteropServices;
using System.Text.Json;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class CodexThreadVisibilityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly SyntheticCodexHome _home = new();
    private SessionProfile Profile => new("codex", "Codex", _home.Root, _home.SessionsRoot, _home.IndexPath, _home.StateDatabasePath);

    public void Dispose() => _home.Dispose();

    private static IRuntimeActivitySource CreateSource() => (IRuntimeActivitySource)Activator.CreateInstance(
        typeof(BridgeMonitorAdapter).Assembly.GetType("TheOverseer.App.Monitoring.WindowsSessionActivitySource", throwOnError: true)!,
        nonPublic: true)!;

    [Theory]
    [InlineData("user", true)]
    [InlineData("agent_created_thread", true)]
    [InlineData("chatgpt_handoff", true)]
    [InlineData("subagent", false)]
    [InlineData("guardian_review", false)]
    [InlineData("unknown_future_source", false)]
    public void RuntimeDiscoveryIncludesUserFacingOriginsAndExcludesInternalOrigins(string source, bool expected)
    {
        WriteDatabase(("visible-chat", source, Now.ToUnixTimeMilliseconds(), 0));
        var result = CreateSource().Read(Profile, Now.AddMinutes(-15), 64);
        Assert.Equal(RuntimeActivityAvailability.Available, result.Availability);
        Assert.Equal(expected ? 1 : 0, result.Hints.Count);
        if (expected) Assert.Equal("visible-chat", Assert.Single(result.Hints).ConversationId);
    }

    [Fact]
    public void RowLimitIsAppliedAfterSourceArchiveAndCutoffFilters()
    {
        var stamp = Now.ToUnixTimeMilliseconds();
        WriteDatabase(("review", "guardian_review", stamp + 4, 0),
            ("child", "subagent", stamp + 3, 0), ("archived", "agent_created_thread", stamp + 2, 1),
            ("handoff", "chatgpt_handoff", stamp + 1, 0), ("created", "agent_created_thread", stamp, 0),
            ("old", "user", stamp - 60_000, 0));
        var source = CreateSource();
        var limited = source.Read(Profile, Now.AddSeconds(-1), 1);
        Assert.Equal(RuntimeActivityAvailability.Available, limited.Availability);
        Assert.Equal("handoff", Assert.Single(limited.Hints).ConversationId);
        Assert.Equal(new[] { "handoff", "created" },
            source.Read(Profile, Now.AddSeconds(-1), 64).Hints.Select(item => item.ConversationId));
    }

    [Fact]
    public void EnrichmentRemainsReadOnlyAndReturnsNoContent()
    {
        WriteDatabase(("created", "agent_created_thread", Now.ToUnixTimeMilliseconds(), 0));
        var before = File.ReadAllBytes(_home.StateDatabasePath);
        var result = CreateSource().Read(Profile, Now.AddSeconds(-1), 64);
        Assert.Single(result.Hints);
        Assert.Equal(before, File.ReadAllBytes(_home.StateDatabasePath));
        Assert.DoesNotContain("PRIVATE_CONTENT", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private void WriteDatabase(params (string Id, string Source, long UpdatedAt, int Archived)[] rows)
    {
        Assert.Equal(0, Native.sqlite3_open_v2(_home.StateDatabasePath, out var database, 6, null));
        try
        {
            Execute(database, "CREATE TABLE threads (id TEXT, rollout_path TEXT, updated_at_ms INTEGER, thread_source TEXT, archived INTEGER, private_content TEXT)");
            foreach (var row in rows)
            {
                var path = _home.WriteRollout(row.Id, Array.Empty<string>());
                Execute(database, $"INSERT INTO threads VALUES ('{Quote(row.Id)}','{Quote(path)}',{row.UpdatedAt},'{Quote(row.Source)}',{row.Archived},'PRIVATE_CONTENT')");
            }
        }
        finally { Assert.Equal(0, Native.sqlite3_close(database)); }
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void Execute(IntPtr database, string sql)
    {
        var code = Native.sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) Native.sqlite3_free(error);
        Assert.Equal(0, code);
    }

    private static class Native
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database,
            int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string? vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            IntPtr callback, IntPtr parameter, out IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr memory);
    }
}
