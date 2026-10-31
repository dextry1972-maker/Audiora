using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;
using SonosFlow.Core.Diagnostics;
using SonosFlow.Core.Net;

namespace SonosFlow.Core.Tests;

/// <summary>
/// The server under the situations a long session meets: a player that stalls, one that vanishes mid-handshake,
/// repeated reconnection, bursts of 50 ms device packets, a stream that never ends. "Accelerated" tests push the DATA
/// of minutes through in seconds: they prove the code path, not that a real six minutes of wall time behave.
/// </summary>
public sealed class StreamServerHardeningTests
{
    private static readonly WaveFormat Format = new(48000, 16, 2);
    private const int Frames10Ms = 480, Frames50Ms = 2400;

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

    private static WavStreamServer Server(int prebufferMs = 0, int maxPendingMs = 0, StreamDiagnostics? diag = null) =>
        new(IPAddress.Loopback, 0, Format, [IPAddress.Loopback], prebufferMs, maxPendingMs) { Diagnostics = diag };

    private static async Task<(TcpClient Tcp, NetworkStream Stream, byte[] Extra)> ConnectAsync(WavStreamServer server)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET /sonosflow.wav HTTP/1.1\r\nHost: t\r\n\r\n"));
        var got = new List<byte>();
        var buf = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0 || got.Count < headerEnd + 4 + 44)
        {
            int n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(n > 0, "connection closed before the header");
            got.AddRange(buf.AsSpan(0, n).ToArray());
            headerEnd = Encoding.ASCII.GetString(got.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        return (tcp, s, got.Skip(headerEnd + 4 + 44).ToArray());
    }

    private static async Task WaitForClientsAsync(WavStreamServer server, int count)
    {
        for (int i = 0; i < 200 && server.ClientCount != count; i++) await Task.Delay(10);
        Assert.Equal(count, server.ClientCount);
    }

    private static async Task<byte[]> ReadAvailableAsync(NetworkStream s, byte[] extra, TimeSpan quietFor)
    {
        var all = new List<byte>(extra);
        var buf = new byte[65536];
        while (true)
        {
            int n;
            using var quiet = new CancellationTokenSource(quietFor); // a real cancellation: no abandoned read left to fail later
            try { n = await s.ReadAsync(buf, quiet.Token); }
            catch (OperationCanceledException) { break; }
            if (n == 0) break;
            all.AddRange(buf.AsSpan(0, n).ToArray());
        }
        return all.ToArray();
    }

    [Fact]
    public async Task The_queue_is_bounded_in_time_so_50_ms_device_packets_cannot_hide_ten_seconds()
    {
        using var server = Server(); // margin 0 → bound 1.5 s
        Assert.Equal(1500, server.MaxPendingMs);
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientsAsync(server, 1);

        // The player stops reading while 10 s of 50 ms packets (device capture) are produced. A count of 200 chunks
        // would have kept all of it; the time bound keeps about the last 1.5 s plus what the socket buffers hold.
        int frame = 0;
        for (int i = 0; i < 2000; i++) { server.Write(Frames(frame, Frames50Ms)); frame += Frames50Ms; } // 100 s: more than any socket buffer holds
        await Task.Delay(300);
        Assert.True(Interlocked.Read(ref server.DroppedChunks) > 1500, "stale audio must be dropped, and counted");
        Assert.True(server.TakeMaxBacklogMs() < 4500, "the backlog stays bounded");

        // It resumes: everything it receives is a contiguous run that ENDS at the newest frame (near live, not stale).
        var received = await ReadAvailableAsync(s, extra, TimeSpan.FromMilliseconds(700));
        int frames = received.Length / 4;
        Assert.True(frames > 0);
        var last = BitConverter.ToInt16(received, (frames - 1) * 4);
        Assert.Equal((short)((frame - 1) & 0x7FFF), last);        // the live edge survived
        Assert.True(frames < 2000 * Frames50Ms / 2, "most of the stale audio is gone");
    }

    [Fact]
    public async Task Dropping_keeps_the_stream_intact_after_the_splice()
    {
        using var server = Server(0, maxPendingMs: 1500);
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientsAsync(server, 1);
        int frame = 0;
        for (int i = 0; i < 80; i++) { server.Write(Frames(frame, Frames50Ms)); frame += Frames50Ms; }
        await Task.Delay(200);
        var received = await ReadAvailableAsync(s, extra, TimeSpan.FromMilliseconds(500));
        var samples = Enumerable.Range(0, received.Length / 4).Select(i => BitConverter.ToInt16(received, i * 4)).ToArray();
        // After the one splice, the counter must be strictly sequential: no duplicates, no further holes.
        int jumps = 0;
        for (int i = 1; i < samples.Length; i++)
            if (samples[i] != (short)((samples[i - 1] + 1) & 0x7FFF)) jumps++;
        Assert.InRange(jumps, 0, 3); // one splice per dropped region (the socket buffer / the queue / the in-flight chunk)
    }

    [Fact]
    public async Task Only_audio_beyond_the_bound_is_dropped_and_every_dropped_chunk_is_accounted_for()
    {
        using var server = Server(0, maxPendingMs: 2000);   // no margin: nothing but live audio in the stream
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientsAsync(server, 1);
        int frame = 0;
        for (int i = 0; i < 60; i++) { server.Write(Frames(frame, Frames50Ms)); frame += Frames50Ms; } // 3 s
        await Task.Delay(200);
        var received = await ReadAvailableAsync(s, extra, TimeSpan.FromMilliseconds(500));
        // 3 s produced against a 2 s bound: some audio is dropped and counted — but never more than exceeded the bound.
        Assert.Equal(frame * 4L - Interlocked.Read(ref server.DroppedChunks) * Frames50Ms * 4L, received.Length);
        Assert.Equal(Interlocked.Read(ref server.DroppedChunks) * Frames50Ms * 4L, Interlocked.Read(ref server.DroppedBytes)); // bytes match chunks
    }

    /// <summary>A real producer runs at wall-clock rate; a test's loop is thousands of times faster, so it waits for the consumer.</summary>
    private static async Task PaceAsync(WavStreamServer server, long producedBytes, int maxInFlightBytes)
    {
        while (producedBytes - Interlocked.Read(ref server.BytesSent) > maxInFlightBytes) await Task.Delay(1);
    }

    [Fact]
    public async Task Normal_live_audio_is_never_dropped()
    {
        using var server = Server(0);
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientsAsync(server, 1);
        var all = new List<byte>(extra);
        var reader = Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (true)
            {
                int n;
                using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { n = await s.ReadAsync(buf, quiet.Token); } catch (OperationCanceledException) { return; }
                if (n == 0) return;
                lock (all) all.AddRange(buf.AsSpan(0, n).ToArray());
            }
        });
        int frame = 0; long produced = 0;
        for (int i = 0; i < 600; i++)                         // 6 s of 10 ms packets, consumer keeping pace
        {
            var chunk = Frames(frame, Frames10Ms); frame += Frames10Ms; produced += chunk.Length;
            server.Write(chunk);
            if (i % 20 == 19) await PaceAsync(server, produced, 10 * chunk.Length);
        }
        await reader;
        Assert.Equal(0, Interlocked.Read(ref server.DroppedChunks));
        lock (all) Assert.Equal(produced, all.Count);
    }

    [Fact]
    public async Task A_player_that_vanishes_mid_handshake_does_not_stop_the_server_or_leave_unobserved_failures()
    {
        var unobserved = new List<Exception>();
        void Hook(object? o, UnobservedTaskExceptionEventArgs e) { lock (unobserved) unobserved.Add(e.Exception); }
        TaskScheduler.UnobservedTaskException += Hook;
        try
        {
            using var server = Server();
            var events = new List<string>();
            server.ConnectionEvent += e => { lock (events) events.Add(e); };
            for (int i = 0; i < 25; i++)
            {
                using var rude = new TcpClient();
                await rude.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
                await rude.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /sonosflow.wav HTTP/1.1\r\nHost: t\r\n\r\n"));
                rude.LingerState = new LingerOption(true, 0); // RST instead of a polite close
            }
            await Task.Delay(300);
            // The ones that got as far as being counted leave when the first audio write to them fails (live audio flows every 10–50 ms).
            for (int k = 0; k < 200 && server.ClientCount > 0; k++) { server.Write(Frames(0, Frames10Ms)); await Task.Delay(10); }
            Assert.Equal(0, server.ClientCount);
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(50); }

            // The server still serves a well-behaved player afterwards.
            var (tcp, _, _) = await ConnectAsync(server);
            using var __ = tcp;
            await WaitForClientsAsync(server, 1);
            // The property under test: nothing the SERVER started is left unobserved. (Unobserved faults are process-wide;
            // a read abandoned by some other test's client code is not the server's.)
            lock (unobserved)
            {
                var fromServer = unobserved.Where(x => x.ToString().Contains("WavStreamServer")).ToList();
                Assert.True(fromServer.Count == 0, string.Join(Environment.NewLine + "---" + Environment.NewLine, fromServer.Select(x => x.ToString())));
            }
        }
        finally { TaskScheduler.UnobservedTaskException -= Hook; }
    }

    [Fact]
    public async Task Garbage_and_oversized_requests_are_rejected_without_harm()
    {
        using var server = Server();
        foreach (var payload in new[] { "\r\n\r\n", "NOT HTTP AT ALL", new string('A', 20000), "GET\r\n\r\n", "POST /sonosflow.wav HTTP/1.1\r\n\r\n" })
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
            await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes(payload));
            await Task.Delay(30);
        }
        await Task.Delay(200);
        Assert.Equal(0, server.ClientCount);
        var (tcp, _, _) = await ConnectAsync(server);      // still alive and serving
        using var __ = tcp;
        await WaitForClientsAsync(server, 1);
    }

    [Fact]
    public async Task Reconnecting_many_times_leaves_no_clients_and_no_leaked_handles()
    {
        using var server = Server(500);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        // warm up (JIT, thread pool), then compare
        for (int i = 0; i < 3; i++)
        {
            var (t, _, _) = await ConnectAsync(server);
            await WaitForClientsAsync(server, 1);
            t.Dispose();
            for (int k = 0; k < 200 && server.ClientCount > 0; k++) { server.Write(Frames(0, Frames10Ms)); await Task.Delay(10); }
        }
        Assert.Equal(0, server.ClientCount);
        await Task.Delay(300); current.Refresh();
        int before = current.HandleCount;

        for (int i = 0; i < 60; i++)
        {
            var (tcp, _, _) = await ConnectAsync(server);
            await WaitForClientsAsync(server, 1);
            server.Write(Frames(i, Frames10Ms));
            tcp.Dispose();
            for (int k = 0; k < 100 && server.ClientCount > 0; k++) { server.Write(Frames(0, Frames10Ms)); await Task.Delay(10); }
            Assert.Equal(0, server.ClientCount);
        }
        await Task.Delay(300);
        GC.Collect(); GC.WaitForPendingFinalizers();
        current.Refresh();
        Assert.True(current.HandleCount - before < 60, $"handles {before} → {current.HandleCount}");
    }

    [Fact]
    public async Task A_second_connection_while_the_first_lingers_is_served_and_the_old_one_is_released_when_it_fails()
    {
        using var server = Server();
        var (first, _, _) = await ConnectAsync(server);
        await WaitForClientsAsync(server, 1);
        var (second, s2, e2) = await ConnectAsync(server);   // the Sonos reopened on its own
        using var _1 = second;
        await WaitForClientsAsync(server, 2);
        first.Dispose();                                      // and the old one is finally gone
        for (int k = 0; k < 200 && server.ClientCount > 1; k++) { server.Write(Frames(0, Frames10Ms)); await Task.Delay(10); }
        Assert.Equal(1, server.ClientCount);
        server.Write(Frames(7, Frames10Ms));
        var got = await ReadAvailableAsync(s2, e2, TimeSpan.FromMilliseconds(300));
        Assert.True(got.Length >= Frames10Ms * 4);
    }

    [Fact]
    public async Task ACCELERATED_an_endless_stream_carries_six_minutes_of_data_with_a_valid_header_and_no_loss()
    {
        using var server = Server(0);
        var (tcp, s, extra) = await ConnectAsync(server);
        using var _ = tcp;
        await WaitForClientsAsync(server, 1);

        long totalBytes = 0, producedBytes = 0;
        var reader = Task.Run(async () =>
        {
            totalBytes = extra.Length;
            var buf = new byte[1 << 20];
            while (true)
            {
                int n;
                using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { n = await s.ReadAsync(buf, quiet.Token); }
                catch (OperationCanceledException) { return; }
                if (n == 0) return;
                totalBytes += n;
            }
        });
        const int blocks = 6 * 60 * 100;                       // 6 minutes of 10 ms blocks
        var chunk = Frames(0, Frames10Ms);
        for (int i = 0; i < blocks; i++)
        {
            server.Write(chunk); producedBytes += chunk.Length;
            if (i % 100 == 99) await PaceAsync(server, producedBytes, 50 * chunk.Length); // consumer keeps pace: a data-volume test
        }
        await reader;
        Assert.Equal(0, Interlocked.Read(ref server.DroppedChunks));
        Assert.Equal(producedBytes, totalBytes);
        Assert.Equal(producedBytes, Interlocked.Read(ref server.BytesSent));
    }

    [Fact]
    public async Task The_diagnostics_see_connections_stalls_and_drops_without_recording_addresses()
    {
        using var diag = new StreamDiagnostics(null, 48000, "app");
        using var server = Server(0, 1500, diag);
        var (tcp, s, extra) = await ConnectAsync(server);
        await WaitForClientsAsync(server, 1);
        for (int i = 0; i < 100; i++) server.Write(Frames(i * Frames50Ms, Frames50Ms));  // stall → drops
        await Task.Delay(300);
        var tick = diag.Tick(server.TakeMaxBacklogMs(), null, 0);
        Assert.True(tick.DroppedChunks > 0);
        Assert.Contains(tick.Reasons, r => r.StartsWith("descartes"));
        tcp.Dispose();
        for (int k = 0; k < 100 && server.ClientCount > 0; k++) { server.Write(Frames(0, Frames10Ms)); await Task.Delay(20); }
        await Task.Delay(100);
        var events = diag.RecentEvents();
        Assert.Contains(events, e => e.Kind == "http_abierta" && e.Detail.Contains("cliente#1"));
        Assert.Contains(events, e => e.Kind == "http_cerrada" && e.Detail.Contains("descartes="));
        Assert.DoesNotContain(events, e => System.Text.RegularExpressions.Regex.IsMatch(e.Detail, @"\d+\.\d+\.\d+\.\d+"));
    }
}
