using System.Text;
using System.Text.Json;

namespace TheOverseer.Core.Models;

/// <summary>User-confirmed intent. Pins never supply monitoring evidence.</summary>
public sealed record FocusPin(string Id, string Project, string Objective, string NextStep,
    string Reason, DateTimeOffset ConfirmedAt);

public sealed record FocusProposal(string Project, string Objective, string NextStep, string Reason, bool Uncertain);
public sealed record FocusImportResult(bool Accepted, string Message, IReadOnlyList<FocusProposal> Items);

/// <summary>A bounded, data-only interchange for the manual focus board.</summary>
public static class FocusPinContract
{
    public const int MaximumPins = 3;
    public const int MaximumImportBytes = 32 * 1024;
    public const string Prompt = """
        Suggest up to three focus pins from this conversation's explicit user objectives and constraints.
        You only know this conversation; do not claim to know my global priorities.
        Keep each objective, nextStep and reason under 240 characters, and project under 80 characters.
        Use an empty project for a general objective. Set uncertain=true when you infer priority or a next step.
        Include no credentials, private file contents, task status, executable instructions or fabricated IDs.
        Return only JSON in this exact format, with version 1 and at most three items:
        {"version":1,"items":[{"project":"","objective":"","nextStep":"","reason":"","uncertain":false}]}
        If there is insufficient evidence, return {"version":1,"items":[]}.
        I will review and edit these suggestions before saving them in OVERSEER.
        """;

    public static FocusImportResult Parse(string? json)
    {
        FocusImportResult Reject(string message) => new(false, message, Array.Empty<FocusProposal>());
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumImportBytes || Encoding.UTF8.GetByteCount(json) > MaximumImportBytes)
            return Reject("Paste a JSON object of at most 32 KiB.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!HasOnly(root, "version", "items") || !root.TryGetProperty("version", out var version) ||
                !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() > MaximumPins)
                return Reject("Expected version 1 with up to three items.");
            var proposals = new List<FocusProposal>();
            foreach (var item in items.EnumerateArray())
            {
                if (!HasOnly(item, "project", "objective", "nextStep", "reason", "uncertain") ||
                    !Read(item, "project", 80, out var project) || !Read(item, "objective", 240, out var objective) ||
                    objective.Length == 0 || !Read(item, "nextStep", 240, out var next) ||
                    !Read(item, "reason", 240, out var reason) || !item.TryGetProperty("uncertain", out var uncertain) ||
                    uncertain.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Reject("Each item needs bounded text fields, a nonempty objective and an uncertain boolean.");
                proposals.Add(new(project, objective, next, reason, uncertain.GetBoolean()));
            }
            return new(true, proposals.Count == 0 ? "No focus suggestions were supplied." : "Review and edit these suggestions, then confirm them.", proposals);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Reject("Invalid focus JSON. No pins were changed.");
        }
    }

    public static IReadOnlyList<FocusPin> Normalize(IEnumerable<FocusPin>? pins) =>
        (pins ?? Array.Empty<FocusPin>()).Take(32)
            .Where(pin => pin is not null && Guid.TryParseExact(pin.Id, "N", out _) &&
                Valid(pin.Project, 80) && Valid(pin.Objective, 240) && !string.IsNullOrWhiteSpace(pin.Objective) &&
                Valid(pin.NextStep, 240) && Valid(pin.Reason, 240) && pin.ConfirmedAt != default)
            .DistinctBy(pin => pin.Id, StringComparer.Ordinal).Take(MaximumPins).ToArray();

    private static bool HasOnly(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        return true;
    }

    private static bool Read(JsonElement item, string key, int maximum, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        var text = field.GetString();
        if (!Valid(text, maximum)) return false;
        value = text!.Trim();
        return true;
    }

    private static bool Valid(string? text, int maximum) =>
        text is not null && text.Length <= maximum && !text.Any(char.IsControl);
}
