using System.Globalization;

namespace TheOverseer.Core.Monitoring;

public enum ClaudeOverviewNodeKind { Other, Document, Pane, Group, Heading, Link, Image, Feed, Edit, WorkerTitle }

/// <summary>Lazy accessibility metadata. Private content getters need never run.</summary>
public interface IClaudeOverviewNode : IDisposable
{
    ClaudeOverviewNodeKind Kind { get; }
    string Name { get; }
    string Address { get; }
    IEnumerable<IClaudeOverviewNode> Children { get; }
}

/// <summary>Reads project/thread identity, status groups and dedicated worker-title labels.</summary>
public sealed class ClaudeOverviewParser
{
    private readonly string _scope;
    private readonly DateTimeOffset _observedAt;
    private readonly int _maximumSessions;
    private int _remaining;
    private string _reason = "ok";
    private bool _directorySeen;
    private readonly List<Builder> _projects = new();

    public ClaudeOverviewParser(string scope, DateTimeOffset observedAt, int maximumBytes, int maximumSessions)
    {
        _scope = scope;
        _observedAt = observedAt;
        _remaining = Math.Clamp(maximumBytes, 1, 4 * 1024 * 1024);
        _maximumSessions = Math.Clamp(maximumSessions, 1, 1024);
    }

    public ClaudeProjectRead Read(IEnumerable<IClaudeOverviewNode> roots)
    {
        foreach (var root in roots.Take(4)) Visit(root, null, false, 0);
        var projects = _projects.Where(item => item.OverviewFound).Select(item =>
            new ClaudeProjectObservation(_scope, item.Id, item.Name, _observedAt, item.Workers,
                item.Counts.All(pair => item.Workers.Count(worker => worker.Group == pair.Key) == pair.Value) && _reason == "ok")).ToArray();
        return new(projects, projects.Length == 0 && _reason == "ok"
            ? _directorySeen ? "projects-directory-has-no-worker-state" : "projects-overview-not-visible" : _reason);
    }

