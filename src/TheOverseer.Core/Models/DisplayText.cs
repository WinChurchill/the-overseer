using System.Globalization;
using System.Text;

namespace TheOverseer.Core.Models;

/// <summary>
/// Cleans text that came from an agent's own files (session titles, folder names)
/// before the HUD shows it. Those files can be written by a prompt-injected agent,
/// so invisible characters must not be able to change what a row appears to say.
/// Control characters and format characters (direction overrides such as U+202E,
/// zero-width spaces, BOMs) are removed; the zero-width joiner is kept so emoji
/// sequences still render. Lone surrogate halves are dropped.
/// </summary>
public static class DisplayText
{
    private const char ZeroWidthJoiner = '‍';

    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (!NeedsCleaning(value)) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    var category = CharUnicodeInfo.GetUnicodeCategory(value, i);
                    if (category != UnicodeCategory.Format) builder.Append(c).Append(value[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            if (char.IsControl(c))
            {
                // Keep word separation where a tab or newline split words.
                if (char.IsWhiteSpace(c)) builder.Append(' ');
                continue;
            }
            if (c != ZeroWidthJoiner && char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    private static bool NeedsCleaning(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                if (CharUnicodeInfo.GetUnicodeCategory(value, i) == UnicodeCategory.Format) return true;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(c) || char.IsControl(c)) return true;
            if (c != ZeroWidthJoiner && char.GetUnicodeCategory(c) == UnicodeCategory.Format) return true;
        }
        return value.Length != value.AsSpan().Trim().Length;
    }
}
