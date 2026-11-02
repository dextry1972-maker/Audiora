using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;
using SonosFlow.Core.Diagnostics;
using SonosFlow.Core.Net;

namespace SonosFlow.Core.Tests;

/// <summary>
/// 02/11/2026: margin 1 s ends the micro-cuts after restarting Brave while margin 0 does not, with every PC-side metric clean and the
/// ping at 0 % loss. The part of the path nobody measured is the kernel's own view of the player's connection.
/// </summary>
public sealed class TcpInfoTests
{
    [Fact]
    public void The_structure_layout_is_right_on_a_real_connection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = accept.Result;
        var payload = new byte[300_000];
        server.GetStream().Write(payload);
        using var reader = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buf = new byte[65536];
        long got = 0;
        while (got < payload.Length) got += client.GetStream().Read(buf);

        var snap = TcpInfo.Read(server.Client);
        Assert.NotNull(snap);
        Assert.True(snap!.Value.BytesOut >= (ulong)payload.Length, $"BytesOut {snap.Value.BytesOut}: the layout is off");
        Assert.InRange(snap.Value.Cwnd, 1u, 100_000_000u);
        Assert.InRange(snap.Value.RttUs, 0u, 5_000_000u);
        Assert.Equal(0u, snap.Value.TimeoutEpisodes);                      // loopback does not lose packets
        // a second reading never goes backwards
        var again = TcpInfo.Read(server.Client)!.Value;
        Assert.True(again.BytesOut >= snap.Value.BytesOut);
    }

    [Fact]
    public void A_closed_socket_gives_null_not_an_exception()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Dispose();
        Assert.Null(TcpInfo.Read(socket));
    }

    [Fact]
    public async Task The_server_reports_the_players_connection_once_a_second_without_touching_the_audio()
    {
        var format = new WaveFormat(48000, 16, 2);
        using var server = new WavStreamServer(IPAddress.Loopback, 0, format, [IPAddress.Loopback], 0, 0);
        Assert.Null(server.SampleTcp());                                   // no player yet

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET /sonosflow.wav HTTP/1.1\r\nHost: t\r\n\r\n"));
        for (int i = 0; i < 200 && server.ClientCount != 1; i++) await Task.Delay(10);
        for (int i = 0; i < 20; i++) server.Write(new byte[9600]);
        var buf = new byte[65536];
        long got = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (got < 9600 * 20) got += await s.ReadAsync(buf, stop.Token);

        var first = server.SampleTcp();
        Assert.NotNull(first);
        Assert.Equal(0, first!.Value.RetransBytes);                        // the first reading has nothing to subtract from
        Assert.True(first.Value.ReceiveWindowBytes > 0);
        var second = server.SampleTcp();
        Assert.NotNull(second);
        Assert.InRange(second!.Value.RetransBytes, 0, 1 << 20);
    }

    [Fact]
    public void The_diagnostics_turn_retransmissions_into_an_anomaly_and_a_summary_line()
    {
        using var diag = new StreamDiagnostics(null, 48000, "test");
        diag.Tcp(rttMs: 3.0, receiveWindowBytes: 65535, retransBytes: 0, timeoutEpisodes: 0);
        var calm = diag.Tick(0, null, 192);
        Assert.DoesNotContain(calm.Reasons, r => r.Contains("tcp"));
        Assert.Equal(3.0, calm.TcpRttMaxMs);
        Assert.Equal(65535, calm.TcpReceiveWindowMin);

        diag.Tcp(rttMs: 40.0, receiveWindowBytes: 1000, retransBytes: 2920, timeoutEpisodes: 1);
        diag.Tcp(rttMs: 12.0, receiveWindowBytes: 900, retransBytes: 1460, timeoutEpisodes: 0);
        var bad = diag.Tick(0, null, 192);
        Assert.Contains(bad.Reasons, r => r == "retransmision_tcp 4380B");
        Assert.Contains(bad.Reasons, r => r == "rto_tcp 1");
        Assert.Equal(40.0, bad.TcpRttMaxMs);
        Assert.Equal(900, bad.TcpReceiveWindowMin);

        var after = diag.Tick(0, null, 192);                               // consumed: nothing carries over
        Assert.Equal(0, after.TcpRetransBytes);
        Assert.Equal(-1, after.TcpReceiveWindowMin);
    }
}
