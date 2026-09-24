using System.Net.Http;
using TheOverseer.App.Voice.Speech;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// Builds the one provider graph used everywhere: the user's assigned recording
/// wins, otherwise the provider the current settings select. There is deliberately
/// no automatic cross-provider fallback in this round, so a failing selected
/// provider is reported unavailable instead of quietly speaking another voice.
/// </summary>
public static class VoiceProviderFactory
{
    /// <summary>Assigned recording first, then exactly the selected provider.</summary>
    public static AssignedVoiceProvider Create(string dataDirectory, Func<VoiceSettings> settings, VoiceProviderBuilders? builders = null) =>
        new(dataDirectory, settings, new SelectedVoiceProvider(dataDirectory, settings, builders));

    /// <summary>One reusable TTS client: no redirects, no cookies, bounded by provider timeouts.</summary>
    public static HttpClient CreateTtsClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>
    /// Availability of the installed offline baseline, probed without synthesizing
    /// or playing anything. The code is a bounded reason such as
    /// <c>speech-no-english-voice</c> and is never empty.
    /// </summary>
    public static VoiceProviderStatus DescribeSpeechBaseline(ISpeechSynthesisEngine? engine = null)
    {
        var actual = engine ?? new SapiSpeechSynthesisEngine();
        if (actual.IsAvailable)
        {
            return new VoiceProviderStatus("speech", true, "speech-ok");
        }

        var code = actual.UnavailableCode;
        return new VoiceProviderStatus("speech", false, string.IsNullOrWhiteSpace(code) ? "speech-unavailable" : code);
    }
}
