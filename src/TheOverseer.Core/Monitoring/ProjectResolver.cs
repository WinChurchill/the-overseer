using System.Globalization;
using System.Text;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring;

/// <summary>Bounded filesystem seams so resolution is testable without a disk.</summary>
public sealed record ProjectResolverProbe(
    Func<string, bool> DirectoryExists,
    Func<string, bool> FileExists,
    Func<string, int, string?> ReadBoundedText,
    Func<string, bool>? IsReparsePoint = null)
{
    public static ProjectResolverProbe System { get; } = new(
        Directory.Exists,
        File.Exists,
        static (path, maximumBytes) =>
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Read at most the bound: the byte cap is enforced by the read
                // itself, not by trusting a length that can change underneath.
                var cap = Math.Clamp(maximumBytes, 1, 64 * 1024);
                var buffer = new byte[cap];
                var read = 0;
                while (read < cap)
                {
                    var next = stream.Read(buffer, read, cap - read);
                    if (next <= 0)
                    {
                        break;
                    }

                    read += next;
                }

                // Encoding is imported directly: the member named System on this
                // record would otherwise shadow the System namespace.
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        },
        static path =>
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                // Uninspectable is treated as a reparse point: never traverse it.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        });
}

/// <summary>
/// Deterministic project identity. Rules, in order: explicit assignment,
/// longest configured root ancestor, locally verified Git repository root
/// (worktrees of one repository group through verified Git metadata), the
/// normalized workspace root itself, otherwise Unassigned.
///
/// Paths are compared by complete segments after normalization, so
/// <c>C:/work/app2</c> is never inside <c>C:/work/app</c>, two folders named
/// <c>repo</c> stay distinct, and relative or missing paths never resolve
/// against the process working directory. WSL/POSIX roots live in their own
/// namespace and join Windows roots only through an explicit alias.
/// </summary>
public sealed class ProjectResolver
{
    private const string PosixNamespace = "posix:";
    private readonly ProjectSettings _settings;
    private readonly ProjectResolverProbe _probe;
    private readonly Dictionary<string, ProjectAssignment> _cache = new(StringComparer.Ordinal);
    private readonly List<(string Alias, string Canonical)> _aliases = new();
    private readonly List<(string Root, ProjectDescriptor Project)> _mappings = new();
    private readonly int _maximumAncestorChecks;
    private readonly int _maximumCacheEntries;

    public ProjectResolver(ProjectSettings? settings = null, ProjectResolverProbe? probe = null)
    {
        _settings = settings ?? new ProjectSettings();
        _probe = probe ?? ProjectResolverProbe.System;
        _maximumAncestorChecks = Math.Clamp(_settings.MaxAncestorChecks, 1, 128);
        _maximumCacheEntries = Math.Clamp(_settings.MaxCacheEntries, 16, 8192);
        BuildAliases();
        BuildMappings();
    }

    public int CacheCount => _cache.Count;

