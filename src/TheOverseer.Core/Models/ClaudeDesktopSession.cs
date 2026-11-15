namespace TheOverseer.Core.Models;

/// <summary>
/// The Claude desktop app's session ids and the link that opens one. The id is
/// the only value that reaches the link, and only after this check.
/// </summary>
public static class ClaudeDesktopSession
{
    public const string LinkPrefix = "claude://claude.ai/epitaxy/";
    private const string IdPrefix = "local_";

    /// <summary>local_ followed by 1-64 ASCII letters, digits or dashes.</summary>
    public static bool IsValidId(string? id)
    {
        if (id is null || id.Length <= IdPrefix.Length || id.Length > IdPrefix.Length + 64) return false;
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal)) return false;
        for (var i = IdPrefix.Length; i < id.Length; i++)
        {
            var c = id[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        }
        return true;
    }

    public static string? LinkFor(string? id) => IsValidId(id) ? LinkPrefix + id : null;
}
