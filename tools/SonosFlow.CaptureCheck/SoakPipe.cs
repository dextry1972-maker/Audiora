using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Net;

/// <summary>
/// One capture → pacer → <see cref="WavStreamServer"/> → local HTTP reader chain, with a sample-accurate
/// sine-continuity check on what the "Sonos" receives and timing of every stage.
/// </summary>
internal sealed class SoakPipe
{
    public string Name { get; }
    public List<(double AtSec, double LengthMs, string Kind)> Glitches { get; } = [];
    public double MaxPacketGapMs { get; private set; }
    public double MaxReadStallMs { get; private set; }
    public List<(double AtSec, double Ms)> PacketGaps { get; } = [];
    public List<(double AtSec, double Ms)> ReadStalls { get; } = [];
    public long SamplesReceived { get; private set; }

    private readonly LoopbackSource _source;
    private readonly WavStreamServer _server;
    private readonly Stopwatch _clock;
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;
    private double _lastPacket = -1;

    private SoakPipe(string name, LoopbackSource source, WavStreamServer server, Stopwatch clock)
    {
        Name = name;
        _source = source;
        _server = server;
        _clock = clock;
    }

    public static SoakPipe Start(string name, LoopbackSource source, int port, Stopwatch clock, int prebufferMs = 0)
    {
        var server = new WavStreamServer(IPAddress.Loopback, port, source.OutputFormat, [IPAddress.Loopback], prebufferMs);
        var spectrum = new SpectrumAnalyzer(source.OutputFormat.SampleRate); // same work as in the app
        var pipe = new SoakPipe(name, source, server, clock);
        source.PcmAvailable += pcm =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (pipe._lastPacket >= 0)
            {
                double gap = now - pipe._lastPacket;
                if (gap > pipe.MaxPacketGapMs) pipe.MaxPacketGapMs = gap;
                if (gap > 60) lock (pipe.PacketGaps) pipe.PacketGaps.Add((now / 1000, gap));
            }
            pipe._lastPacket = now;
            server.Write(pcm);
            spectrum.Process(pcm.Span);
        };
        source.Start();
        if (prebufferMs > 0) Thread.Sleep(prebufferMs + 200); // let the margin fill before the "Sonos" connects
        pipe._reader = Task.Run(() => pipe.ReadAsync(port));
        return pipe;
    }

    private async Task ReadAsync(int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET /sonosflow.wav HTTP/1.1\r\nHost: soak\r\n\r\n"));

        // HTTP headers, then the 44-byte WAV header.
        var buf = new byte[65536];
        var pre = new List<byte>();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await s.ReadAsync(buf, _cts.Token);
            if (n == 0) return;
            pre.AddRange(buf.AsSpan(0, n).ToArray());
            headerEnd = IndexOf(pre, "\r\n\r\n"u8.ToArray());
        }
        while (pre.Count < headerEnd + 4 + 44)
        {
            int n = await s.ReadAsync(buf, _cts.Token);
            if (n == 0) return;
            pre.AddRange(buf.AsSpan(0, n).ToArray());
        }
        int rate = BitConverter.ToInt32(pre.ToArray(), headerEnd + 4 + 24);
        double c = 2 * Math.Cos(2 * Math.PI * 1000 / rate);
        var pending = pre.Skip(headerEnd + 4 + 44).ToList();

        double x1 = 0, x2 = 0;
        long n0 = 0, glitchStart = -1, lastBad = -1;
        double lastRead = _clock.Elapsed.TotalMilliseconds;
        bool settled = false;

        void Feed(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                double x = BitConverter.ToInt16(data.Slice(i, 2)) / 32768.0;
                if (n0 >= 2)
                {
                    double r = x - c * x1 + x2;
                    bool bad = Math.Abs(r) > 0.02;
                    if (!settled && n0 > rate) settled = true; // ignore the first second (tone/stream start)
                    if (settled && bad)
                    {
                        if (glitchStart < 0) glitchStart = n0;
                        lastBad = n0;
                    }
                    else if (glitchStart >= 0 && n0 - lastBad > rate / 20) // close a glitch after 50 ms of clean sine
                    {
                        Glitches.Add(((double)glitchStart / rate, (lastBad - glitchStart + 1) * 1000.0 / rate, x == 0 && x1 == 0 ? "silencio" : "discontinuidad"));
                        glitchStart = -1;
                    }
                }
                x2 = x1;
                x1 = x;
                n0++;
            }
            SamplesReceived = n0;
        }

        Feed(pending.ToArray());
        int carry = pending.Count % 4;
        var leftover = pending.Skip(pending.Count - carry).ToArray();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await s.ReadAsync(buf.AsMemory(leftover.Length), _cts.Token);
                if (n == 0) break;
                double now = _clock.Elapsed.TotalMilliseconds;
                double stall = now - lastRead;
                lastRead = now;
                if (stall > MaxReadStallMs) MaxReadStallMs = stall;
                if (stall > 150) ReadStalls.Add((now / 1000, stall));
                leftover.CopyTo(buf, 0);
                int total = leftover.Length + n;
                int whole = total - total % 4;
                Feed(buf.AsSpan(0, whole));
                leftover = buf.AsSpan(whole, total - whole).ToArray();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public void Stop()
    {
        _cts.Cancel();
        _source.Stop();
        _source.Dispose();
        _server.Dispose();
        try { _reader?.Wait(2000); } catch (AggregateException) { }
    }

    public void Report()
    {
        double secs = _clock.Elapsed.TotalSeconds;
        Console.WriteLine($"== {Name}");
        Console.WriteLine($"   cortes en la onda recibida: {Glitches.Count}" + (Glitches.Count > 0 ? "  →  " + string.Join("  ", Glitches.Take(40).Select(g => $"{g.AtSec:0.000}s ({g.LengthMs:0.0} ms, {g.Kind})")) : ""));
        Console.WriteLine($"   muestras recibidas: {SamplesReceived:N0} (≈{SamplesReceived / 48000.0:0.0} s de audio en {secs:0.0} s)");
        Console.WriteLine($"   relleno de silencio del marcapasos: {_source.SilenceBytes / 1000} kB");
        Console.WriteLine($"   hueco máx. entre paquetes de captura: {MaxPacketGapMs:0.0} ms; >60 ms: {PacketGaps.Count}" + (PacketGaps.Count > 0 ? "  " + string.Join(" ", PacketGaps.Take(20).Select(g => $"{g.AtSec:0.0}s:{g.Ms:0}ms")) : ""));
        Console.WriteLine($"   bloqueo máx. de lectura (lado «Sonos»): {MaxReadStallMs:0.0} ms; >150 ms: {ReadStalls.Count}" + (ReadStalls.Count > 0 ? "  " + string.Join(" ", ReadStalls.Take(20).Select(g => $"{g.AtSec:0.0}s:{g.Ms:0}ms")) : ""));
        Console.WriteLine($"   descartes en el servidor: {_server.DroppedChunks}");
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
}
