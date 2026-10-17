using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class IdentityTests : IDisposable
{
    private readonly SyntheticCodexHome _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void ReadsConversationIdWorkspaceAndSurface()
    {
        var path = _home.WriteRollout(
            "01a0c68e-e7f6-7740-ab8f-088bd7aedd9d",
            new[] { Records.SessionMeta("01a0c68e-e7f6-7740-ab8f-088bd7aedd9d", cwd: @"D:\work\overseer") });

        var identity = SessionIdentityReader.Read(path, 64 * 1024);
        Assert.True(identity.Found);
        Assert.Equal("01a0c68e-e7f6-7740-ab8f-088bd7aedd9d", identity.ConversationId);
        Assert.Equal("overseer", identity.WorkspaceLeaf);
        Assert.Equal(ClientSurface.Desktop, identity.Surface);
        Assert.False(identity.IsExplicitInternal);
    }

    [Theory]
    [InlineData("codex_work_desktop", @"C:\Users\fixture\Documents\Codex\2026-10-05\a-prompt-slug", true)]
    [InlineData("codex_work_desktop", @"C:\USERS\fixture\DOCUMENTS\CODEX\2026-10-05\chat", true)]
    [InlineData("Codex Desktop", @"C:\Users\fixture\Documents\Codex\2026-10-05\chat", false)]
    [InlineData("codex_vscode", @"C:\Users\fixture\Documents\Codex\2026-10-05\chat", false)]
    [InlineData("codex_work_desktop", @"D:\projects\a-prompt-slug", false)]
    [InlineData("codex_work_desktop", @"C:\Users\fixture\Documents\Codex\2026-02-30\chat", false)]
    [InlineData("codex_work_desktop", @"C:\Users\fixture\Documents\Codex\2026-10-05\chat\nested", false)]
    public void StandaloneWorkNeedsBothOriginatorAndExactManagedDirectory(string originator, string cwd, bool expected)
    {
        const string id = "01a0c68f-d03c-7352-b737-43bb94f76b81";
        var path = _home.WriteRollout(id, new[] { Records.SessionMeta(id, originator: originator, source: "vscode", cwd: cwd) });
        var identity = SessionIdentityReader.Read(path, 64 * 1024);
        Assert.Equal(expected, identity.IsStandaloneWorkspace);
    }

    [Fact]
    public void VscodeOriginatorWinsOverVscodeSource()
    {
        var path = _home.WriteRollout(
            "01a0c68f-d03c-7352-b737-43bb94f76b81",
            new[] { Records.SessionMeta("01a0c68f-d03c-7352-b737-43bb94f76b81", originator: "codex_vscode", source: "vscode") });

        Assert.Equal(ClientSurface.VsCode, SessionIdentityReader.Read(path, 64 * 1024).Surface);
    }

    [Theory]
    [InlineData("guardian_review")]
    [InlineData("subagent")]
    [InlineData("agent_created_thread")]
    [InlineData("chatgpt_handoff")]
    public void ExplicitInternalThreadSourcesAreExcluded(string threadSource)
    {
        var path = _home.WriteRollout(
            "01a0c68f-d03c-7352-b737-43bb94f76b81",
            new[] { Records.SessionMeta("01a0c68f-d03c-7352-b737-43bb94f76b81", threadSource: threadSource) });
        Assert.True(SessionIdentityReader.Read(path, 64 * 1024).IsExplicitInternal);
    }

    [Fact]
    public void SubagentSourceObjectIsExcludedButParentThreadAloneIsNot()
    {
        var internalPath = _home.WriteRollout(
            "01a0c68f-d03c-7352-b737-43bb94f76b81",
            new[] { Records.SessionMeta("01a0c68f-d03c-7352-b737-43bb94f76b81", subagent: true, threadSource: null) });
        Assert.True(SessionIdentityReader.Read(internalPath, 64 * 1024).IsExplicitInternal);

        var forked = _home.WriteRollout(
            "01a0c690-0000-0000-0000-000000000001",
            new[] { Records.SessionMeta("01a0c690-0000-0000-0000-000000000001", threadSource: "user", parentThreadId: "01a0c68e-e7f6-7740-ab8f-088bd7aedd9d") });
        var identity = SessionIdentityReader.Read(forked, 64 * 1024);
        Assert.True(identity.HasParentThread);
        Assert.False(identity.IsExplicitInternal);
    }

    [Fact]
    public void MissingHeaderYieldsEmptyIdentityAndProvisionalFileNameKey()
    {
        var path = _home.WriteRollout(
            "01a0c691-0000-0000-0000-000000000002",
            new[] { "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}" });

        var identity = SessionIdentityReader.Read(path, 64 * 1024);
        Assert.False(identity.Found);
        Assert.Equal("01a0c691-0000-0000-0000-000000000002", SessionIdentityReader.TryDeriveConversationIdFromFileName(path));
    }

    [Fact]
    public void HeaderReadIsByteBounded()
    {
        var padding = new string('x', 200_000);
        var path = _home.WriteRollout(
            "01a0c692-0000-0000-0000-000000000003",
            new[]
            {
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"" + padding + "\"}}",
                Records.SessionMeta("01a0c692-0000-0000-0000-000000000003"),
            });

        var identity = SessionIdentityReader.Read(path, 64 * 1024);
        Assert.False(identity.Found);
        Assert.Equal("session-meta-missing", identity.DiagnosticCode);
    }
}
