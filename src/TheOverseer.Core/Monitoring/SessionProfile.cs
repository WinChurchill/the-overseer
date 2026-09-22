namespace TheOverseer.Core.Monitoring;

/// <summary>
/// One Codex profile root. Additional configured roots become additional
/// profiles; there is no compulsory provider-specific profile.
/// </summary>
public sealed record SessionProfile(
    string Id,
    string Label,
    string RootPath,
    string SessionsRoot,
    string SessionIndexPath,
    string StateDatabasePath)
{
    public const string DefaultId = "codex";

    public bool HasSessionIndex => !string.IsNullOrWhiteSpace(SessionIndexPath);

    public bool HasStateDatabase => !string.IsNullOrWhiteSpace(StateDatabasePath);
}

public static class MonitorProfiles
{
    /// <summary>
    /// Builds profiles from a Codex home (CODEX_HOME or %USERPROFILE%\.codex)
    /// plus optional additional roots. Only paths are derived; no Codex
    /// configuration or authentication content is read.
    /// </summary>
    public static IReadOnlyList<SessionProfile> Create(string codexHome, IEnumerable<string>? additionalRoots = null)
    {
        var profiles = new List<SessionProfile>
        {
            CreateDefault(codexHome),
        };

        var index = 1;
        foreach (var root in additionalRoots ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            index++;
            var full = SafeFullPath(root);
            profiles.Add(new SessionProfile(
                "codex-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LeafOf(full),
                full,
                Path.Combine(full, "sessions"),
                Path.Combine(full, "session_index.jsonl"),
                Path.Combine(full, "state_5.sqlite")));
        }

        return profiles;
    }

    public static SessionProfile CreateDefault(string codexHome)
    {
        var root = SafeFullPath(codexHome);
        return new SessionProfile(
            SessionProfile.DefaultId,
            "Codex",
            root,
            Path.Combine(root, "sessions"),
            Path.Combine(root, "session_index.jsonl"),
            Path.Combine(root, "state_5.sqlite"));
    }

    /// <summary>Resolves CODEX_HOME, else %USERPROFILE%\.codex.</summary>
    public static string ResolveCodexHome(string? explicitHome)
    {
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return SafeFullPath(explicitHome);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return SafeFullPath(fromEnvironment);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex");
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
    }

    private static string LeafOf(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var separator = trimmed.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
    }
}
