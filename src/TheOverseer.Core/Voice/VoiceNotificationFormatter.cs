using System.Globalization;
using System.Text;

namespace TheOverseer.Core.Voice;

/// <summary>Generic TTS request body. Only these three fields are ever sent.</summary>
public sealed record VoiceTtsRequest(string Text, string EventCode, string Task);

/// <summary>
/// Short notification text. Default aliases are "Task N"/"All tasks": session
/// titles can contain prompt-derived private text and are never used.
/// </summary>
public static class VoiceNotificationFormatter
{
    public const int MaximumCharacters = 160;

    public static string EventCode(VoiceEventKind kind) => kind switch
    {
        VoiceEventKind.PermissionRequired => "permission_required",
        VoiceEventKind.InputRequired => "input_required",
        VoiceEventKind.TaskError => "task_error",
        VoiceEventKind.TaskCompleted => "task_complete",
        _ => "all_tasks_complete",
    };

    public static string Alias(VoiceCandidate candidate)
    {
        if (candidate.IsAggregate)
        {
            return candidate.TaskCount > 0
                ? candidate.TaskCount.ToString(CultureInfo.InvariantCulture) + " tasks"
                : "All tasks";
        }

        var number = candidate.Number > 0
            ? candidate.Number.ToString(CultureInfo.InvariantCulture)
            : "N";
        return "Task " + number;
    }

    public static string Text(VoiceCandidate candidate)
    {
        var alias = Alias(candidate);
        var text = candidate.Kind switch
        {
            VoiceEventKind.PermissionRequired => alias + " needs your approval.",
            VoiceEventKind.InputRequired => alias + " needs your input.",
            VoiceEventKind.TaskError => alias + " failed.",
            VoiceEventKind.TaskCompleted => candidate.IsAggregate && candidate.TaskCount > 1
                ? alias + " finished."
                : alias + " finished.",
            _ => string.IsNullOrEmpty(alias) || alias == "All tasks" ? "All tasks finished." : alias + " finished.",
        };
        return Clamp(text);
    }

    public static VoiceTtsRequest ToRequest(VoiceCandidate candidate) =>
        new(Text(candidate), EventCode(candidate.Kind), Alias(candidate));

    public static string Clamp(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Task finished.";
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaximumCharacters));
        foreach (var character in text)
        {
            if (builder.Length >= MaximumCharacters)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
