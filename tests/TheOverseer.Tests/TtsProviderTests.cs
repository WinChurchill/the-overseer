using System.Net;
using System.Net.Http;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class TtsProviderTests
{
    private const string PrivateSentinel = "SENTINEL-PRIVATE-PROMPT";

    private static VoiceSettings Settings(string? endpoint = "https://tts.example.invalid/speak", double timeoutSeconds = 5) =>
        new() { Provider = "tts", Endpoint = endpoint, TimeoutSeconds = timeoutSeconds, Muted = false };

    private static VoiceCandidate Candidate(int number = 3, string label = PrivateSentinel) => new(
        VoiceEventKind.PermissionRequired,
        VoiceTestData.Key(),
        number,
        label,
        "turn-1",
        VoiceTestData.Start,
        "permission-requested",
        false,
        1,
        string.Empty);

    private static (TtsEndpointProvider Provider, FakeHttpMessageHandler Handler, HttpClient Client) Create(VoiceSettings settings)
    {
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new TtsEndpointProvider(client, () => settings), handler, client);
    }

    [Fact]
    public async Task SendsExactlyTheShortNotificationContract()
    {
        var settings = Settings();
        var (provider, handler, client) = Create(settings);
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Audio(VoicePackValidator.CreateTone(440, 0.05)));

        var clip = await provider.TryCreateAsync(Candidate(), CancellationToken.None);

        Assert.NotNull(clip);
        Assert.Equal("tts", clip!.SourceCode);
        Assert.Equal(1, handler.RequestCount);
        var body = Assert.Single(handler.Bodies);
        Assert.Equal("{\"text\":\"Task 3 needs your approval.\",\"event\":\"permission_required\",\"task\":\"Task 3\"}", body);
        Assert.DoesNotContain(PrivateSentinel, body, StringComparison.Ordinal);
        client.Dispose();
    }

    [Fact]
    public async Task NoEndpointMeansNoRequest()
    {
        var settings = Settings(endpoint: null);
        var (provider, handler, client) = Create(settings);

        var clip = await provider.TryCreateAsync(Candidate(), CancellationToken.None);

        Assert.Null(clip);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal("no-endpoint", provider.Status.LastCode);
        client.Dispose();
    }

    [Fact]
    public async Task PackModeNeverCallsTheEndpoint()
    {
        var settings = Settings() with { Provider = "pack" };
        var (provider, handler, client) = Create(settings);

        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
        client.Dispose();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "http-500")]
    [InlineData(HttpStatusCode.NotFound, "http-404")]
    [InlineData(HttpStatusCode.Found, "http-302")]
    public async Task NonSuccessStatusIsReportedWithoutBodyUse(HttpStatusCode status, string expected)
    {
        var (provider, handler, client) = Create(Settings());
        handler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("ignored"),
        });

        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal(expected, provider.Status.LastCode);
        client.Dispose();
    }

    [Fact]
    public async Task WrongContentTypeIsRejected()
    {
        var (provider, handler, client) = Create(Settings());
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Audio(VoicePackValidator.CreateTone(440, 0.05), "text/html"));

        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal("content-type", provider.Status.LastCode);
        client.Dispose();
    }

    [Fact]
    public async Task CorruptAudioIsRejected()
    {
        var (provider, handler, client) = Create(Settings());
        handler.Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Audio(new byte[128]));

        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal("wav-header", provider.Status.LastCode);
        client.Dispose();
    }

    [Fact]
    public async Task DeclaredOversizeIsRejectedWithoutReading()
    {
        var (provider, handler, client) = Create(Settings());
        handler.Responder = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[16]),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            response.Content.Headers.ContentLength = VoicePackValidator.MaximumClipBytes + 1;
            return Task.FromResult(response);
        };

        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal("audio-too-large", provider.Status.LastCode);
        client.Dispose();
    }

    [Fact]
    public async Task ChunkedOversizeBodyIsCutOffAtTheCap()
    {
        var (provider, handler, client) = Create(Settings());
        handler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new EndlessStreamContent(64 * 1024, 128),
        });

        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal("audio-too-large", provider.Status.LastCode);
        client.Dispose();
    }

    [Fact]
    public async Task TimeoutIsReportedAndLinkedToTheConfiguredValue()
    {
        var (provider, handler, client) = Create(Settings(timeoutSeconds: 1));
        handler.Responder = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return FakeHttpMessageHandler.Audio(VoicePackValidator.CreateTone(440, 0.05));
        };

        var started = Environment.TickCount64;
        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal("timeout", provider.Status.LastCode);
        Assert.True(elapsed < 8000, "timeout should be bounded by the configured value, took " + elapsed + "ms");
        client.Dispose();
    }

    [Fact]
    public async Task CallerCancellationIsDistinctFromTimeout()
    {
        var (provider, handler, client) = Create(Settings());
        using var cts = new CancellationTokenSource();
        handler.Responder = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return FakeHttpMessageHandler.Audio(VoicePackValidator.CreateTone(440, 0.05));
        };

        var task = provider.TryCreateAsync(Candidate(), cts.Token).AsTask();
        cts.CancelAfter(100);
        Assert.Null(await task);
        Assert.Equal("cancelled", provider.Status.LastCode);
        client.Dispose();
    }

    [Fact]
    public async Task FallbackRunsOnceAndOnlyWhenAllowed()
    {
        var ttsHandler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
        };
        var tts = new TtsEndpointProvider(new HttpClient(ttsHandler) { Timeout = Timeout.InfiniteTimeSpan }, () => Settings());
        var pack = new FakeVoiceProvider("pack");

        var withFallback = new FallbackVoiceProvider(tts, pack, allowFallback: true);
        var clip = await withFallback.TryCreateAsync(Candidate(), CancellationToken.None);
        Assert.NotNull(clip);
        Assert.Equal("fallback", clip!.DetailCode);
        Assert.Equal(1, withFallback.FallbackCount);
        Assert.Equal(1, pack.Calls);

        var withoutFallback = new FallbackVoiceProvider(tts, pack, allowFallback: false);
        Assert.Null(await withoutFallback.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal(1, pack.Calls);
    }

    [Fact]
    public async Task FailedFallbackIsSilentNotRetried()
    {
        var tts = new FakeVoiceProvider("tts", available: false);
        var pack = new FakeVoiceProvider("pack", available: false);
        var router = new FallbackVoiceProvider(tts, pack, allowFallback: true);

        Assert.Null(await router.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Null(await router.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal(2, pack.Calls);
        Assert.Equal(0, router.FallbackCount);
    }
}
