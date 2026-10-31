using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Sonos;
using SonosFlow.Core.Streaming;

namespace SonosFlow.Core.Tests;

/// <summary>
/// A Sonos stand-in: the UPnP calls the session makes (answered like the real player) plus a "player" that pulls the stream
/// over HTTP and can stop reading. Listens on 127.0.0.1:1400, the port <see cref="SonosClient"/> always uses.
/// </summary>
internal sealed class FakeSonos : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 1400);
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Diagnostics.Stopwatch _played = new();
    private string _state = "STOPPED", _uri = "";
    private int _fetching;
    public volatile bool PauseReading;
    public long BytesRead;
    public int StreamRequests;
    public List<string> Actions { get; } = [];

    public FakeSonos()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch (Exception) { return; }
            _ = ServeAsync(c);
        }
    }

    private async Task ServeAsync(TcpClient c)
    {
        using var _ = c;
        var s = c.GetStream();
        var buf = new byte[16384];
        var pending = new List<byte>();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int headerEnd;
                while ((headerEnd = IndexOf(pending, "\r\n\r\n"u8.ToArray())) < 0)
                {
                    int n = await s.ReadAsync(buf, _cts.Token);
                    if (n == 0) return;
                    pending.AddRange(buf.AsSpan(0, n).ToArray());
                }
                string head = Encoding.ASCII.GetString(pending.Take(headerEnd).ToArray());
                int length = int.Parse(Regex.Match(head, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase).Groups[1].Value);
                while (pending.Count < headerEnd + 4 + length)
                {
                    int n = await s.ReadAsync(buf, _cts.Token);
                    if (n == 0) return;
                    pending.AddRange(buf.AsSpan(0, n).ToArray());
                }
                string body = Encoding.UTF8.GetString(pending.Skip(headerEnd + 4).Take(length).ToArray());
                pending.RemoveRange(0, headerEnd + 4 + length);

                var action = Regex.Match(head, "SOAPACTION:\\s*\"([^#\"]+)#([^\"]+)\"", RegexOptions.IgnoreCase);
                string service = action.Groups[1].Value, name = action.Groups[2].Value;
                lock (Actions) Actions.Add(name);
                string fields = Handle(name, body);
                string xml = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                             $"<s:Body><u:{name}Response xmlns:u=\"{service}\">{fields}</u:{name}Response></s:Body></s:Envelope>";
                var payload = Encoding.UTF8.GetBytes(xml);
                await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/xml; charset=\"utf-8\"\r\nContent-Length: {payload.Length}\r\n\r\n"));
                await s.WriteAsync(payload);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private string Handle(string action, string body)
    {
        switch (action)
        {
            case "SetAVTransportURI":
                _uri = System.Net.WebUtility.HtmlDecode(Regex.Match(body, "<CurrentURI>(.*?)</CurrentURI>", RegexOptions.Singleline).Groups[1].Value);
                return "";
            case "Play":
                _state = "PLAYING";
                _played.Restart();
                if (Interlocked.Exchange(ref _fetching, 1) == 0) _ = FetchAsync();
                return "";
            case "Stop":
                _state = "STOPPED";
                return "";
            case "GetTransportInfo":
                return $"<CurrentTransportState>{_state}</CurrentTransportState><CurrentTransportStatus>OK</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed>";
            case "GetPositionInfo":
                var t = _played.Elapsed;
                return $"<Track>1</Track><TrackURI>{System.Net.WebUtility.HtmlEncode(_uri)}</TrackURI><RelTime>{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}</RelTime>";
            case "GetMediaInfo":
                return $"<NrTracks>1</NrTracks><CurrentURI>{System.Net.WebUtility.HtmlEncode(_uri)}</CurrentURI>";
            default:
                return "";
        }
    }

    /// <summary>What the Sonos does after Play: it connects to the stream URL and reads it.</summary>
    private async Task FetchAsync()
    {
        try
        {
            var uri = new Uri(_uri);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port, _cts.Token);
            var s = tcp.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes($"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}\r\nUser-Agent: FakeSonos\r\n\r\n"), _cts.Token);
            Interlocked.Increment(ref StreamRequests);
            var buf = new byte[65536];
            while (!_cts.IsCancellationRequested && _state == "PLAYING")
            {
                while (PauseReading && !_cts.IsCancellationRequested) await Task.Delay(20);
                int n = await s.ReadAsync(buf, _cts.Token);
                if (n == 0) break;
                Interlocked.Add(ref BytesRead, n);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        finally { Interlocked.Exchange(ref _fetching, 0); }
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

    public void Dispose() { _cts.Cancel(); _listener.Stop(); }
}

