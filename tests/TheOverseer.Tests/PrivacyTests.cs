using System.Text.Json;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class PrivacyTests : IDisposable
{
    private const string SentinelPrompt = "SENTINEL-PROMPT-9f3a";
    private const string SentinelQuestion = "SENTINEL-QUESTION-4c1b";
    private const string SentinelCommand = "SENTINEL-COMMAND-77de";
    private const string SentinelOutput = "SENTINEL-OUTPUT-2b8e";
    private const string SentinelCredential = "SENTINEL-CREDENTIAL-1234abcd";
    private const string SentinelAssistant = "SENTINEL-ASSISTANT-5a6f";

    private readonly SyntheticCodexHome _home = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    public void Dispose() => _home.Dispose();

    [Fact]
    public void RecordedTextNeverReachesSnapshotsOrLogs()
    {
        var id = "01a0c800-0000-0000-0000-000000000001";
        var sessionsRoot = _home.SessionsRoot;
        var dateDirectory = Path.Combine(sessionsRoot, "2026", "01", "02");
        Directory.CreateDirectory(dateDirectory);
        var rollout = Path.Combine(dateDirectory, "rollout-2026-01-02T03-04-05-" + id + ".jsonl");

        File.WriteAllLines(rollout, new[]
        {
            Records.SessionMeta(id),
            "{\"type\":\"future_record\",\"payload\":{\"credential\":\"" + SentinelCredential + "\"}}",
            Records.TaskStarted("turn-1"),
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"turn_id\":\"turn-1\",\"message\":\"" + SentinelPrompt + "\"}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"turn_id\":\"turn-1\",\"message\":\"" + SentinelAssistant + "\"}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"request_user_input\",\"turn_id\":\"turn-1\",\"call_id\":\"q\",\"isBlocking\":true,\"question\":\"" + SentinelQuestion + "\"}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"exec_approval_request\",\"turn_id\":\"turn-1\",\"call_id\":\"a\",\"command\":\"" + SentinelCommand + "\"}}",
            Records.ItemCompleted("turn-1", "CommandExecution", command: SentinelCommand),
            Records.CustomToolCallOutput("turn-1", "call-1", output: SentinelOutput),
        });

        var logDirectory = Path.Combine(_home.Root, "logs");
        using var log = new LocalLog(Path.Combine(logDirectory, "overseer.log"), 1024 * 1024, MonitorLogLevel.Debug);
        var profile = new SessionProfile("codex", "Codex", _home.Root, sessionsRoot, _home.IndexPath, _home.StateDatabasePath);
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { profile }, null, log, _clock);

        var snapshot = monitor.Refresh();
        log.Write(MonitorLogLevel.Info, "privacy-check", "tasks=" + snapshot.Tasks.Count);

        var serialized = JsonSerializer.Serialize(snapshot);
        foreach (var sentinel in new[] { SentinelPrompt, SentinelQuestion, SentinelCommand, SentinelOutput, SentinelCredential, SentinelAssistant })
        {
            Assert.DoesNotContain(sentinel, serialized, StringComparison.Ordinal);
        }

        var logText = File.Exists(Path.Combine(logDirectory, "overseer.log"))
            ? File.ReadAllText(Path.Combine(logDirectory, "overseer.log"))
            : string.Empty;
        foreach (var sentinel in new[] { SentinelPrompt, SentinelQuestion, SentinelCommand, SentinelOutput, SentinelCredential, SentinelAssistant })
        {
            Assert.DoesNotContain(sentinel, logText, StringComparison.Ordinal);
        }

        // The task itself is still normalized from structural evidence only.
        Assert.True(snapshot.Tasks.Count >= 1);
    }

    [Fact]
    public void SnapshotNeverCarriesRawRecordFields()
    {
        var id = "01a0c801-0000-0000-0000-000000000002";
        _home.WriteRollout(id, new[]
        {
            Records.SessionMeta(id),
            Records.TaskStarted("turn-1"),
            Records.ItemCompleted("turn-1", "CommandExecution", command: "dotnet test --filter " + SentinelCommand),
        });

        var profile = new SessionProfile("codex", "Codex", _home.Root, _home.SessionsRoot, _home.IndexPath, _home.StateDatabasePath);
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { profile }, null, null, _clock);
        var snapshot = monitor.Refresh();

        var task = snapshot.Tasks[0];
        Assert.Equal("Running tests", task.Action.Label);
        Assert.Equal("test-runner", task.Action.DetailCode);
        Assert.DoesNotContain(SentinelCommand, JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void LogSanitizesControlCharactersAndInjectedPaths()
    {
        var path = Path.Combine(_home.Root, "logs", "sanitize.log");
        using (var log = new LocalLog(path, 256 * 1024, MonitorLogLevel.Debug))
        {
            log.Write(MonitorLogLevel.Info, "code", "first" + Environment.NewLine + "injected C:\\Users\\someone\\secret.txt");
        }

        var lines = File.ReadAllLines(path);
        Assert.Single(lines);
        Assert.Contains("?injected", lines[0]);
        // The sanitizer guarantee is structural: one line, no path separators,
        // bounded length, so a path-shaped value cannot be read as a path.
        Assert.DoesNotContain('\\', lines[0]);
        Assert.DoesNotContain('/', lines[0]);
        Assert.True(lines[0].Length < 300);
    }

    [Fact]
    public void LogLevelFilteringAndBoundedRollover()
    {
        var path = Path.Combine(_home.Root, "logs", "roll.log");
        using (var log = new LocalLog(path, 64 * 1024, MonitorLogLevel.Warning))
        {
            log.Write(MonitorLogLevel.Info, "info-code", "should-not-appear");
            log.Write(MonitorLogLevel.Error, "error-code", "appears");
        }

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("should-not-appear", text, StringComparison.Ordinal);
        Assert.Contains("error-code", text, StringComparison.Ordinal);

        using (var rolling = new LocalLog(path, 64 * 1024, MonitorLogLevel.Debug))
        {
            var filler = new string('f', 200);
            for (var index = 0; index < 2000; index++)
            {
                rolling.Write(MonitorLogLevel.Info, "fill-" + index, filler);
            }

            Assert.False(rolling.IsDegraded);
        }

        Assert.True(File.Exists(path + ".1"), "a bounded log must roll to a single backup");
        Assert.True(new FileInfo(path).Length <= 128 * 1024, "active log must stay bounded");
    }
}
