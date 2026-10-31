using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;

namespace SonosFlow.Core.Net;

/// <summary>
/// Serves the live PCM as an endless WAV over plain HTTP/1.1 on the LAN address only.
/// Uses a raw TcpListener (no HttpListener) so no URL ACL / admin rights are needed.
/// Only allow-listed peers (the Sonos players) may connect.
/// Optionally keeps the last N ms of audio and sends it as soon as a player connects
/// ("margen anti-cortes"): the player then plays N ms behind live and can ride out network
/// pauses shorter than that instead of running dry.
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
    private readonly byte[] _history;   // ring with the most recent audio, for the prebuffer
    private int _historyStart, _historyCount;
    private long _maxBacklogBytes;

    public IPEndPoint Endpoint { get; }
    public string StreamUrl => $"http://{Endpoint.Address}:{Endpoint.Port}{StreamPath}";
    public int ClientCount { get { lock (_clients) return _clients.Count; } }
    public long BytesSent;
    public long DroppedChunks;
    public long RejectedConnections;
    public string? LastClientInfo { get; private set; }
    public int PrebufferMs { get; }

    /// <summary>Raised (on a pool thread) when a player connects or disconnects, for the logs.</summary>
    public event Action<string>? ConnectionEvent;

    /// <param name="prebufferMs">Audio sent at once when a player connects (0 = pure live, minimum latency).</param>
    public WavStreamServer(IPAddress bindAddress, int port, WaveFormat format, IEnumerable<IPAddress> allowedPeers, int prebufferMs = 0)
    {
        _format = format;
        PrebufferMs = Math.Max(0, prebufferMs);
        int historyBytes = (int)((long)format.AverageBytesPerSecond * PrebufferMs / 1000);
        _history = new byte[historyBytes - historyBytes % format.BlockAlign];
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
            Remember(pcm.Span);
            if (_clients.Count == 0) return;
            var copy = pcm.ToArray();
            foreach (var c in _clients)
            {
                if (!c.Queue.Writer.TryWrite(copy)) { Interlocked.Increment(ref DroppedChunks); continue; }
                long pending = Interlocked.Add(ref c.PendingBytes, copy.Length);
                if (pending > _maxBacklogBytes) _maxBacklogBytes = pending;
            }
        }
    }

    /// <summary>
    /// Largest amount of audio (ms) waiting to be taken by a player since the previous call. Normal live
    /// playback keeps it near one packet; it grows only while the network or the player stops reading.
    /// </summary>
    public double TakeMaxBacklogMs()
    {
        lock (_clients)
        {
            long max = _maxBacklogBytes;
            _maxBacklogBytes = _clients.Count == 0 ? 0 : _clients.Max(c => Interlocked.Read(ref c.PendingBytes));
            return max * 1000.0 / _format.AverageBytesPerSecond;
        }
    }

    private void Remember(ReadOnlySpan<byte> data)
    {
        if (_history.Length == 0) return;
        if (data.Length >= _history.Length)
        {
            data[^_history.Length..].CopyTo(_history);
            _historyStart = 0;
            _historyCount = _history.Length;
            return;
        }
        int end = (_historyStart + _historyCount) % _history.Length;
        int first = Math.Min(data.Length, _history.Length - end);
        data[..first].CopyTo(_history.AsSpan(end));
        data[first..].CopyTo(_history);
        _historyCount += data.Length;
        if (_historyCount > _history.Length)
        {
            _historyStart = (_historyStart + _historyCount - _history.Length) % _history.Length;
            _historyCount = _history.Length;
        }
    }

    /// <summary>
    /// The full margin: the Sonos connects ~0.2–0.5 s after the stream starts, before the ring has filled,
    /// so the missing part goes first as silence (zero bytes are silence in PCM and float).
    /// </summary>
    private byte[] HistorySnapshot()
    {
        var copy = new byte[_history.Length];
        int pad = _history.Length - _historyCount;
        int first = Math.Min(_historyCount, _history.Length - _historyStart);
        _history.AsSpan(_historyStart, first).CopyTo(copy.AsSpan(pad));
        _history.AsSpan(0, _historyCount - first).CopyTo(copy.AsSpan(pad + first));
        return copy;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (SocketException)
            {
                // Transient (e.g. the adapter is reconfiguring): don't spin.
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

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
        var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        LastClientInfo = $"{peer} {method} {path} UA={headers.GetValueOrDefault("user-agent", "?")}";
        ConnectionEvent?.Invoke($"conexión {remote} {method} {path}" +
            (headers.TryGetValue("range", out var range) ? $" Range={range}" : "") +
            $" UA={headers.GetValueOrDefault("user-agent", "?")}");
        var connectedAt = DateTime.UtcNow;

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
            "Server: SonosFlow/1.0\r\n" +
            "Connection: close\r\n\r\n", ct);
        if (method == "HEAD") return;

        await stream.WriteAsync(BuildWavHeader(_format), ct);

        // 200 chunks (≈2 s of 10 ms app-capture chunks, more with larger device packets) plus the prebuffer;
        // TryWrite stays non-blocking; Wait mode reports a full queue instead of silently dropping a write.
        using var client = new Client(Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        }), CancellationTokenSource.CreateLinkedTokenSource(ct));
        lock (_clients)
        {
            // Prebuffer and live audio are queued under the same lock: no gap and no duplicate between them.
            var prebuffer = HistorySnapshot();
            if (prebuffer.Length > 0 && client.Queue.Writer.TryWrite(prebuffer)) client.PendingBytes = prebuffer.Length;
            _clients.Add(client);
        }
        string reason = "fin normal";
        try
        {
            await foreach (var chunk in client.Queue.Reader.ReadAllAsync(client.Abort.Token))
            {
                await stream.WriteAsync(chunk, client.Abort.Token);
                Interlocked.Exchange(ref client.LastProgressTicks, Environment.TickCount64);
                Interlocked.Add(ref client.PendingBytes, -chunk.Length);
                Interlocked.Add(ref BytesSent, chunk.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Player closed the connection (stop, track change or network drop), or we aborted a stuck one.
            reason = ex is not OperationCanceledException ? ex.GetBaseException().Message
                : ct.IsCancellationRequested ? "detenido" : "cerrada por el vigilante (no recogía audio)";
        }
        finally
        {
            lock (_clients) _clients.Remove(client);
            ConnectionEvent?.Invoke($"desconexión {remote} tras {(DateTime.UtcNow - connectedAt).TotalSeconds:0} s: {reason}");
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

    /// <summary>
    /// Time since any connected player last took audio (a socket write completed). Grows only when the
    /// network or the player stops reading — never during silence, which is sent like any other audio.
    /// </summary>
    public TimeSpan SinceLastClientProgress
    {
        get
        {
            lock (_clients)
            {
                if (_clients.Count == 0) return TimeSpan.Zero;
                long newest = _clients.Max(c => Interlocked.Read(ref c.LastProgressTicks));
                return TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - newest));
            }
        }
    }

    /// <summary>Closes every player connection (including one stuck in a socket write) so the stream can be re-issued.</summary>
    public void AbortClients()
    {
        List<Client> clients;
        lock (_clients) clients = [.. _clients];
        foreach (var c in clients)
        {
            // Outside the lock: cancellation callbacks may run inline. A client that just ended is already disposed.
            try { c.Abort.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private sealed class Client(Channel<byte[]> queue, CancellationTokenSource abort) : IDisposable
    {
        public Channel<byte[]> Queue { get; } = queue;
        public CancellationTokenSource Abort { get; } = abort;
        public long PendingBytes; // queued for this player but not yet written to its socket
        public long LastProgressTicks = Environment.TickCount64;
        public void Dispose() => Abort.Dispose();
    }
}