[Collection("FakeSonos on port 1400")]
public sealed class StreamingSessionDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"audiora-session-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>
    /// The whole engine against a stand-in Sonos, on real WASAPI capture of the silent "Steam Streaming" output: the session
    /// starts, streams, the player stops reading for 4 s and resumes, the session stops. The diagnostics file must exist, record
    /// the episode and the summary, and hold no address. Without that output the test does nothing (and says so in the name).
    /// </summary>
    [Fact]
    public async Task A_real_session_against_a_stand_in_Sonos_writes_a_sane_diagnostics_file_and_survives_a_stalled_player()
    {
        var steam = AudioDevices.ListOutputs().FirstOrDefault(d => d.Name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase));
        if (steam is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;

        using var sonos = new FakeSonos();
        using var device = AudioDevices.Resolve(steam.Id);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        output.Init(new SignalGenerator(48000, 2) { Frequency = 1000, Gain = 0.01, Type = SignalGeneratorType.Sin }.ToWaveProvider());
        output.Play(); // −40 dBFS on an output nobody hears: there is audio to capture

        var target = new SonosDevice(IPAddress.Loopback, "Fake", "Stand-in", "S0", "uuid:fake");
        var messages = new List<string>();
        var states = new List<SessionState>();
        await using var session = new StreamingSession(target, [IPAddress.Loopback], CaptureSpec.SystemOutput(steam.Id),
            new StreamOptions(Port: 8795, MuteLocal: false, FullLevel: false, LogDirectory: _dir, PrebufferMs: 500),
            Path.Combine(_dir, "guard.json"));
        session.Diagnostic += m => { lock (messages) messages.Add(m); };
        session.StateChanged += s => { lock (states) states.Add(s); };

        await session.StartAsync();
        for (int i = 0; i < 100 && (Volatile.Read(ref sonos.StreamRequests) == 0 || Interlocked.Read(ref sonos.BytesRead) == 0); i++) await Task.Delay(50);
        Assert.True(sonos.StreamRequests >= 1 && Interlocked.Read(ref sonos.BytesRead) > 0, "the stand-in Sonos pulled the stream");

        await Task.Delay(3000);
        sonos.PauseReading = true;               // the player stops taking audio: more than the queue's time bound (margin 0.5 s + 1 s)
        await Task.Delay(4000);
        sonos.PauseReading = false;
        await Task.Delay(6500);                  // recovers; enough ticks for a ten-second summary

        await session.StopAsync();

        // the diagnostics file
        var files = Directory.GetFiles(_dir, "diag-*.jsonl");
        Assert.Single(files);
        var lines = File.ReadAllLines(files[0]);
        string text = string.Join("\n", lines);
        foreach (var line in lines) System.Text.Json.JsonDocument.Parse(line).Dispose();       // every line is valid JSON
        Assert.Contains("\"k\":\"inicio\"", text);
        Assert.Contains("modo=system", text);
        Assert.Contains("\"k\":\"resumen10s\"", text);
        Assert.Contains("\"k\":\"resumen\"", text);
        Assert.Contains("http_abierta", text);
        Assert.Contains("http_cerrada", text);
        Assert.Contains("\"k\":\"episodio\"", text);                                            // the stall was seen as an episode
        Assert.Contains("descartes", text);                                                     // and the dropped audio was counted
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", text);                                       // no address
        Assert.DoesNotContain("Fake", text);                                                    // no room name
        Assert.True(new FileInfo(files[0]).Length < 64 * 1024, "small");

        // the session reported it and the CSV keeps the verdict
        lock (messages)
        {
            Assert.Contains(messages, m => m.StartsWith("diagnóstico:"));
            Assert.Contains(messages, m => m.StartsWith("diagnóstico detallado: diag-"));
        }
        var csv = Directory.GetFiles(_dir, "stream-*.csv").Single();
        Assert.Contains("diagnóstico:", File.ReadAllText(csv));

        // and the session itself was unharmed by the stall
        lock (states) Assert.DoesNotContain(SessionState.Failed, states);
        lock (sonos.Actions) { Assert.Contains("SetAVTransportURI", sonos.Actions); Assert.Contains("Play", sonos.Actions); }
    }
}
