using System.Text.Json;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Core.Configuration;
using TheOverseer.UI.ViewModels;
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
        public void Input(string callId, int count = 1) => Record(new
        {
            type = "function_call", name = "request_user_input_async", call_id = callId,
            arguments = JsonSerializer.Serialize(new { questions = Enumerable.Range(0, count).Select(_ => new { title = "PRIVATE_QUESTION" }) }),
        });
        public void Reply(string callId, int index = 0, string role = "user", string prefix = "") => Record(new
        {
            type = "message", role,
            internal_chat_message_metadata_passthrough = new { turn_id = "turn" },
            content = new[] { new { type = "input_text", text = prefix + Envelope(callId, index) } },
        });
        public void Record(object payload)
        {
            _clock.AdvanceSeconds(1);
            SyntheticCodexHome.Append(_path, new[] { JsonSerializer.Serialize(new { type = "response_item", timestamp = _clock.GetUtcNow(), payload }) });
        }
        public void Dispose() { Monitor.Dispose(); _home.Dispose(); }
    }

    private static string Envelope(string callId, int index) => "<send_user_message_question_reply>\n" +
        JsonSerializer.Serialize(new[] { new { questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", callId, index }), question = "PRIVATE_QUESTION", answer = "PRIVATE_ANSWER" } }) +
        "\n</send_user_message_question_reply>";

    [Fact]
    public void AsyncAnswerClearsAppliedHudWithoutWaitingForTurnEnd()
    {
        using var fixture = new Fixture();
        fixture.Input("call-question");
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(fixture.Monitor.Refresh());
        Assert.Equal("Needs you", vm.StatusHeadline);
        fixture.Append("function_call_output", "call-question"); // acceptance acknowledgement is not the answer
        Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
        fixture.Reply("call-question");
        var answered = fixture.Monitor.Refresh();
        Assert.False(Assert.Single(answered.Tasks).NeedsHuman);
        Assert.Equal(AgentTaskState.Working, Assert.Single(answered.Tasks).State);
        vm.ApplySnapshot(answered);
        Assert.Equal("Working", vm.StatusHeadline);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(answered));
        Assert.Empty(answered.Transitions);
    }

    [Fact]
    public void PartialDuplicateAndUnrelatedAnswersDoNotClearOtherQuestions()
    {
        using var fixture = new Fixture();
        fixture.Input("first", 2);
        fixture.Input("second");
        fixture.Monitor.Refresh();
        foreach (var (call, index) in new[] { ("unknown", 0), ("first", 3), ("first", 0), ("first", 0), ("second", 0) })
        {
            fixture.Reply(call, index);
            Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
        }
        fixture.Reply("first", 1);
        Assert.False(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
    }

    [Theory]
    [InlineData("assistant", "")]
    [InlineData("tool", "")]
    [InlineData("user", "Here is an example: ")]
    public void QuotedOrNonUserEnvelopesCannotResolveAttention(string role, string prefix)
    {
        using var fixture = new Fixture();
        fixture.Input("call-question");
        fixture.Monitor.Refresh();
        fixture.Reply("call-question", role: role, prefix: prefix);
        Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
    }

    [Fact]
    public void HydratedAsyncReplyDoesNotReplayAttention()
    {
        using var fixture = new Fixture();
        fixture.Input("call-question");
        fixture.Reply("call-question");
        using var monitor = fixture.CreateMonitor();
        var snapshot = monitor.Refresh();
        Assert.False(Assert.Single(snapshot.Tasks).NeedsHuman);
        Assert.Empty(snapshot.Transitions);
    }

    [Fact]
    public void AnswerCannotClearUnknownQuestionCount()
    {
        using var fixture = new Fixture();
        fixture.Append("function_call", "unknown-count", "request_user_input_async");
        fixture.Monitor.Refresh();
        fixture.Reply("unknown-count");
        Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
    }

    [Fact]
    public void BatchedReplyResolvesOnlyItsReferencedCallsAndDropsPrivateText()
    {
        using var fixture = new Fixture();
        fixture.Input("a");
        fixture.Input("b");
        fixture.Monitor.Refresh();
        var text = "<send_user_message_question_reply>" + JsonSerializer.Serialize(new[] { "a", "b" }.Select(call => new
        {
            questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", call, 0 }), answer = "PRIVATE_ANSWER",
        })) + "</send_user_message_question_reply>";
        var payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } };
        fixture.Record(payload);
        Assert.False(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
        var parsed = CodexEventParser.ParseLine(JsonSerializer.Serialize(new { type = "response_item", payload }),
            new ParseContext("codex", "session", 1, 0, 1, 1, false, DateTimeOffset.UtcNow, 1024 * 1024));
        Assert.Equal(2, parsed.Event!.InputAnswers.Count);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(parsed));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"questionItemId\":\"[]\",\"answer\":\"x\"}]")]
    [InlineData("[{\"questionItemId\":false,\"answer\":\"x\"}]")]
    public void MalformedReplyCannotClearAttention(string body)
    {
        using var fixture = new Fixture();
        fixture.Input("a");
        fixture.Monitor.Refresh();
        fixture.Record(new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "<send_user_message_question_reply>" + body + "</send_user_message_question_reply>" } } });
        Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
    }

    [Fact]
    public void OversizedReplyCannotClearAttention()
    {
        using var fixture = new Fixture();
        fixture.Input("a");
        fixture.Monitor.Refresh();
        fixture.Record(new { type = "message", role = "user", content = new[] { new { type = "input_text", text = Envelope("a", 0).Replace("PRIVATE_ANSWER", new string('x', 65536)) } } });
        Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
    }

    [Theory]
    [InlineData("{\"role\":false,\"type\":\"message\"}")]
    [InlineData("{\"role\":\"user\",\"type\":\"message\",\"content\":[{\"type\":23,\"text\":\"x\"}]}")]
    public void MalformedMessageShapesDoNotThrow(string payload)
    {
        var result = CodexEventParser.ParseLine("{\"type\":\"response_item\",\"payload\":" + payload + "}",
            new ParseContext("codex", "session", 1, 0, 1, 1, false, DateTimeOffset.UtcNow, 1024 * 1024));
        Assert.NotEqual(RawCodexEventKind.InputResolved, result.Event?.Kind);
    }
}
