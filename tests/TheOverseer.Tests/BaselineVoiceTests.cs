using TheOverseer.App.Voice.Speech;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class BaselineVoiceTests
{
    private sealed class FakeSpeechEngine : ISpeechSynthesisEngine
    {
        public string Id => "fake-speech";

        public bool IsAvailable { get; set; } = true;

        public string UnavailableCode { get; set; } = "speech-no-english-voice";

        public int Calls { get; private set; }

        public List<string> Scripts { get; } = new();

        public CancellationToken LastToken { get; private set; }

        public TimeSpan LastTimeout { get; private set; }

        /// <summary>Set while a synthesis is inside the engine, so a test can act in flight.</summary>
        public ManualResetEventSlim? Entered { get; set; }

        /// <summary>Blocks the engine until the test releases it.</summary>
        public ManualResetEventSlim? Release { get; set; }

        public Func<string, SpeechSynthesisResult>? Result { get; set; }

        public SpeechSynthesisResult Synthesize(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            Scripts.Add(script);
            LastToken = cancellationToken;
            LastTimeout = timeout;
            Entered?.Set();
            Release?.Wait(TimeSpan.FromSeconds(10));
            if (!IsAvailable)
            {
                return SpeechSynthesisResult.Failed(UnavailableCode);
            }

            return Result?.Invoke(script) ?? new SpeechSynthesisResult(VoicePackValidator.CreateTone(440, 0.05), "speech-ok");
        }
    }

    private static VoiceCandidate Candidate(VoiceEventKind kind) => new(
        kind,
        TaskKey.For(HarnessIds.Codex, "codex", "01a0e000-0000-0000-0000-000000000101"),
        1,
        "Task 1",
        "turn-1",
        DateTimeOffset.UtcNow,
        "code",
        false,
        1,
        string.Empty);

    private static WindowsSpeechVoiceProvider Provider(FakeSpeechEngine engine, string provider = "speech") =>
        new(engine, () => new VoiceSettings { Provider = provider, Muted = false });

    [Fact]
    public async Task EachCatalogScriptSynthesizesOnceAndIsCached()
    {
        var engine = new FakeSpeechEngine();
        using var provider = Provider(engine);

        foreach (var kind in Enum.GetValues<VoiceEventKind>())
        {
            var clip = await provider.TryCreateAsync(Candidate(kind), CancellationToken.None);
            Assert.NotNull(clip);
            Assert.True(VoicePackValidator.ValidateWav(clip!.Wav).IsValid);
            Assert.StartsWith("speech:", clip.SourceCode, StringComparison.Ordinal);
        }

        Assert.Equal(9, engine.Calls);
        Assert.Equal(9, engine.Scripts.Distinct(StringComparer.Ordinal).Count());

        // A repeat is served from the bounded cache: no second synthesis, and the
        // cache never grows past the nine fixed scripts.
        _ = await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None);
        Assert.Equal(9, engine.Calls);
        Assert.Contains("cache=9", provider.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingEnglishVoiceIsVisibleAndSilent()
    {
        var engine = new FakeSpeechEngine { IsAvailable = false, UnavailableCode = "speech-no-english-voice" };
        using var provider = Provider(engine);

        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskError), CancellationToken.None));
        Assert.Equal(0, engine.Calls);
        Assert.Equal("speech-no-english-voice", provider.Status.LastCode);
        Assert.False(provider.Status.IsAvailable);
    }

    [Fact]
    public async Task UnvalidatedAudioIsRejectedInsteadOfPlayed()
    {
        var engine = new FakeSpeechEngine { Result = _ => new SpeechSynthesisResult(new byte[256], "speech-ok") };
        using var provider = Provider(engine);

        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None));
        Assert.StartsWith("speech-invalid-wav", provider.Status.LastCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineFailuresAreReportedWithoutThrowing()
    {
        var engine = new FakeSpeechEngine { Result = _ => SpeechSynthesisResult.Failed("speech-timeout") };
        using var provider = Provider(engine);

        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None));
        Assert.Equal("speech-timeout", provider.Status.LastCode);
    }

    [Fact]
    public async Task TheCallersCancellationTokenReachesTheEngine()
    {
        var engine = new FakeSpeechEngine();
        using var provider = Provider(engine);
        using var cancellation = new CancellationTokenSource();

        _ = await provider.TryCreateAsync(Candidate(VoiceEventKind.PermissionRequired), cancellation.Token);

        Assert.Equal(cancellation.Token, engine.LastToken);
    }

    [Fact]
    public void TheBaselineProviderOnlyProducesAudioAndNeverPlaysIt()
    {
        var interfaces = typeof(WindowsSpeechVoiceProvider).GetInterfaces();

        Assert.Contains(typeof(IVoiceProvider), interfaces);
        Assert.DoesNotContain(typeof(IVoicePlayer), interfaces);
    }

    [Theory]
    [InlineData("speech", VoiceProviderKind.Speech, "speech")]
    [InlineData("baseline", VoiceProviderKind.Speech, "speech")]
    [InlineData("SPEECH", VoiceProviderKind.Speech, "speech")]
    [InlineData("pack", VoiceProviderKind.Pack, "pack")]
    [InlineData("tts", VoiceProviderKind.Tts, "tts")]
    [InlineData("unknown-thing", VoiceProviderKind.Pack, "pack")]
    public void SelectingBaselineNeverResetsAnExistingChoice(string configured, VoiceProviderKind expected, string name)
    {
        var settings = new VoiceSettings { Provider = configured, Muted = true };

        Assert.Equal(expected, settings.ResolvedProvider);
        Assert.Equal(name, settings.ProviderName);
        Assert.True(settings.Muted);
    }
    [Fact]
    public async Task StatusNeverClaimsTheBaselineIsAvailableWithoutAnEnglishVoice()
    {
        var engine = new FakeSpeechEngine();
        using var provider = Provider(engine);
        Assert.NotNull(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None));
        Assert.True(provider.Status.IsAvailable);

        engine.IsAvailable = false;
        engine.UnavailableCode = "speech-no-english-voice";

        var status = provider.Status;
        Assert.False(status.IsAvailable);
        Assert.Equal("speech-no-english-voice", status.LastCode);
        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None));
    }

    [Fact]
    public async Task AFailedOrTimedOutSynthesisIsNeverCached()
    {
        var engine = new FakeSpeechEngine { Result = _ => SpeechSynthesisResult.Failed("speech-timeout") };
        using var provider = Provider(engine);

        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None));
        Assert.Equal("speech-timeout", provider.Status.LastCode);

        // The next request retries instead of being poisoned by the earlier hang.
        engine.Result = _ => new SpeechSynthesisResult(VoicePackValidator.CreateTone(440, 0.05), "speech-ok");
        Assert.NotNull(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None));

        Assert.Equal(2, engine.Calls);
        Assert.Equal("speech-ok", provider.Status.LastCode);
    }

    [Fact]
    public async Task TheConfiguredVoiceTimeoutReachesTheEngineBoundedByTheProviderCap()
    {
        var settings = new VoiceSettings { Provider = "speech", TimeoutSeconds = 2 };

        var capped = new FakeSpeechEngine();
        using (var provider = new WindowsSpeechVoiceProvider(capped, () => settings, TimeSpan.FromSeconds(1)))
        {
            _ = await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(1), capped.LastTimeout);
        }

        var configured = new FakeSpeechEngine();
        using (var provider = new WindowsSpeechVoiceProvider(configured, () => settings))
        {
            _ = await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(2), configured.LastTimeout);
        }
    }

    [Fact]
    public async Task DisposingDuringSynthesisIsSafeAndNeverRepopulatesTheCache()
    {
        var engine = new FakeSpeechEngine
        {
            Entered = new ManualResetEventSlim(false),
            Release = new ManualResetEventSlim(false),
        };
        using var provider = Provider(engine);
        var inFlight = provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None).AsTask();
        Assert.True(engine.Entered!.Wait(TimeSpan.FromSeconds(5)), "the engine never started");

        provider.Dispose();
        engine.Release!.Set();

        // The in-flight caller still gets its clip, but disposal wins afterwards.
        Assert.NotNull(await inFlight);
        Assert.False(provider.Status.IsAvailable);
        Assert.Equal("speech-disposed", provider.Status.LastCode);
        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskError), CancellationToken.None));
        Assert.Equal(1, engine.Calls);
        engine.Entered.Dispose();
        engine.Release.Dispose();
    }

    [Fact]
    public void AHungEngineTimesOutWithinItsBoundAndItsWorkerDeletesItsOwnFile()
    {
        using var home = new SyntheticCodexHome();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var finished = new ManualResetEventSlim(false);
        var engine = new SapiSpeechSynthesisEngine(
            (script, path, timeout) =>
            {
                // The worker owns the file it writes: the caller must never delete it
                // while the renderer is still running.
                File.WriteAllBytes(path, new byte[64]);
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(15));
                finished.Set();
                return new SpeechSynthesisResult(VoicePackValidator.CreateTone(440, 0.05), "speech-ok");
            },
            () => "ok",
            home.Root);

        var started = Environment.TickCount64;
        var result = engine.Synthesize("Task completed.", TimeSpan.FromMilliseconds(400), CancellationToken.None);
        var elapsed = Environment.TickCount64 - started;

        Assert.False(result.IsValid);
        Assert.Equal("speech-timeout", result.Code);
        Assert.True(entered.IsSet, "the worker never reached the renderer");
        Assert.False(finished.IsSet, "the renderer returned before the caller's bound, so this was not a hang");
        Assert.True(elapsed < 3000, "the caller's timeout must bound the wait; took " + elapsed + "ms");

        // The abandoned worker is still writing its own file: it must exist.
        Assert.Single(Directory.GetFiles(home.Root, "*.wav"));
        Assert.Equal(1, engine.AbandonedWorkers);

        release.Set();
        Assert.True(WaitUntil(() => Directory.GetFiles(home.Root, "*.wav").Length == 0, 5000), "the worker must delete its own temporary file");
        Assert.True(WaitUntil(() => engine.AbandonedWorkers == 0, 5000), "the abandoned slot must be released when the worker returns");
    }

    [Fact]
    public async Task CancellingWhileWaitingReturnsPromptlyAndTheWorkerStillCleansUp()
    {
        using var home = new SyntheticCodexHome();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var engine = new SapiSpeechSynthesisEngine(
            (script, path, timeout) =>
            {
                File.WriteAllBytes(path, new byte[64]);
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(15));
                return new SpeechSynthesisResult(VoicePackValidator.CreateTone(440, 0.05), "speech-ok");
            },
            () => "ok",
            home.Root);

        using var cancellation = new CancellationTokenSource();
        var call = Task.Run(() => engine.Synthesize("Task completed.", TimeSpan.FromSeconds(30), cancellation.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the worker never reached the renderer");

        var started = Environment.TickCount64;
        cancellation.Cancel();
        var winner = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(ReferenceEquals(call, winner), "cancellation must be observed while waiting, not after the worker finishes");
        var elapsed = Environment.TickCount64 - started;
        var result = await call;

        Assert.Equal("speech-cancelled", result.Code);
        Assert.True(elapsed < 2000, "cancellation must return promptly; took " + elapsed + "ms");
        Assert.Single(Directory.GetFiles(home.Root, "*.wav"));

        release.Set();
        Assert.True(WaitUntil(() => Directory.GetFiles(home.Root, "*.wav").Length == 0, 5000), "the worker must delete its own temporary file after cancellation");
    }

    [Fact]
    public void RepeatedCancellationNeverStartsAWorkerOrLeavesAFile()
    {
        using var home = new SyntheticCodexHome();
        var calls = 0;
        var engine = new SapiSpeechSynthesisEngine(
            (script, path, timeout) =>
            {
                Interlocked.Increment(ref calls);
                return new SpeechSynthesisResult(VoicePackValidator.CreateTone(440, 0.05), "speech-ok");
            },
            () => "ok",
            home.Root);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = engine.Synthesize("Task completed.", TimeSpan.FromSeconds(1), cancellation.Token);
            Assert.Equal("speech-cancelled", result.Code);
        }

        Assert.Equal(0, calls);
        Assert.Empty(Directory.GetFiles(home.Root, "*.wav"));
    }

    [Fact]
    public async Task ConcurrentSynthesisIsCappedAndRecoversWhenTheWorkersFinish()
    {
        using var home = new SyntheticCodexHome();
        using var bothRunning = new CountdownEvent(SapiSpeechSynthesisEngine.MaximumConcurrentWorkers);
        using var release = new ManualResetEventSlim(false);
        var gate = new object();
        var running = 0;
        var observedMaximum = 0;
        var calls = 0;
        var engine = new SapiSpeechSynthesisEngine(
            (script, path, timeout) =>
            {
                lock (gate)
                {
                    calls++;
                    running++;
                    observedMaximum = Math.Max(observedMaximum, running);
                }

                File.WriteAllBytes(path, new byte[64]);
                if (bothRunning.CurrentCount > 0)
                {
                    bothRunning.Signal();
                }

                release.Wait(TimeSpan.FromSeconds(15));
                lock (gate)
                {
                    running--;
                }

                return new SpeechSynthesisResult(VoicePackValidator.CreateTone(440, 0.05), "speech-ok");
            },
            () => "ok",
            home.Root);

        var callsInFlight = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => engine.Synthesize("Task completed.", TimeSpan.FromSeconds(30), CancellationToken.None)))
            .ToArray();

        Assert.True(bothRunning.Wait(TimeSpan.FromSeconds(5)), "exactly the capped number of workers must be admitted");
        Assert.True(WaitUntil(() => callsInFlight.Count(call => call.IsCompleted) >= 4, 3000), "calls beyond the cap must return immediately");

        var busy = 0;
        foreach (var call in callsInFlight.Where(call => call.IsCompleted))
        {
            Assert.Equal("speech-busy", (await call).Code);
            busy++;
        }

        Assert.Equal(4, busy);
        Assert.Equal(SapiSpeechSynthesisEngine.MaximumConcurrentWorkers, calls);
        Assert.Equal(SapiSpeechSynthesisEngine.MaximumConcurrentWorkers, observedMaximum);

        release.Set();
        var all = Task.WhenAll(callsInFlight);
        var winner = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(all, winner), "every call must finish once its worker returns");
        var results = await all;
        Assert.Equal(SapiSpeechSynthesisEngine.MaximumConcurrentWorkers, results.Count(result => result.IsValid));
        Assert.True(WaitUntil(() => Directory.GetFiles(home.Root, "*.wav").Length == 0, 5000), "every worker must delete its own file");

        var recovered = engine.Synthesize("Task completed.", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(recovered.IsValid, "the busy state must clear once the workers finish: " + recovered.Code);
    }

    [Fact]
    public void TheInstalledEngineEitherSynthesizesAValidWavOrReportsAConcreteReason()
    {
        // This test always runs and never asserts a particular machine's voice pack:
        // it either proves real offline synthesis or proves the honest unavailable path.
        var engine = new SapiSpeechSynthesisEngine();

        if (!engine.IsAvailable)
        {
            Assert.False(string.IsNullOrWhiteSpace(engine.UnavailableCode));
            Assert.StartsWith("speech-", engine.UnavailableCode, StringComparison.Ordinal);
            var unavailable = engine.Synthesize("Task completed.", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(unavailable.IsValid);
            Assert.Equal(engine.UnavailableCode, unavailable.Code);
            return;
        }

        var result = engine.Synthesize("Task completed.", TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.True(result.IsValid, "an available English engine must synthesize: " + result.Code);
        var validation = VoicePackValidator.ValidateWav(result.Wav!);
        Assert.True(validation.IsValid, "synthesized audio failed validation: " + validation.ReasonCode);
        Assert.True(result.Wav!.Length > 44, "the synthesized WAV must carry audio payload, not just a header");
    }

    private static bool WaitUntil(Func<bool> condition, int milliseconds)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < milliseconds)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }
}