    /// <summary>Resolves one task workspace root. Never throws and never guesses.</summary>
    public ProjectAssignment Resolve(string? workspaceRoot, string? explicitProjectKey = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitProjectKey))
        {
            var explicitDescriptor = FindExplicit(explicitProjectKey!);
            if (explicitDescriptor is not null)
            {
                return new ProjectAssignment(explicitDescriptor, ProjectResolution.Explicit, "explicit");
            }
        }

        var normalized = Normalize(workspaceRoot);
        if (normalized.Length == 0)
        {
            return ProjectAssignment.None;
        }

        if (_cache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var assignment = ResolveNormalized(normalized, workspaceRoot!);
        if (_cache.Count >= _maximumCacheEntries)
        {
            _cache.Clear();
        }

        _cache[normalized] = assignment;
        return assignment;
    }

    /// <summary>
    /// Canonical comparison form: lower case, backslash separators, no trailing
    /// separator, no dot segments, device/relative paths rejected. POSIX paths
    /// keep forward slashes behind an explicit namespace marker.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var value = path.Trim().Trim('"');
        if (value.Length == 0 || value.Contains('\0', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var isPosix = value[0] == '/';
        var isUnc = false;
        if (!isPosix)
        {
            if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                value = @"\\" + value[8..];
            }
            else if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                value = value[4..];
            }

            // Device namespaces are never project roots.
            if (value.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            isUnc = value.StartsWith(@"\\", StringComparison.Ordinal);
            if (isUnc)
            {
                var rootParts = value[2..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (rootParts.Length < 2)
                {
                    // A bare server or share cannot identify a project on its own.
                    return string.Empty;
                }
            }
            else if (!(value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
                       (value[2] == '\\' || value[2] == '/')))
            {
                // Rejects relative paths, drive-relative paths such as C:repo, and
                // any other form that cannot be compared by complete segments.
                return string.Empty;
            }
        }

        var segments = new List<string>();
        foreach (var raw in value.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".")
            {
                continue;
            }

            if (raw == "..")
            {
                // Root boundaries are absolute: .. can never pop the drive root or
                // the UNC server/share, and never escapes a POSIX root.
                var floor = isPosix ? 0 : isUnc ? 2 : 1;
                if (segments.Count > floor)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(raw);
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        if (isPosix)
        {
            // POSIX paths preserve case, are compared case-sensitively, and live in
            // their own namespace so they never match a Windows root.
            return PosixNamespace + "/" + string.Join("/", segments);
        }

        var joined = string.Join("\\", segments).ToLowerInvariant();
        return isUnc ? @"\\" + joined : joined;
    }

    private bool IsReparsePoint(string normalizedPath)
    {
        var probe = _probe.IsReparsePoint;
        if (probe is null)
        {
            return false;
        }

        try
        {
            return probe(normalizedPath);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Joins metadata file names using the separator of the namespace.</summary>
    private static string Join(string parent, string child) =>
        parent.Contains('\\', StringComparison.Ordinal) ? parent + "\\" + child : parent + "/" + child;

    /// <summary>Complete-segment containment test on already normalized paths.</summary>
    public static bool IsUnder(string normalizedRoot, string normalizedCandidate)
    {
        if (normalizedRoot.Length == 0 || normalizedCandidate.Length == 0)
        {
            return false;
        }

        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.Ordinal))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.Ordinal) ||
               normalizedCandidate.StartsWith(normalizedRoot + "\\", StringComparison.Ordinal);
    }

    /// <summary>Last segment of a normalized path, used as a default display name.</summary>
    public static string Leaf(string normalizedPath)
    {
        if (normalizedPath.Length == 0)
        {
            return string.Empty;
        }

        var trimmed = normalizedPath.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var leaf = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        if (leaf.StartsWith(PosixNamespace, StringComparison.Ordinal))
        {
            leaf = leaf[PosixNamespace.Length..];
            index = leaf.LastIndexOf('/');
            leaf = index >= 0 ? leaf[(index + 1)..] : leaf;
        }

        return leaf;
    }

    /// <summary>Safe diagnostic summary: counts only, never a path.</summary>
    public string Describe() =>
        "projects=" + _mappings.Count.ToString(CultureInfo.InvariantCulture) +
        " aliases=" + _aliases.Count.ToString(CultureInfo.InvariantCulture) +
        " cache=" + _cache.Count.ToString(CultureInfo.InvariantCulture);

    private ProjectAssignment ResolveNormalized(string normalized, string original)
    {
        var aliased = ApplyAliases(normalized);

        // 1. Longest configured root ancestor wins over filesystem evidence.
        ProjectDescriptor? bestMapping = null;
        var bestLength = -1;
        foreach (var (root, project) in _mappings)
        {
            if (IsUnder(root, aliased) && root.Length > bestLength)
            {
                bestMapping = project;
                bestLength = root.Length;
            }
        }

        if (bestMapping is not null)
        {
            return new ProjectAssignment(bestMapping, ProjectResolution.ConfiguredRoot, "configured-root");
        }

        // 2. Local Git evidence, bounded ancestor walk, never recursive.
        var git = FindRepositoryRoot(aliased);
        if (git is not null)
        {
            return new ProjectAssignment(git, ProjectResolution.GitRoot, git.CheckoutLabel is null ? "git-root" : "git-worktree");
        }

        // 3. The workspace root itself, when it is an absolute usable path.
        var leaf = Leaf(normalized);
        if (leaf.Length == 0)
        {
            return ProjectAssignment.None;
        }

        var descriptor = new ProjectDescriptor(ProjectId.FromRoot(aliased), DisplayNameFor(leaf), normalized, ProjectResolution.WorkspaceRoot);
        _ = original;
        return new ProjectAssignment(descriptor, ProjectResolution.WorkspaceRoot, "workspace-root");
    }

    private string ApplyAliases(string normalized)
    {
        var best = normalized;
        var bestLength = -1;
        foreach (var (alias, canonical) in _aliases)
        {
            if (IsUnder(alias, normalized) && alias.Length > bestLength)
            {
                best = canonical + normalized[alias.Length..];
                bestLength = alias.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Verified Git root. A <c>.git</c> directory is the repository root; a
    /// <c>.git</c> file is a linked worktree whose <c>gitdir</c>/<c>commondir</c>
    /// metadata identifies the main checkout, so worktrees of one repository
    /// group into one project while independent clones stay separate.
    /// </summary>
    private ProjectDescriptor? FindRepositoryRoot(string normalized)
    {
        // Only a local absolute Windows drive path is probed. UNC and POSIX roots
        // never become filesystem probes, so a network or WSL path cannot block
        // resolution and is never traversed as a local path.
        if (normalized.StartsWith(PosixNamespace, StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var current = normalized;
        for (var depth = 0; depth < _maximumAncestorChecks && current.Length > 0; depth++)
        {
            if (IsReparsePoint(current))
            {
                // A junction or symlink must never redirect project identity, so the
                // walk stops here instead of following the link upwards.
                return null;
            }

            var gitPath = Join(current, ".git");
            if (_probe.DirectoryExists(gitPath))
            {
                return new ProjectDescriptor(ProjectId.FromRoot(current), DisplayNameFor(Leaf(current)), current, ProjectResolution.GitRoot);
            }

            if (_probe.FileExists(gitPath))
            {
                var gitFile = _probe.ReadBoundedText(gitPath, 4096);
                var gitDir = ParseGitDir(gitFile, current);
                if (gitDir.Length > 0)
                {
                    var common = gitDir;
                    var commonFile = _probe.ReadBoundedText(Join(gitDir, "commondir"), 4096);
                    if (!string.IsNullOrWhiteSpace(commonFile))
                    {
                        var resolved = ResolveRelativePath(gitDir, commonFile!.Trim());
                        if (resolved.Length > 0)
                        {
                            common = resolved;
                        }
                    }

                    var mainRoot = StripGitSuffix(common);
                    if (mainRoot.Length > 0)
                    {
                        // Worktree checkouts share the main repository project but
                        // keep their own checkout label for task detail.
                        var checkout = Leaf(current);
                        return new ProjectDescriptor(
                            ProjectId.FromRoot(mainRoot),
                            DisplayNameFor(Leaf(mainRoot)),
                            mainRoot,
                            ProjectResolution.GitRoot,
                            string.Equals(checkout, Leaf(mainRoot), StringComparison.Ordinal) ? null : checkout);
                    }
                }
            }

            var separator = current.LastIndexOfAny(new[] { '/', '\\' });
            if (separator <= 0)
            {
                break;
            }

            current = current[..separator];
        }

        return null;
    }

    private static string ParseGitDir(string? gitFile, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(gitFile))
        {
            return string.Empty;
        }

        var text = gitFile.Trim();
        const string prefix = "gitdir:";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var target = text[prefix.Length..].Trim();
        if (target.Length == 0 || target.Length > 1024)
        {
            return string.Empty;
        }

        return ResolveRelativePath(Join(worktreeRoot, ".git"), target);
    }

    private static string ResolveRelativePath(string basePath, string target)
    {
        var normalizedTarget = target.Replace('\\', '/').Trim();
        var combined = normalizedTarget.Length > 0 && (normalizedTarget[0] == '/' || normalizedTarget.Contains(':'))
            ? normalizedTarget
            : basePath + "/" + normalizedTarget;
        var normalized = Normalize(combined);
        return normalized;
    }

    private static string StripGitSuffix(string gitDirectory)
    {
        var trimmed = gitDirectory.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        if (index <= 0)
        {
            return string.Empty;
        }

        var leaf = trimmed[(index + 1)..];
        if (leaf is ".git" or "git")
        {
            return trimmed[..index];
        }

        // A bare/common directory outside the worktree is not a project root.
        return string.Empty;
    }

    private static string DisplayNameFor(string leaf) => leaf.Length == 0 ? "Project" : leaf;

    private void BuildAliases()
    {
        foreach (var alias in _settings.Aliases ?? Array.Empty<ProjectAlias>())
        {
            var normalizedAlias = Normalize(alias.Alias);
            var canonical = Normalize(alias.Canonical);
            if (normalizedAlias.Length == 0 || canonical.Length == 0 || normalizedAlias == canonical)
            {
                continue;
            }

            _aliases.Add((normalizedAlias, canonical));
        }

        _aliases.Sort(static (left, right) => right.Alias.Length.CompareTo(left.Alias.Length));
    }

    private void BuildMappings()
    {
        foreach (var mapping in _settings.Mappings ?? Array.Empty<ProjectRootMapping>())
        {
            var root = Normalize(mapping.Root);
            if (root.Length == 0)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(mapping.Name) ? Leaf(root) : mapping.Name.Trim();
            var id = string.IsNullOrWhiteSpace(mapping.ProjectKey) ? ProjectId.FromRoot(root) : ProjectId.FromKey(mapping.ProjectKey);
            _mappings.Add((root, new ProjectDescriptor(id, DisplayNameFor(name), root, ProjectResolution.ConfiguredRoot)));
        }

        _mappings.Sort(static (left, right) => right.Root.Length.CompareTo(left.Root.Length));
    }

    private ProjectDescriptor? FindExplicit(string projectKey)
    {
        var id = ProjectId.FromKey(projectKey);
        foreach (var (_, project) in _mappings)
        {
            if (project.Id == id)
            {
                return project;
            }
        }

        return null;
    }
}
