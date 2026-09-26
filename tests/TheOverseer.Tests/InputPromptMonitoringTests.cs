using System.Text.Json;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class InputPromptMonitoringTests
{
    [Theory]
    [InlineData("request_user_input", true)]
    [InlineData("functions.request_user_input", true)]
    [InlineData("request_user_input_async", false)]
    [InlineData("functions.request_user_input_async", false)]
    public void DurableInputToolRaisesAttentionWithoutReadingQuestionText(string tool, bool blocking)
    {
        using var fixture = new Fixture();
        fixture.Append("function_call", "question", tool);
        var requested = fixture.Monitor.Refresh();
        var task = Assert.Single(requested.Tasks);
        Assert.True(task.NeedsHuman);
        Assert.Equal(blocking ? AgentTaskState.NeedsInput : AgentTaskState.Working, task.State);
        Assert.Contains(requested.Transitions, edge => edge.Kind == AttentionEventKind.InputRequired);
        Assert.Equal(1, requested.Counts.NeedsHuman.Value);
        Assert.Equal(CapabilitySupport.LiveObserved, requested.Capabilities.InputRequests.Support);
        Assert.DoesNotContain("PRIVATE_QUESTION", JsonSerializer.Serialize(requested));

        // An unrelated tool completion cannot answer this question.
        fixture.Append("function_call_output", "unrelated");
        Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);

        fixture.Append("function_call_output", "question");
        var afterOutput = fixture.Monitor.Refresh();
        Assert.Equal(!blocking, Assert.Single(afterOutput.Tasks).NeedsHuman);
        Assert.DoesNotContain(afterOutput.Transitions, edge => edge.Kind == AttentionEventKind.InputRequired);
    }

    [Fact]
    public void HydratingAnAnsweredInputToolDoesNotReplayAnAlert()
    {
        using var fixture = new Fixture();
        fixture.Append("function_call", "question", "request_user_input");
        fixture.Append("function_call_output", "question");
        using var fresh = fixture.CreateMonitor();
        var snapshot = fresh.Refresh();
        Assert.False(Assert.Single(snapshot.Tasks).NeedsHuman);
        Assert.DoesNotContain(snapshot.Transitions, edge => edge.Kind == AttentionEventKind.InputRequired);
    }

    [Fact]
    public void MentioningInputInsideAnOpaqueToolDoesNotInventAttention()
    {
        using var fixture = new Fixture();
        fixture.Append("function_call", "other", "exec");
        var snapshot = fixture.Monitor.Refresh();
        Assert.False(Assert.Single(snapshot.Tasks).NeedsHuman);
        Assert.DoesNotContain(snapshot.Transitions, edge => edge.Kind == AttentionEventKind.InputRequired);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SyntheticCodexHome _home = new();
        private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(Records.BaseSeconds));
        private readonly string _path;
        public CodexMonitor Monitor { get; }
        public Fixture()
        {
            _path = _home.WriteRollout("01a0c700-0000-0000-0000-000000000001", new[]
            {
                Records.SessionMeta("01a0c700-0000-0000-0000-000000000001"), Records.TaskStarted("turn"),
            });
            Monitor = CreateMonitor();
            Monitor.Refresh();
        }
        public CodexMonitor CreateMonitor() => new(MonitorOptions.Default, new[]
        {
            new SessionProfile("codex", "Codex", _home.Root, _home.SessionsRoot, _home.IndexPath, _home.StateDatabasePath),
        }, timeProvider: _clock);
        public void Append(string type, string callId, string? name = null)
        {
            _clock.AdvanceSeconds(1);
            SyntheticCodexHome.Append(_path, new[] { JsonSerializer.Serialize(new
            {
                type = "response_item", timestamp = _clock.GetUtcNow(),
                payload = new { type, name, call_id = callId,
                    arguments = "PRIVATE_QUESTION request_user_input options answers",
                    output = "PRIVATE_QUESTION response accepted", },
            }) });
        }
        public void Dispose() { Monitor.Dispose(); _home.Dispose(); }
    }
}
