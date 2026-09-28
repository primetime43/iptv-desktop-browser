using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DesktopApp.Services;

// External players need a master playlist to keep separate audio/subtitle tracks.
// Serve just that small manifest from memory; the player fetches media directly
// from the provider. Credential-bearing playlists are never written to disk.
public sealed class PlaybackManifestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _clients = new(8);
    private readonly Dictionary<string, byte[]> _manifests = new();
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private bool _disposed;
    private readonly int _port;

    public PlaybackManifestServer()
    {
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync(_lifetime.Token);
    }

    public Uri Publish(string manifest)
    {
        var data = Encoding.UTF8.GetBytes(manifest);
        if (data.Length > StreamQualityService.MaximumManifestBytes * 2) throw new ArgumentException("Playlist too large.", nameof(manifest));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var path = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "/master.m3u8";
            _manifests[path] = data;
            _order.Enqueue(path);
            while (_order.Count > 32) _manifests.Remove(_order.Dequeue());
            return new Uri($"http://127.0.0.1:{_port}{path}");
        }
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (!_clients.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken lifetime)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var token = timeout.Token;
            try
            {
                var stream = client.GetStream();
                var header = new byte[8192];
                var length = 0;
                while (length < header.Length)
                {
                    var read = await stream.ReadAsync(header.AsMemory(length), token).ConfigureAwait(false);
                    if (read == 0) return;
                    length += read;
                    if (Encoding.ASCII.GetString(header, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var request = Encoding.ASCII.GetString(header, 0, length);
                if (!request.Contains("\r\n\r\n", StringComparison.Ordinal)) return;
                var parts = request.Split("\r\n", 2)[0].Split(' ');
                byte[]? data = null;
                if (parts.Length == 3 && parts[0] is "GET" or "HEAD")
                    lock (_gate) _manifests.TryGetValue(parts[1], out data);
                var status = data == null ? "404 Not Found" : "200 OK";
                data ??= [];
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/vnd.apple.mpegurl\r\nContent-Length: {data.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, token).ConfigureAwait(false);
                if (parts.Length == 3 && parts[0] == "GET") await stream.WriteAsync(data, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            finally { _clients.Release(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _manifests.Clear(); _order.Clear();
            _lifetime.Cancel(); _listener.Stop(); _lifetime.Dispose();
        }
    }
}