    private void Visit(IClaudeOverviewNode node, Builder? project, bool inOverview, int depth)
    {
        using (node)
        {
            if (_remaining < 128 || depth > 32) { _reason = "projects-read-budget"; return; }
            _remaining -= 128;
            var kind = node.Kind;
            // Prune before reading Name, Address or descendants. Edit values and
            // conversation text are outside this collector's capability.
            if (kind is ClaudeOverviewNodeKind.Feed or ClaudeOverviewNodeKind.Edit) return;
            if (kind == ClaudeOverviewNodeKind.Document)
            {
                var address = ReadText(node.Address, 1024);
                if (!TryThreadAddress(address, out var id, out _))
                {
                    if (!address.Contains('%') && Uri.TryCreate(address, UriKind.Absolute, out var directory) &&
                        directory.Scheme == "https" && directory.Host == "claude.ai" && directory.IsDefaultPort &&
                        directory.UserInfo.Length == 0 && directory.AbsolutePath is "/projects" or "/code/projects/browse")
                        _directorySeen = true;
                    return;
                }
                if (_projects.Count >= 8) { _reason = "projects-project-limit"; return; }
                var title = ReadText(node.Name, 128).Replace(" - Claude Code", "", StringComparison.Ordinal);
                project = new Builder(id, title.Length <= 80 ? title : "Claude project");
                _projects.Add(project);
                inOverview = false;
            }
            if (project is not null && kind == ClaudeOverviewNodeKind.Pane && ReadText(node.Name, 128) == "Overview")
            { inOverview = true; project.OverviewFound = true; }
            if (project is not null && inOverview && kind == ClaudeOverviewNodeKind.Heading)
            {
                var heading = ReadText(node.Name, 128).Split('·', StringSplitOptions.TrimEntries);
                if (heading.Length == 2 && int.TryParse(heading[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                {
                    project.Group = heading[0];
                    if (!project.Counts.TryAdd(project.Group, count)) _reason = "projects-duplicate-group";
                }
            }
            if (project is not null && inOverview && kind == ClaudeOverviewNodeKind.Link)
            {
                if (TryThreadAddress(ReadText(node.Address, 1024), out var id, out var thread) && thread.Length > 0)
                {
                    if (id != project.Id) { _reason = "projects-cross-project-link"; return; }
                    var worker = ReadWorker(node, thread, project.Group);
                    // A card and its artifact can link to the same worker. Only
                    // identical metadata is redundant. A conflict must stay
                    // unconfirmed even when the session budget is already full.
                    var existing = project.Workers.FindIndex(item => item.ThreadId == thread);
                    if (existing >= 0)
                    {
                        var previous = project.Workers[existing];
                        if (!previous.HasSameStatus(worker))
                        {
                            project.Workers[existing] = new(thread, "", true) { Title = previous.Title };
                            _reason = "projects-conflicting-worker";
                        }
                        else if (previous.Title.Length == 0 && worker.Title.Length > 0)
                            project.Workers[existing] = previous with { Title = worker.Title };
                        return;
                    }
                    if (_projects.Sum(item => item.Workers.Count) >= _maximumSessions) { _reason = "projects-session-limit"; return; }
                    project.Workers.Add(worker);
                }
                return;
            }
            foreach (var child in node.Children)
            {
                Visit(child, project, inOverview, depth + 1);
                if (_remaining < 128) break;
            }
        }
    }

    private ClaudeProjectWorker ReadWorker(IClaudeOverviewNode link, string thread, string group)
    {
        var disconnected = false;
        var title = "";
        var titleNodes = 0;
        var childrenRemaining = 16;
        void ReadChildren(IClaudeOverviewNode parent, bool allowGroup)
        {
            foreach (var child in parent.Children)
            {
                using (child)
                {
                    if (childrenRemaining-- <= 0) break;
                    if (_remaining < 128) { _reason = "projects-read-budget"; break; }
                    _remaining -= 128;
                    var kind = child.Kind;
                    // Only a direct, platform-verified title node may expose text.
                    // Never read the aggregate link name, snippets, feeds or edits.
                    if (kind == ClaudeOverviewNodeKind.WorkerTitle && allowGroup)
                    {
                        var value = child.Name;
                        var bytes = value.Length <= 1024 ? System.Text.Encoding.UTF8.GetByteCount(value) : 4096;
                        if (bytes > _remaining) { _remaining = 0; _reason = "projects-read-budget"; title = ""; }
                        else { _remaining -= bytes; title = ++titleNodes == 1 ? ClaudeProjectWorker.DisplayTitle(value) : ""; }
                    }
                    else if (kind == ClaudeOverviewNodeKind.Image && ReadText(child.Name, 64) == "Disconnected") disconnected = true;
                    else if (kind == ClaudeOverviewNodeKind.Group && allowGroup) ReadChildren(child, false);
                }
                if (_remaining < 128 || childrenRemaining <= 0) break;
            }
        }
        ReadChildren(link, true);
        return new(thread, group, disconnected) { Title = title };
    }

    private string ReadText(string text, int maximum)
    {
        if (text.Length > maximum || text.Any(char.IsControl)) { _reason = "projects-metadata-rejected"; return ""; }
        _remaining -= System.Text.Encoding.UTF8.GetByteCount(text);
        return text;
    }

    public static bool TryThreadAddress(string address, out string projectId, out string threadId)
    {
        projectId = threadId = "";
        // Provider IDs are ASCII opaque tokens. Reject encoded aliases before
        // System.Uri can canonicalize percent-encoded unreserved characters.
        if (address.Contains('%') || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "claude.ai" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        const string prefix = "/epitaxy/project/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        projectId = uri.AbsolutePath[prefix.Length..];
        if (!Opaque(projectId, "chan_")) return false;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] != "thread") continue;
            if (threadId.Length > 0 || parts.Length != 2 || !Opaque(parts[1], "cmsg_")) return false;
            threadId = parts[1];
        }
        return true;
    }

    private static bool Opaque(string value, string prefix) => value.Length > prefix.Length && value.Length <= 256 &&
        value.StartsWith(prefix, StringComparison.Ordinal) && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private sealed class Builder(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Group { get; set; } = "";
        public bool OverviewFound { get; set; }
        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
        public List<ClaudeProjectWorker> Workers { get; } = new();
    }
}
