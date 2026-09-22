using System.Net;
using System.Net.Http;
using System.Text;

namespace TheOverseer.Tests.Support;

/// <summary>Scripted HTTP handler for provider tests. Never touches the network.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<string> _bodies = new();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }

    public int RequestCount { get; private set; }

    public IReadOnlyList<string> Bodies => _bodies;

    public static HttpResponseMessage Audio(byte[] bytes, string mediaType = "audio/wav", bool contentLength = true, bool chunked = false)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        if (!contentLength)
        {
            content.Headers.ContentLength = null;
        }

        // A chunked response simply has no declared length from the caller's view.
        _ = chunked;

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (request.Content is not null)
        {
            _bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        if (Responder is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return await Responder(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Streaming content that reports no length and produces an unbounded body.</summary>
public sealed class EndlessStreamContent : HttpContent
{
    private readonly int _chunkBytes;
    private readonly int _maximumChunks;

    public EndlessStreamContent(int chunkBytes, int maximumChunks)
    {
        _chunkBytes = chunkBytes;
        _maximumChunks = maximumChunks;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
    }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        var chunk = new byte[_chunkBytes];
        Array.Fill(chunk, (byte)7);
        for (var index = 0; index < _maximumChunks; index++)
        {
            await stream.WriteAsync(chunk).ConfigureAwait(false);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
