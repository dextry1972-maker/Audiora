using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;

namespace SonosFlow.Poc.Net;

/// <summary>
/// Serves the live PCM as an endless WAV over plain HTTP/1.1 on the LAN address only.
/// Uses a raw TcpListener (no HttpListener) so no URL ACL / admin rights are needed.
/// Only allow-listed peers (the Sonos players) may connect.
/// </summary>
public sealed class WavStreamServer : IDisposable
{
    public const string StreamPath = "/sonosflow.wav";
    public const string MimeType = "audio/wav";

    private readonly TcpListener _listener;
    private readonly WaveFormat _format;
    private readonly HashSet<IPAddress> _allowed;
    private readonly List<Client> _clients = [];
    private readonly CancellationTokenSource _cts = new();

    public IPEndPoint Endpoint { get; }
    public string StreamUrl => $"http://{Endpoint.Address}:{Endpoint.Port}{StreamPath}";
    public int ClientCount { get { lock (_clients) return _clients.Count; } }
    public long BytesSent;
    public long DroppedChunks;
    public long RejectedConnections;
    public string? LastClientInfo { get; private set; }

    public WavStreamServer(IPAddress bindAddress, int port, WaveFormat format, IEnumerable<IPAddress> allowedPeers)
    {
        _format = format;
        _allowed = [.. allowedPeers];
        _listener = new TcpListener(bindAddress, port);
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Fan-out live PCM to all connected players. Never blocks the audio thread.</summary>
    public void Write(ReadOnlyMemory<byte> pcm)
    {
        lock (_clients)
        {
            if (_clients.Count == 0) return;
            var copy = pcm.ToArray();
            foreach (var c in _clients)
                if (!c.Queue.Writer.TryWrite(copy)) Interlocked.Increment(ref DroppedChunks);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }

            var peer = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            if (!_allowed.Contains(peer))
            {
                Interlocked.Increment(ref RejectedConnections);
                tcp.Dispose();
                continue;
            }
            _ = HandleClientAsync(tcp, peer, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, IPAddress peer, CancellationToken ct)
    {
        using var _ = tcp;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();

        var (method, path, headers) = await ReadRequestAsync(stream, ct);
        if (method is null) return;
        LastClientInfo = $"{peer} {method} {path} UA={headers.GetValueOrDefault("user-agent", "?")}";

        if (!path!.StartsWith(StreamPath, StringComparison.Ordinal) || (method != "GET" && method != "HEAD"))
        {
            await WriteAsciiAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
            return;
        }

        await WriteAsciiAsync(stream,
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {MimeType}\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "Accept-Ranges: none\r\n" +
            "transferMode.dlna.org: Streaming\r\n" +
            "contentFeatures.dlna.org: DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\r\n" +
            "Server: SonosFlow/0.1\r\n" +
            "Connection: close\r\n\r\n", ct);
        if (method == "HEAD") return;

        await stream.WriteAsync(BuildWavHeader(_format), ct);

        // ~2 s of 10 ms chunks; a full queue means the player stopped reading.
        var client = new Client(Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        }));
        lock (_clients) _clients.Add(client);
        try
        {
            await foreach (var chunk in client.Queue.Reader.ReadAllAsync(ct))
            {
                await stream.WriteAsync(chunk, ct);
                Interlocked.Add(ref BytesSent, chunk.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Player closed the connection (stop, track change or network drop).
        }
        finally
        {
            lock (_clients) _clients.Remove(client);
        }
    }

    private static async Task<(string? Method, string? Path, Dictionary<string, string> Headers)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int total = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (total < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
                if (n == 0) break;
                total += n;
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n")) break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return (null, null, []);
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return (null, null, []);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) headers[line[..idx].Trim().ToLowerInvariant()] = line[(idx + 1)..].Trim();
        }
        return (parts[0], parts[1], headers);
    }

    private static byte[] BuildWavHeader(WaveFormat f)
    {
        // Endless stream: sizes set to the maximum, as live WAV streamers commonly do.
        using var ms = new MemoryStream(44);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(uint.MaxValue);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)f.Channels);
        w.Write(f.SampleRate);
        w.Write(f.AverageBytesPerSecond);
        w.Write((short)f.BlockAlign);
        w.Write((short)f.BitsPerSample);
        w.Write("data"u8);
        w.Write(uint.MaxValue - 36);
        return ms.ToArray();
    }

    private static Task WriteAsciiAsync(NetworkStream s, string text, CancellationToken ct) =>
        s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients)
            foreach (var c in _clients) c.Queue.Writer.TryComplete();
    }

    private sealed record Client(Channel<byte[]> Queue);
}
