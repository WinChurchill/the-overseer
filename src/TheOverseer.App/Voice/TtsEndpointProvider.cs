using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// Generic TTS client. It POSTs only the short notification JSON documented in
/// the plan, reads a bounded audio/wav response with a linked timeout, and never
/// logs or stores URLs, headers, request text or response bodies.
/// </summary>
public sealed class TtsEndpointProvider : IVoiceProvider, IDisposable
{
    private const int ReadChunkBytes = 16 * 1024;
    private static readonly string[] AcceptedContentTypes = { "audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave" };

    private readonly HttpClient _client;
    private readonly Func<VoiceSettings> _settings;
    private string _lastCode = "idle";

    public TtsEndpointProvider(HttpClient client, Func<VoiceSettings> settings)
    {
        _client = client;
        _settings = settings;
    }

    public string Id => "tts";

    public VoiceProviderStatus Status => new("tts", _settings().Endpoint is not null, _lastCode);

    /// <summary>Builds the exact request the endpoint contract allows.</summary>
    public static StringContent CreateContent(VoiceCandidate candidate)
    {
        var request = VoiceNotificationFormatter.ToRequest(candidate);
        var json = JsonSerializer.Serialize(new
        {
            text = request.Text,
            @event = request.EventCode,
            task = request.Task,
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return content;
    }

    public async ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (settings.ResolvedProvider != VoiceProviderKind.Tts || settings.Endpoint is null)
        {
            _lastCode = "no-endpoint";
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        var token = timeout.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
            {
                Content = CreateContent(candidate),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _lastCode = "http-" + (int)response.StatusCode;
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!IsAcceptedAudio(mediaType))
            {
                _lastCode = "content-type";
                return null;
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is > VoicePackValidator.MaximumClipBytes)
            {
                _lastCode = "audio-too-large";
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var audio = await ReadBoundedAsync(stream, VoicePackValidator.MaximumClipBytes, token).ConfigureAwait(false);
            if (audio is null)
            {
                _lastCode = "audio-too-large";
                return null;
            }

            var wav = VoicePackValidator.ValidateWav(audio);
            if (!wav.IsValid)
            {
                _lastCode = wav.ReasonCode;
                return null;
            }

            _lastCode = "ok";
            return new VoiceClip(audio, "tts", "tts");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _lastCode = "timeout";
            return null;
        }
        catch (OperationCanceledException)
        {
            _lastCode = "cancelled";
            return null;
        }
        catch (HttpRequestException)
        {
            _lastCode = "request-failed";
            return null;
        }
        catch (IOException)
        {
            _lastCode = "io";
            return null;
        }
        catch (JsonException)
        {
            _lastCode = "request-encode";
            return null;
        }
    }

    private static bool IsAcceptedAudio(string mediaType)
    {
        foreach (var accepted in AcceptedContentTypes)
        {
            if (string.Equals(mediaType, accepted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Streams at most <paramref name="maximumBytes"/>; the cap holds even without a Content-Length.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose() => _client.Dispose();
}
