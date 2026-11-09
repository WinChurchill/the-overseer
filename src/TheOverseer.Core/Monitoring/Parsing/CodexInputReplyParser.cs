using System.Text.Json;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Parsing;

/// <summary>
/// Reads the desktop's exact reply envelope, not natural-language answers. Only
/// call IDs, question indices and the original question count leave this parser.
/// Unknown, oversized or ambiguous envelopes retain attention rather than guess.
/// </summary>
internal static class CodexInputReplyParser
{
    private const int MaxChars = 64 * 1024;
    internal const int MaxQuestions = 64;
    private const string Open = "<send_user_message_question_reply>";
    private const string Close = "</send_user_message_question_reply>";

    public static int? ReadQuestionCount(JsonElement payload)
    {
        if (!payload.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String)
            return null;
        var text = arguments.GetString();
        if (text is null || text.Length > MaxChars) return null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array &&
                questions.GetArrayLength() is > 0 and <= MaxQuestions)
                return questions.GetArrayLength();
        }
        catch (JsonException) { }
        return null;
    }

    public static IReadOnlyList<InputAnswerReference> ReadAnswers(JsonElement payload)
    {
        if (!payload.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "user" ||
            !payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1)
            return Array.Empty<InputAnswerReference>();
        var item = content[0];
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "input_text" ||
            !item.TryGetProperty("text", out var textNode) || textNode.ValueKind != JsonValueKind.String)
            return Array.Empty<InputAnswerReference>();
        var text = textNode.GetString()!;
        if (text.Length > MaxChars) return Array.Empty<InputAnswerReference>();
        text = text.Trim();
        if (!text.StartsWith(Open, StringComparison.Ordinal) || !text.EndsWith(Close, StringComparison.Ordinal))
            return Array.Empty<InputAnswerReference>();
        try
        {
            using var document = JsonDocument.Parse(text[Open.Length..^Close.Length], new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is 0 or > MaxQuestions)
                return Array.Empty<InputAnswerReference>();
            var answers = new HashSet<InputAnswerReference>();
            foreach (var answer in root.EnumerateArray())
            {
                if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("answer", out _) ||
                    !answer.TryGetProperty("questionItemId", out var key) || key.ValueKind != JsonValueKind.String || key.GetString()!.Length > 256)
                    return Array.Empty<InputAnswerReference>();
                using var identity = JsonDocument.Parse(key.GetString()!, new JsonDocumentOptions { MaxDepth = 4 });
                var parts = identity.RootElement;
                if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 3 ||
                    parts[0].ValueKind != JsonValueKind.String || parts[0].GetString() is not ("request_user_input_async" or "functions.request_user_input_async") ||
                    parts[1].ValueKind != JsonValueKind.String || parts[1].GetString() is not { Length: > 0 and <= 128 } callId ||
                    parts[2].ValueKind != JsonValueKind.Number || !parts[2].TryGetInt32(out var index) || index is < 0 or >= MaxQuestions)
                    return Array.Empty<InputAnswerReference>();
                answers.Add(new(callId, index));
            }
            return answers.ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Array.Empty<InputAnswerReference>();
        }
    }
}
