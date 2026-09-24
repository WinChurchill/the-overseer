using System.IO;

namespace TheOverseer.App.Voice.Speech;

/// <summary>
/// Renders one plain-text script into <paramref name="path"/> on the engine's own
/// worker thread. A renderer may block for as long as it needs; the engine bounds
/// the wait, observes cancellation while waiting and never deletes a file a live
/// renderer may still be writing.
/// </summary>
public delegate SpeechSynthesisResult SpeechSynthesisRenderer(string script, string path, TimeSpan timeout);

/// <summary>
/// Windows speech (SAPI) stream synthesis on bounded dedicated STA worker threads.
/// The engine renders into a temporary WAV file through <c>SpFileStream</c>, never
/// to the default audio output, and every call is bounded by a caller timeout, a
/// worker cap and a 4 MiB byte cap. A timed-out worker is abandoned instead of
/// awaited: at most <see cref="MaximumConcurrentWorkers"/> can exist, each one
/// deletes its own temporary file in its own finally, and the engine accepts work
/// again once they finish. A machine without an English voice reports unavailable
/// instead of guessing.
/// </summary>
public sealed class SapiSpeechSynthesisEngine : ISpeechSynthesisEngine
{
    /// <summary>Live workers allowed at once; further calls report <c>speech-busy</c>.</summary>
    public const int MaximumConcurrentWorkers = 2;

    private const int SsfmCreateForWrite = 3;
    private const int SvsFlagsAsync = 1;
    private const int SvsFPurgeBeforeSpeak = 2;
    private const int MaximumWavBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan JoinSlice = TimeSpan.FromMilliseconds(25);

    private readonly SpeechSynthesisRenderer _renderer;
    private readonly Lazy<string> _availability;
    private readonly string _temporaryDirectory;
    private readonly object _gate = new();
    private readonly List<Worker> _workers = new();
    private int _abandoned;

    public SapiSpeechSynthesisEngine()
        : this(RenderWithSapi, ProbeAvailability, temporaryDirectory: null)
    {
    }

    /// <summary>
    /// Alternate-renderer seam: a host that must not touch SAPI directly supplies
    /// its own renderer, availability probe and temporary directory.
    /// </summary>
    public SapiSpeechSynthesisEngine(SpeechSynthesisRenderer renderer, Func<string> availabilityProbe, string? temporaryDirectory = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _availability = new Lazy<string>(
            availabilityProbe ?? throw new ArgumentNullException(nameof(availabilityProbe)),
            isThreadSafe: true);
        _temporaryDirectory = string.IsNullOrWhiteSpace(temporaryDirectory) ? Path.GetTempPath() : temporaryDirectory;
    }

    public string Id => "sapi";

    public bool IsAvailable => string.Equals(_availability.Value, "ok", StringComparison.Ordinal);

    public string UnavailableCode => _availability.Value;

    /// <summary>
    /// Live workers the caller already stopped waiting for. Bounded by
    /// <see cref="MaximumConcurrentWorkers"/>; each one still removes its own
    /// temporary file before it exits.
    /// </summary>
    public int AbandonedWorkers
    {
        get
        {
            lock (_gate)
            {
                PruneLocked();
                return _abandoned;
            }
        }
    }

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

        if (cancellationToken.IsCancellationRequested)
        {
            return SpeechSynthesisResult.Failed("speech-cancelled");
        }

        if (timeout <= TimeSpan.Zero)
        {
            return SpeechSynthesisResult.Failed("speech-timeout");
        }

        var path = Path.Combine(_temporaryDirectory, "overseer-speech-" + Guid.NewGuid().ToString("N") + ".wav");
        var result = SpeechSynthesisResult.Failed("speech-failed");
        var started = Environment.TickCount64;
        Worker worker;
        lock (_gate)
        {
            // A finished worker releases its slot; an abandoned one keeps its slot
            // until it returns, so threads and temporary files stay bounded.
            PruneLocked();
            if (_workers.Count >= MaximumConcurrentWorkers)
            {
                return SpeechSynthesisResult.Failed("speech-busy");
            }

            var thread = new Thread(() =>
            {
                try
                {
                    result = _renderer(script, path, timeout);
                }
                catch (Exception exception)
                {
                    result = SpeechSynthesisResult.Failed("speech-error:" + exception.GetType().Name);
                }
                finally
                {
                    // The worker owns the temporary file it created: the caller may
                    // have given up long ago, so only this finally deletes it.
                    TryDelete(path);
                }
            })
            {
                IsBackground = true,
                Name = "OVERSEER.Speech",
            };

            try
            {
                thread.SetApartmentState(ApartmentState.STA);
            }
            catch (PlatformNotSupportedException)
            {
            }

            worker = new Worker(thread);
            _workers.Add(worker);
            try
            {
                thread.Start();
            }
            catch (Exception exception) when (exception is ThreadStateException or OutOfMemoryException)
            {
                _workers.Remove(worker);
                return SpeechSynthesisResult.Failed("speech-error:" + exception.GetType().Name);
            }
        }

        started += (long)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        while (true)
        {
            // Bounded join slices: cancellation and the caller's deadline are both
            // observed while the worker runs, never only after it has finished.
            if (worker.Thread.Join(JoinSlice))
            {
                lock (_gate)
                {
                    _workers.Remove(worker);
                    PruneLocked();
                }

                return result;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Abandon(worker);
                return SpeechSynthesisResult.Failed("speech-cancelled");
            }

            if (Environment.TickCount64 >= started)
            {
                Abandon(worker);
                return SpeechSynthesisResult.Failed("speech-timeout");
            }
        }
    }

    private void Abandon(Worker worker)
    {
        lock (_gate)
        {
            if (worker.Abandoned)
            {
                return;
            }

            worker.Abandoned = true;
            _abandoned++;
        }
    }

    private void PruneLocked()
    {
        for (var index = _workers.Count - 1; index >= 0; index--)
        {
            var worker = _workers[index];
            if (worker.Thread.IsAlive)
            {
                continue;
            }

            if (worker.Abandoned)
            {
                _abandoned--;
            }

            _workers.RemoveAt(index);
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

    /// <summary>Stream synthesis: SpFileStream only, explicit English voice, bounded bytes.</summary>
    private static SpeechSynthesisResult RenderWithSapi(string script, string path, TimeSpan timeout)
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

            // Asynchronous speak with a bounded wait: a hanging engine ends this
            // worker by itself instead of holding its slot until the caller gives up.
            spVoice.Speak(script, SvsFlagsAsync);
            if (!spVoice.WaitUntilDone((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue)))
            {
                spVoice.Speak(string.Empty, SvsFPurgeBeforeSpeak);
                return SpeechSynthesisResult.Failed("speech-timeout");
            }

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

    private sealed class Worker(Thread thread)
    {
        public Thread Thread { get; } = thread;

        public bool Abandoned { get; set; }
    }
}
