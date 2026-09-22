using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TheOverseer.App.Diagnostics;

public sealed record LoopbackResponse(
    int Status,
    string ContentType,
    byte[] Body,
    TimeSpan Delay,
    bool OmitLength);

/// <summary>
/// Owned loopback TTS fixture: a tiny HTTP/1.1 server on 127.0.0.1 used to prove
/// the real HttpClient contract without any external service, account or
/// credential. Request bodies are captured for assertions only and are never
/// logged; only field names and sizes are printed by the smoke.
/// </summary>
public sealed class LoopbackTtsServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<int, string, LoopbackResponse> _responder;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _bodies = new();
    private readonly Task _acceptTask;
    private int _requests;

    private LoopbackTtsServer(TcpListener listener, Func<int, string, LoopbackResponse> responder)
    {
        _listener = listener;
        _responder = responder;
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptAsync(_lifetime.Token));
    }

    public string Endpoint { get; private set; } = string.Empty;

    public int RequestCount => Volatile.Read(ref _requests);

    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_bodies)
            {
                return _bodies.ToArray();
            }
        }
    }

    public static LoopbackTtsServer Start(Func<int, string, LoopbackResponse> responder)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var server = new LoopbackTtsServer(listener, responder);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        server.Endpoint = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/tts";
        return server;
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var connection = client;
        try
        {
            await using var stream = client.GetStream();
            var headerText = new StringBuilder();
            var buffer = new byte[4096];
            var contentLength = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                headerText.Append(Encoding.ASCII.GetString(buffer, 0, read));
                var text = headerText.ToString();
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    continue;
                }

                foreach (var line in text[..headerEnd].Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(line[15..].Trim(), out contentLength);
                    }
                }

                var bodyStart = headerEnd + 4;
                var body = text.Length > bodyStart ? text[bodyStart..] : string.Empty;
                while (body.Length < contentLength)
                {
                    var more = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (more <= 0)
                    {
                        break;
                    }

                    body += Encoding.UTF8.GetString(buffer, 0, more);
                }

                lock (_bodies)
                {
                    _bodies.Add(body);
                }

                var index = Interlocked.Increment(ref _requests) - 1;
                var response = _responder(index, body);
                if (response.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(response.Delay, cancellationToken).ConfigureAwait(false);
                }

                var statusText = response.Status == 200 ? "OK" : "Error";
                var header = "HTTP/1.1 " + response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + statusText + "\r\n" +
                             "Content-Type: " + response.ContentType + "\r\n" +
                             "Connection: close\r\n" +
                             (response.OmitLength ? string.Empty : "Content-Length: " + response.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n") +
                             "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        try
        {
            _acceptTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _lifetime.Dispose();
    }
}
