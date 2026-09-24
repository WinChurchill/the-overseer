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

        public Func<string, SpeechSynthesisResult>? Result { get; set; }

        public SpeechSynthesisResult Synthesize(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            Scripts.Add(script);
            LastToken = cancellationToken;
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

        // A repeat is served from the bounded cache: no second synthesis.
        _ = await provider.TryCreateAsync(Candidate(VoiceEventKind.TaskCompleted), CancellationToken.None);
        Assert.Equal(9, engine.Calls);
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
}
