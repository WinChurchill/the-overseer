namespace TheOverseer.App.Voice.Speech;

/// <summary>Outcome of one bounded synthesis attempt.</summary>
public sealed record SpeechSynthesisResult(byte[]? Wav, string Code)
{
    public bool IsValid => Wav is { Length: > 0 };

    public static SpeechSynthesisResult Failed(string code) => new(null, code);
}

/// <summary>
/// Injectable boundary around the operating system's offline speech engine. The
/// engine only ever returns validated WAV bytes for the fixed catalog scripts; it
/// never speaks to a speaker, never touches the network and never sees task names,
/// paths or any other run-time text.
/// </summary>
public interface ISpeechSynthesisEngine
{
    /// <summary>Stable engine id for diagnostics, for example <c>sapi</c>.</summary>
    string Id { get; }

    /// <summary>True when an English voice was found and can be used.</summary>
    bool IsAvailable { get; }

    /// <summary>Bounded reason code when <see cref="IsAvailable"/> is false.</summary>
    string UnavailableCode { get; }

    /// <summary>
    /// Synthesizes one plain-text script to WAV bytes. Bounded by the caller's
    /// timeout and cancellation token; returns a failed result instead of throwing.
    /// </summary>
    SpeechSynthesisResult Synthesize(string script, TimeSpan timeout, CancellationToken cancellationToken);
}
