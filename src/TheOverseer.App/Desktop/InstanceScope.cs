using System.IO;
using System.Security.Principal;

namespace TheOverseer.App.Desktop;

/// <summary>
/// One canonical instance identity: the Windows user plus the canonical data
/// directory. The single-instance mutex and the "show the running HUD" event are
/// both derived from it, so a normal launch and an isolated run can never disagree
/// about which instance they mean.
///
/// Before this type existed the mutex used one fixed Local name while the show
/// signal hashed the data directory: an isolated run therefore lost the mutex
/// (two HUDs, one per data directory, fighting over one name) and signalled the
/// wrong scope. The scope is now the single source for both.
/// </summary>
public static class InstanceScope
{
    private const string MutexPrefix = "Local\\OVERSEER.Hud.SingleInstance";
    private const string ShowRequestPrefix = "Local\\OVERSEER.Hud.ShowRequest";

    /// <summary>
    /// Canonical form of a data directory (or any scope string). A rooted path is
    /// fully resolved and compared case-insensitively, because Windows paths and
    /// drive-relative spellings must not create a second instance identity.
    /// </summary>
    public static string Canonical(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return "default";
        }

        var trimmed = scope!.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                trimmed = Path.GetFullPath(trimmed);
            }
        }
        catch (ArgumentException)
        {
            // An unusable path is still a stable scope: fall through with the text.
        }
        catch (NotSupportedException)
        {
        }
        catch (PathTooLongException)
        {
        }

        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    }

    /// <summary>Stable, bounded token: never a path in a kernel object name.</summary>
    public static string Token(string? scope)
    {
        var material = UserIdentity() + "|" + Canonical(scope);
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in material)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Single-instance mutex name for one scope.</summary>
    public static string MutexName(string? scope) => MutexPrefix + "." + Token(scope);

    /// <summary>Show-request event name for one scope.</summary>
    public static string ShowRequestEventName(string? scope) => ShowRequestPrefix + "." + Token(scope);

    /// <summary>
    /// Current Windows user identity, or the account name when the SID is not
    /// readable. The mutex and the event are per-session (Local\) objects, so this
    /// is the user component of the scope rather than the only isolation.
    /// </summary>
    public static string UserIdentity()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                return sid!;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        return Environment.UserName;
    }
}
