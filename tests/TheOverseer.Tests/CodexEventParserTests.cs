using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class CodexEventParserTests
{
    private static ParseContext Context(bool hydration = false, int order = 1) => new(
        "codex",
        "conversation-1",
        1,
        100,
        order,
        0,
        hydration,
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        1024 * 1024);

    private static RawCodexEvent Parse(string line)
    {
        var result = CodexEventParser.ParseLine(line, Context());
        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.NotNull(result.Event);
        return result.Event!;
    }

    [Fact]
    public void LifecycleRecordsMapToStartsCompletionsAndAborts()
    {
        var started = Parse(Records.TaskStarted("turn-1"));
        Assert.Equal(RawCodexEventKind.TaskStarted, started.Kind);
        Assert.Equal("turn-1", started.TurnId);
        Assert.Equal(TurnOutcome.Completed, Parse(Records.TaskComplete("turn-1")).Outcome);
        Assert.Equal(TurnOutcome.Aborted, Parse(Records.TurnAborted("turn-1")).Outcome);
    }

    [Fact]
    public void CompletionDoesNotRequireAssistantText()
    {
        var completed = Parse(Records.TaskComplete("turn-1", lastAgentMessage: null));
        Assert.Equal(RawCodexEventKind.TaskCompleted, completed.Kind);
    }

    [Fact]
    public void ToolCallsAndOutputsCarryCorrelationIdentityOnly()
    {
        var call = Parse(Records.CustomToolCall("turn-1", "call-1"));
        Assert.Equal(RawCodexEventKind.ToolCallCompleted, call.Kind);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal(ToolCallCategory.OpaqueWrapper, call.Tool);
        Assert.Equal(AgentActionKind.RunningCommand, call.Action);
        Assert.DoesNotContain("REMOVED", call.ActionDetailCode);

        var output = Parse(Records.CustomToolCallOutput("turn-1", "call-1"));
        Assert.Equal("call-1", output.CallId);
        Assert.Equal(RawCodexEventKind.ToolCallCompleted, output.Kind);
    }

    [Fact]
    public void ItemCompletedMapsStructuredCategories()
    {
        Assert.Equal(AgentActionKind.EditingFiles, Parse(Records.ItemCompleted("turn-1", "FileChange")).Action);
        Assert.Equal(AgentActionKind.Searching, Parse(Records.ItemCompleted("turn-1", "Extension", extensionKind: "web.search")).Action);
        Assert.Equal(AgentActionKind.Searching, Parse(Records.ItemCompleted("turn-1", "McpToolCall", mcpTool: "search")).Action);
        Assert.Equal(AgentActionKind.Thinking, Parse(Records.ItemCompleted("turn-1", "Reasoning")).Action);
        Assert.Equal(AgentActionKind.Thinking, Parse(Records.ItemCompleted("turn-1", "AgentMessage")).Action);
        Assert.Null(Parse(Records.ItemCompleted("turn-1", "UserMessage")).Action);
    }

    [Theory]
    [InlineData("dotnet test", AgentActionKind.RunningTests)]
    [InlineData("npm test", AgentActionKind.RunningTests)]
    [InlineData("pytest -q", AgentActionKind.RunningTests)]
    [InlineData("dotnet build", AgentActionKind.RunningCommand)]
    [InlineData("rg --files", AgentActionKind.Searching)]
    [InlineData("Get-Content src/file.cs", AgentActionKind.ReadingFiles)]
    [InlineData("./some-tool --flag", AgentActionKind.RunningCommand)]
    public void CommandHeuristicIsNarrowAndBounded(string command, AgentActionKind expected)
    {
        var action = Parse(Records.ItemCompleted("turn-1", "CommandExecution", command: command)).Action;
        Assert.Equal(expected, action);
    }

    [Fact]
    public void MalformedAndUnknownRecordsAreReportedNotGuessed()
    {
        Assert.Equal(ParseOutcome.Malformed, CodexEventParser.ParseLine(Records.Malformed(), Context()).Outcome);
        Assert.Equal(ParseOutcome.UnknownSchema, CodexEventParser.ParseLine("{\"type\":\"future_record\",\"payload\":{\"type\":\"something_new\"}}", Context()).Outcome);
        Assert.Equal(ParseOutcome.Ignored, CodexEventParser.ParseLine("{\"type\":\"future_record\"}", Context()).Outcome);
        Assert.Equal(ParseOutcome.Malformed, CodexEventParser.ParseLine("not json at all", Context()).Outcome);
        Assert.Equal(ParseOutcome.Malformed, CodexEventParser.ParseLine("{\"broken\"", Context()).Outcome);
        Assert.Equal(ParseOutcome.Ignored, CodexEventParser.ParseLine("{\"type\":\"event_msg\"}", Context()).Outcome);
    }

    [Fact]
    public void OversizeRecordIsRejectedBeforeParsing()
    {
        var context = new ParseContext("codex", "conversation-1", 1, 0, 1, 0, false, DateTimeOffset.UtcNow, 128);
        var result = CodexEventParser.ParseLine(new string('x', 5000), in context);
        Assert.Equal(ParseOutcome.Oversize, result.Outcome);
    }

    [Fact]
    public void ProseMentionsOfAttentionKeywordsAreNotStructuredEvidence()
    {
        // The recorded text contains the exact keywords the monitor looks for.
        // A safe parser must produce no attention evidence from prose.
        var line = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"turn_id\":\"turn-1\"," +
                   "\"message\":\"request_user_input isBlocking exec_approval_request waitingOnApproval error\"}}";
        var parsed = Parse(line);
        Assert.Equal(RawCodexEventKind.ItemActivity, parsed.Kind);
        Assert.Equal(ToolCallCategory.Message, parsed.Tool);
        Assert.Null(parsed.IsBlocking);
        Assert.NotEqual(RawCodexEventKind.InputRequested, parsed.Kind);
        Assert.NotEqual(RawCodexEventKind.PermissionRequested, parsed.Kind);
    }

    [Fact]
    public void ThreadSettingsAppliedNeverBecomesAttentionEvidence()
    {
        var line = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\",\"thread_id\":\"t\"," +
                   "\"thread_settings\":{\"approval_policy\":\"never\",\"approvals_reviewer\":\"user\"}}}";
        var result = CodexEventParser.ParseLine(line, Context());
        Assert.Equal(ParseOutcome.Ignored, result.Outcome);
        Assert.Null(result.Event);
    }

    [Fact]
    public void StructuredInputAndPermissionRequestsPreserveBlockingSemantics()
    {
        var blocking = Parse(Records.InputRequest("turn-1", isBlocking: true));
        Assert.Equal(RawCodexEventKind.InputRequested, blocking.Kind);
        Assert.True(blocking.IsBlocking);
        Assert.Equal("input-1", blocking.RequestId);

        var nonBlocking = Parse(Records.InputRequest("turn-1", isBlocking: false, requestId: "input-2"));
        Assert.False(nonBlocking.IsBlocking);

        // A missing isBlocking flag keeps the protocol's blocking default.
        var legacy = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"request_user_input\",\"turn_id\":\"turn-1\",\"call_id\":\"c\"}}";
        Assert.True(Parse(legacy).IsBlocking);

        var permission = Parse(Records.PermissionRequest("turn-1"));
        Assert.Equal(RawCodexEventKind.PermissionRequested, permission.Kind);
        Assert.Equal("req-1", permission.RequestId);
    }

    [Fact]
    public void TimestampsAcceptSecondsMillisecondsAndIsoStrings()
    {
        var seconds = Parse(Records.TaskStarted("turn-1", 1_767_323_045));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_767_323_045), seconds.EventTime);

        var milliseconds = Parse(Records.ItemCompleted("turn-1", "Reasoning", completedAtMs: 1_767_323_046_000));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_767_323_046_000), milliseconds.EventTime);

        var meta = Parse(Records.SessionMeta("conversation-1"));
        Assert.NotNull(meta.EventTime);
    }

    [Fact]
    public void HydrationFlagIsPropagatedFromTheContext()
    {
        var result = CodexEventParser.ParseLine(Records.TaskStarted("turn-1"), Context(hydration: true));
        Assert.True(result.Event!.IsHydration);
    }
}
