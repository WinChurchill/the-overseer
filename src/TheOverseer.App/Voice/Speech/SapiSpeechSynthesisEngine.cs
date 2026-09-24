using System.Globalization;
using System.IO;

namespace TheOverseer.App.Voice.Speech;

/// <summary>
/// Windows speech (SAPI) stream synthesis on a dedicated STA worker thread. The
/// engine renders into a temporary WAV file through <c>SpFileStream</c>, never to
/// the default audio output, and every call is bounded by a timeout and a byte cap.
/// A machine without an English voice reports unavailable instead of guessing.
/// </summary>
public sealed class SapiSpeechSynthesisEngine : ISpeechSynthesisEngine
{
    private const int SsfmCreateForWrite = 3;
    private const int SvsFlagsAsync = 1;
    private const int SvsFPurgeBeforeSpeak = 2;
    private const int MaximumWavBytes = 4 * 1024 * 1024;
    private readonly Lazy<string> _availability;

    public SapiSpeechSynthesisEngine() => _availability = new Lazy<string>(ProbeAvailability, isThreadSafe: true);

    public string Id => "sapi";

    public bool IsAvailable => string.Equals(_availability.Value, "ok", StringComparison.Ordinal);

    public string UnavailableCode => _availability.Value;

    public SpeechSynthesisResult Synthesize(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return SpeechSynthesisResult.Failed("speech-empty-script");
        }

        if (!IsAvailable)
        {
            return SpeechSynthesisResult.Failed(UnavailableCode);
        }

        var path = Path.Combine(Path.GetTempPath(), "overseer-speech-" + Guid.NewGuid().ToString("N") + ".wav");
        var result = SpeechSynthesisResult.Failed("speech-failed");
        var worker = new Thread(() => result = Render(script, path, timeout))
        {
            IsBackground = true,
            Name = "OVERSEER.Speech",
        };

        try
        {
            worker.SetApartmentState(ApartmentState.STA);
        }
        catch (PlatformNotSupportedException)
        {
        }

        worker.Start();
        var finished = worker.Join(timeout + TimeSpan.FromSeconds(2));
        if (!finished)
        {
            // The worker is background and owns its own COM objects: abandoning it
            // cannot block the voice queue or the UI.
            return SpeechSynthesisResult.Failed("speech-timeout");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (result.IsValid)
            {
                return result;
            }

            return result;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static SpeechSynthesisResult Render(string script, string path, TimeSpan timeout)
    {
        object? voice = null;
        object? stream = null;
        try
        {
            voice = Create("SAPI.SpVoice");
            stream = Create("SAPI.SpFileStream");
            if (voice is null || stream is null)
            {
                return SpeechSynthesisResult.Failed("speech-missing");
            }

            dynamic spVoice = voice;
            dynamic spStream = stream;

            // Explicit English (409) engine selection: no locale guessing and no
            // silent fallback to a non-English voice.
            dynamic voices = spVoice.GetVoices("Language=409", string.Empty);
            if (voices.Count == 0)
            {
                return SpeechSynthesisResult.Failed("speech-no-english-voice");
            }

            spVoice.Voice = voices.Item(0);
            spStream.Open(path, SsfmCreateForWrite, false);
            spVoice.AudioOutputStream = spStream;
            spVoice.Speak(script, 0);
            spStream.Close();
            stream = null;

            if (!File.Exists(path))
            {
                return SpeechSynthesisResult.Failed("speech-no-audio");
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length <= 0 || file.Length > MaximumWavBytes)
            {
                return SpeechSynthesisResult.Failed("speech-oversize");
            }

            var bytes = new byte[file.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var next = file.Read(bytes, read, bytes.Length - read);
                if (next <= 0)
                {
                    break;
                }

                read += next;
            }

            if (read != bytes.Length)
            {
                return SpeechSynthesisResult.Failed("speech-short-read");
            }

            return new SpeechSynthesisResult(bytes, "speech-ok");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _ = timeout;
            return SpeechSynthesisResult.Failed("speech-error:" + exception.GetType().Name);
        }
        finally
        {
            TryClose(stream);
            TryRelease(voice);
        }
    }

    private static object? Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false);
        return type is null ? null : Activator.CreateInstance(type);
    }

    private static void TryClose(object? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            ((dynamic)stream).Close();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
    }

    private static void TryRelease(object? instance)
    {
        if (instance is not null && System.Runtime.InteropServices.Marshal.IsComObject(instance))
        {
            try
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Probe: is there a usable English voice on this machine?</summary>
    private static string ProbeAvailability()
    {
        object? voice = null;
        try
        {
            voice = Create("SAPI.SpVoice");
            if (voice is null)
            {
                return "speech-missing";
            }

            dynamic spVoice = voice;
            dynamic voices = spVoice.GetVoices("Language=409", string.Empty);
            return voices.Count > 0 ? "ok" : "speech-no-english-voice";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            return "speech-error:" + exception.GetType().Name;
        }
        finally
        {
            TryRelease(voice);
        }
    }
}
