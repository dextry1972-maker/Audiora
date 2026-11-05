using System.Net.Sockets;

namespace SonosFlow.Core.Net;

/// <summary>One reading of Windows' own TCP statistics for a connection (SIO_TCP_INFO, version 0).</summary>
internal readonly record struct TcpSnapshot(
    uint RttUs, uint MinRttUs, uint BytesInFlight, uint Cwnd, uint SndWnd, uint RcvWnd,
    ulong BytesOut, uint BytesRetrans, uint FastRetrans, uint DupAcksIn, uint TimeoutEpisodes);

/// <summary>What one second of the player's connection looked like: the part of the path the PC-side metrics cannot see.</summary>
public readonly record struct TcpSample(double RttMs, long ReceiveWindowBytes, long BytesInFlight, long RetransBytes, long TimeoutEpisodes);

/// <summary>
/// Reads the kernel's view of a connection: round-trip time, bytes still unacknowledged, the receiver's advertised window
/// and — the point — retransmitted bytes and retransmission timeouts. A write that returns in 1 ms says nothing about whether the
/// Wi-Fi hop delivered it; this does. Read-only; never throws (an unsupported system or a closed socket just gives null).
/// </summary>
internal static class TcpInfo
{
    private const int SioTcpInfo = unchecked((int)0xD8000027);
    private const int V0Size = 88;

    public static TcpSnapshot? Read(Socket socket)
    {
        try
        {
            var output = new byte[V0Size];
            int n = socket.IOControl(SioTcpInfo, BitConverter.GetBytes(0), output); // input: the structure version (0)
            return n >= V0Size ? Parse(output) : null;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or NotSupportedException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>TCP_INFO_v0 layout: State, Mss, ConnectionTimeMs(8), TimestampsEnabled, RttUs, MinRttUs, BytesInFlight, Cwnd, SndWnd, RcvWnd,
    /// RcvBuf, BytesOut(8), BytesIn(8), BytesReordered, BytesRetrans, FastRetrans, DupAcksIn, TimeoutEpisodes, SynRetrans.</summary>
    internal static TcpSnapshot Parse(ReadOnlySpan<byte> b)
    {
        return new TcpSnapshot(RttUs: U(b, 20), MinRttUs: U(b, 24), BytesInFlight: U(b, 28), Cwnd: U(b, 32), SndWnd: U(b, 36), RcvWnd: U(b, 40),
            BytesOut: BitConverter.ToUInt64(b.Slice(48, 8)), BytesRetrans: U(b, 68), FastRetrans: U(b, 72), DupAcksIn: U(b, 76), TimeoutEpisodes: U(b, 80));
    }

    private static uint U(ReadOnlySpan<byte> b, int offset) => BitConverter.ToUInt32(b.Slice(offset, 4));

    internal static uint Mss(ReadOnlySpan<byte> b) => BitConverter.ToUInt32(b.Slice(4, 4)); // layout check in the tests
}
