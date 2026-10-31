using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;
using SonosFlow.Core.Net;

namespace SonosFlow.Core.Tests;

/// <summary>
/// The stream server as the Sonos sees it, over loopback. Audio frames carry a running counter so
/// any gap or duplicate between the prebuffer and live audio is detected exactly.
/// </summary>
public sealed class WavStreamServerTests
{
    private static readonly WaveFormat Format = new(48000, 16, 2);
    private const int FramesPer10Ms = 480;

    private static byte[] Frames(int first, int count)
    {
        var b = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            short v = (short)((first + i) & 0x7FFF);
            BitConverter.TryWriteBytes(b.AsSpan(i * 4), v);
            BitConverter.TryWriteBytes(b.AsSpan(i * 4 + 2), v);
        }
        return b;
    }

    private static WavStreamServer Server(int prebufferMs) =>
        new(IPAddress.Loopback, 0, Format, [IPAddress.Loopback], prebufferMs);

    /// <summary>Connects like a player and returns the stream positioned right after the WAV header.</summary>
    private static async Task<(TcpClient Tcp, NetworkStream Stream, List<byte> Extra)> ConnectAsync(WavStreamServer server)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET /sonosflow.wav HTTP/1.1\r\nHost: t\r\n\r\n"));
        var got = new List<byte>();
        var buf = new byte[8192];
        int headerEnd;
        while ((headerEnd = IndexOf(got, "\r\n\r\n"u8.ToArray())) < 0 || got.Count < headerEnd + 4 + 44)
        {
            int n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(n > 0, "conexión cerrada antes de la cabecera");
            got.AddRange(buf.AsSpan(0, n).ToArray());
        }
        Assert.StartsWith("HTTP/1.1 200 OK", Encoding.ASCII.GetString(got.ToArray(), 0, headerEnd));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(got.ToArray(), headerEnd + 4, 4));
        return (tcp, s, got.Skip(headerEnd + 4 + 44).ToList());
    }

    private static async Task<List<short>> ReadFramesAsync(NetworkStream s, List<byte> extra, int frames)
    {
        var bytes = new List<byte>(extra);
        var buf = new byte[65536];
        while (bytes.Count < frames * 4)
        {
            int n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(n > 0);
            bytes.AddRange(buf.AsSpan(0, n).ToArray());
        }
        var arr = bytes.ToArray();
        return Enumerable.Range(0, frames).Select(i => BitConverter.ToInt16(arr, i * 4)).ToList();
    }

    private static int IndexOf(List<byte> hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Count; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static async Task WaitForClientAsync(WavStreamServer server)
    {
        for (int i = 0; i < 100 && server.ClientCount == 0; i++) await Task.Delay(20);
        Assert.Equal(1, server.ClientCount);
    }

    [Fact]
    public async Task Without_margin_the_player_gets_only_live_audio()
    {
        using var server = Server(0);
        server.Write(Frames(0, 48000)); // 1 s before anyone connects: must not be sent
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientAsync(server);
        Assert.Empty(extra);

        server.Write(Frames(50000, FramesPer10Ms));
        var got = await ReadFramesAsync(s, extra, FramesPer10Ms);
        Assert.Equal(50000 & 0x7FFF, got[0]);
    }

    [Fact]
    public async Task Margin_sends_exactly_the_last_ms_then_live_without_gap_or_duplicate()
    {
        using var server = Server(500); // 500 ms = 24 000 frames
        for (int i = 0; i < 100; i++) server.Write(Frames(i * FramesPer10Ms, FramesPer10Ms)); // 1 s of history
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientAsync(server);
        for (int i = 100; i < 110; i++) server.Write(Frames(i * FramesPer10Ms, FramesPer10Ms)); // then 100 ms live

        var got = await ReadFramesAsync(s, extra, 24000 + 10 * FramesPer10Ms);
        Assert.Equal((48000 - 24000) & 0x7FFF, got[0]);          // starts exactly 500 ms before "now"
        for (int i = 1; i < got.Count; i++)
            Assert.Equal((got[i - 1] + 1) & 0x7FFF, got[i]);       // continuous: no gap, no duplicate
    }

    [Fact]
    public async Task Margin_before_much_audio_is_completed_with_leading_silence()
    {
        using var server = Server(2000); // 96 000 frames
        server.Write(Frames(1, 4800)); // only 100 ms captured so far (values 1…4800, never 0)
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        var got = await ReadFramesAsync(s, extra, 96000);
        Assert.All(got.Take(96000 - 4800), v => Assert.Equal(0, v)); // the Sonos still gets the full 2 s
        Assert.Equal(1, got[96000 - 4800]);
        Assert.Equal(4800, got[^1]);
    }

    [Fact]
    public async Task Backlog_grows_while_the_player_stops_reading_and_recovers()
    {
        using var server = Server(0);
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientAsync(server);
        server.TakeMaxBacklogMs();

        // Player stalls: 20 s of audio produced, nothing read. The kernel's socket buffers absorb part of it
        // (~1 s on loopback); the metric reports what is left waiting in Audiora beyond that.
        for (int i = 0; i < 20; i++) server.Write(Frames(i * 100 * FramesPer10Ms, 100 * FramesPer10Ms));
        await Task.Delay(200);
        double stalled = server.TakeMaxBacklogMs();
        Assert.True(stalled > 1000, $"backlog {stalled} ms");
        // 20 s stalled is far beyond the queue's time bound (margin + 1 s, here 1.5 s): the OLDEST audio is dropped so the
        // player resumes near live instead of replaying stale audio — never silently: every dropped chunk is counted.
        long dropped = Interlocked.Read(ref server.DroppedChunks);
        Assert.True(dropped > 0, "the oldest audio beyond the bound must be dropped and counted");

        // Player catches up: it receives everything that was kept.
        await ReadFramesAsync(s, extra, (int)((20 - dropped) * 100 * FramesPer10Ms));
        await Task.Delay(100);
        server.TakeMaxBacklogMs(); // drop the stalled peak
        server.Write(Frames(0, FramesPer10Ms));
        await Task.Delay(100);
        Assert.True(server.TakeMaxBacklogMs() < 50);
    }

    [Fact]
    public async Task A_player_that_stops_reading_is_detected_and_can_be_closed()
    {
        using var server = Server(0);
        var events = new List<string>();
        server.ConnectionEvent += e => { lock (events) events.Add(e); };
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientAsync(server);

        server.Write(Frames(0, FramesPer10Ms));
        await ReadFramesAsync(s, extra, FramesPer10Ms);
        Assert.True(server.SinceLastClientProgress < TimeSpan.FromMilliseconds(500), "a reading player shows progress");

        // Stops reading: once the socket buffers are full the server's write blocks and progress stops.
        for (int i = 0; i < 3; i++) server.Write(Frames(i * 100 * FramesPer10Ms, 100 * FramesPer10Ms));
        await Task.Delay(1500);
        Assert.True(server.SinceLastClientProgress > TimeSpan.FromSeconds(1), $"stalled for {server.SinceLastClientProgress}");

        server.AbortClients(); // must also release a write blocked on the socket
        for (int i = 0; i < 100 && server.ClientCount > 0; i++) await Task.Delay(20);
        Assert.Equal(0, server.ClientCount);
        for (int i = 0; i < 50; i++) { lock (events) if (events.Count == 2) break; await Task.Delay(20); }
        lock (events) Assert.Contains("vigilante", events[^1]);
    }

    [Fact]
    public async Task Connections_and_disconnections_are_reported()
    {
        using var server = Server(0);
        var events = new List<string>();
        server.ConnectionEvent += e => { lock (events) events.Add(e); };
        var (tcp, _, _) = await ConnectAsync(server);
        await WaitForClientAsync(server);
        tcp.Dispose();
        server.Write(Frames(0, FramesPer10Ms)); // writing to a closed socket ends the client
        for (int i = 0; i < 100 && server.ClientCount > 0; i++) { await Task.Delay(20); server.Write(Frames(0, FramesPer10Ms)); }
        await Task.Delay(100);
        lock (events)
        {
            Assert.Contains(events, e => e.StartsWith("conexión 127.0.0.1:") && e.Contains("GET /sonosflow.wav"));
            Assert.Contains(events, e => e.StartsWith("desconexión 127.0.0.1:"));
        }
    }

    [Fact]
    public async Task Saturation_counts_dropped_chunks_and_backlog_recovers_after_drain()
    {
        using var server = Server(0);
        var (tcp, stream, extra) = await ConnectAsync(server);
        using var connection = tcp;
        await WaitForClientAsync(server);
        Assert.Empty(extra);

        // Overflow the bounded queue while the player is not reading. The producer never waits.
        var chunk = new byte[19200];
        const int produced = 2000;
        for (int i = 0; i < produced; i++) server.Write(chunk);
        long dropped = Interlocked.Read(ref server.DroppedChunks);
        Assert.True(dropped > 0, "a full queue must report the audio it rejected");

        long expected = (produced - dropped) * chunk.Length;
        long received = 0;
        var buffer = new byte[65536];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received < expected)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - received)), timeout.Token);
            Assert.True(count > 0, "connection ended before all accepted audio arrived");
            received += count;
        }
        for (int i = 0; i < 100 && Interlocked.Read(ref server.BytesSent) < expected; i++) await Task.Delay(10);
        server.TakeMaxBacklogMs(); // discard the peak reached during the intentional stall
        Assert.Equal(expected, Interlocked.Read(ref server.BytesSent));
        Assert.Equal(0, server.TakeMaxBacklogMs());
    }
    [Fact]
    public void Peers_outside_the_allow_list_are_rejected()
    {
        using var server = new WavStreamServer(IPAddress.Loopback, 0, Format, [IPAddress.Parse("192.0.2.1")], 1000);
        using var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, server.Endpoint.Port);
        for (int i = 0; i < 50 && Interlocked.Read(ref server.RejectedConnections) == 0; i++) Thread.Sleep(20);
        Assert.Equal(1, Interlocked.Read(ref server.RejectedConnections));
        Assert.Equal(0, server.ClientCount);
    }
}
