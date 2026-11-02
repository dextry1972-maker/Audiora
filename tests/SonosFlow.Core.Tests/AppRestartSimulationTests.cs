using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Sonos;
using SonosFlow.Core.Streaming;

namespace SonosFlow.Core.Tests;

/// <summary>
/// The 02/11 incident, in isolation: an application that is being captured closes and a new instance opens (new PID, new
/// audio session). A stand-in app (CaptureCheck's tone, on the silent "Steam Streaming" output: nothing audible, no
/// Sonos, no browser of the user) is killed and relaunched while a real <see cref="StreamingSession"/> follows it, and a
/// second client reads the HTTP stream exactly as the player would. The tone is analysed 1 ms at a time: a hole in the
/// CONTENT shows up even when every timing metric is clean. Skipped (and says so) where the output or the tool is missing.
/// </summary>
[Collection("FakeSonos on port 1400")]
public sealed class AppRestartSimulationTests(Xunit.Abstractions.ITestOutputHelper log) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"audiora-restart-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static string? ToneTool()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SonosFlow.sln"))) dir = dir.Parent;
        if (dir is null) return null;
        var bin = Path.Combine(dir.FullName, "tools", "SonosFlow.CaptureCheck", "bin");
        return Directory.Exists(bin) ? Directory.GetFiles(bin, "SonosFlow.CaptureCheck.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    private static Process StartTone(string tool, int seconds) =>
        Process.Start(new ProcessStartInfo(tool, $"tone 1000 {seconds} \"Steam Streaming\"") { UseShellExecute = false, CreateNoWindow = true })!;

    /// <summary>Reads the live WAV like the player would; marks, for every received byte, the time it arrived.</summary>
    private sealed class StreamReader : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<(long Offset, double Sec)> _marks = [];
        private readonly MemoryStream _pcm = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Task? _loop;

        public async Task StartAsync(int port)
        {
            await _tcp.ConnectAsync(IPAddress.Loopback, port);
            var s = _tcp.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes("GET /sonosflow.wav HTTP/1.1\r\nHost: t\r\n\r\n"));
            _loop = Task.Run(async () =>
            {
                var buf = new byte[65536];
                bool header = true;
                var head = new List<byte>();
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        int n = await s.ReadAsync(buf, _cts.Token);
                        if (n == 0) break;
                        int from = 0;
                        if (header)
                        {
                            head.AddRange(buf.AsSpan(0, n).ToArray());
                            int end = Encoding.ASCII.GetString(head.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (end < 0 || head.Count < end + 4 + 44) continue;
                            header = false;
                            var rest = head.Skip(end + 4 + 44).ToArray();
                            lock (_pcm) { _marks.Add((_pcm.Length, _clock.Elapsed.TotalSeconds)); _pcm.Write(rest); }
                            continue;
                        }
                        lock (_pcm) { _marks.Add((_pcm.Length, _clock.Elapsed.TotalSeconds)); _pcm.Write(buf, from, n - from); }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
            });
        }

        public double Elapsed => _clock.Elapsed.TotalSeconds;
        public byte[] Snapshot() { lock (_pcm) return _pcm.ToArray(); }
        public void Dispose() { _cts.Cancel(); _tcp.Dispose(); }
    }

    /// <summary>1 ms windows of a 1 kHz tone: "tone" if the window carries the tone's energy, "hole" if a window inside tone is (nearly) empty.</summary>
    private static (int ToneMs, int HoleMs, int Holes, int SilentMs) Analyse(byte[] pcm, int fromFrame, int toFrame, double nominalRms)
    {
        int win = 48, tone = 0, holeMs = 0, holes = 0, silent = 0;
        bool inHole = false;
        int lastToneWindow = -1000;
        for (int f = fromFrame; f + win <= toFrame; f += win)
        {
            double sum = 0;
            for (int i = 0; i < win; i++) { double v = BitConverter.ToInt16(pcm, (f + i) * 4) / 32768.0; sum += v * v; }
            double rms = Math.Sqrt(sum / win);
            int w = f / win;
            if (rms > nominalRms * 0.5) { tone++; inHole = false; lastToneWindow = w; }
            else if (w - lastToneWindow <= 300) { holeMs++; if (!inHole) holes++; inHole = true; } // a short gap right after tone
            else silent++;
        }
        return (tone, holeMs, holes, silent);
    }

    /// <summary>
    /// EXPERIMENT (off unless AUDIORA_EXPERIMENTS=1): the same stand-in app, captured per application, rendering on the silent
    /// output versus on the real default output at -60 dBFS (the state "plan B" leaves a browser in). Compares the content.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Experiment_content_of_a_per_app_capture_when_the_app_renders_on_a_real_output(bool realOutput)
    {
        if (Environment.GetEnvironmentVariable("AUDIORA_EXPERIMENTS") != "1") { log.WriteLine("OMITIDA: experimento (AUDIORA_EXPERIMENTS=1)."); return; }
        var steam = AudioDevices.ListOutputs().FirstOrDefault(d => d.Name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase));
        string? tool = ToneTool();
        if (steam is null || tool is null) { log.WriteLine("OMITIDA: falta la salida o la herramienta."); return; }

        using var sonos = new FakeSonos();
        double gain = realOutput ? 0.001 : 0.25;
        var tone = Process.Start(new ProcessStartInfo(tool, realOutput ? "tone-default 1000 60 0.001" : "tone 1000 60 \"Steam Streaming\"") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            AudioAppInfo? app = null;
            for (int i = 0; i < 150 && app is null; i++) { await Task.Delay(100); app = AudioApps.List().FirstOrDefault(a => a.ProcessId == tone.Id); }
            Assert.NotNull(app);
            var target = new SonosDevice(IPAddress.Loopback, "Fake", "Stand-in", "S0", "uuid:fake");
            await using var session = new StreamingSession(target, [IPAddress.Loopback], CaptureSpec.Application(app!),
                new StreamOptions(Port: 8797, MuteLocal: false, FullLevel: false, LogDirectory: _dir, PrebufferMs: 0), Path.Combine(_dir, "guard.json"));
            using var reader = new StreamReader();
            await session.StartAsync();
            await reader.StartAsync(8797);
            await Task.Delay(25000);
            await session.StopAsync();
            var pcm = reader.Snapshot();
            int frames = pcm.Length / 4;
            var r = Analyse(pcm, 48000 * 3, frames - 48000, gain / Math.Sqrt(2));
            log.WriteLine($"{(realOutput ? "SALIDA REAL" : "SALIDA SILENCIOSA")}: tono {r.ToneMs} ms, huecos {r.Holes} ({r.HoleMs} ms), silencio {r.SilentMs} ms");
        }
        finally { try { tone.Kill(true); } catch (Exception) { } }
    }

    [Fact]
    public async Task If_the_stand_in_app_restarts_the_session_follows_it_and_the_content_stays_whole()
    {
        var steam = AudioDevices.ListOutputs().FirstOrDefault(d => d.Name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase));
        string? tool = ToneTool();
        if (steam is null || tool is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            log.WriteLine($"OMITIDA: falta la salida «Steam Streaming» ({steam is null}) o la herramienta de tono ({tool is null}).");
            Assert.False(Environment.GetEnvironmentVariable("AUDIORA_REQUIRE_SESSION_TEST") == "1", "AUDIORA_REQUIRE_SESSION_TEST=1 exige ejecutar esta prueba.");
            return;
        }

        using var sonos = new FakeSonos();
        var first = StartTone(tool, 120);
        Process? second = null;
        try
        {
            AudioAppInfo? app = null;
            for (int i = 0; i < 100 && app is null; i++)
            {
                await Task.Delay(100);
                app = AudioApps.List().FirstOrDefault(a => a.ProcessId == first.Id);
            }
            Assert.NotNull(app);

            var target = new SonosDevice(IPAddress.Loopback, "Fake", "Stand-in", "S0", "uuid:fake");
            var changes = new List<(double Sec, int Pid)>();
            await using var session = new StreamingSession(target, [IPAddress.Loopback], CaptureSpec.Application(app!),
                new StreamOptions(Port: 8796, MuteLocal: false, FullLevel: false, LogDirectory: _dir, PrebufferMs: 0), Path.Combine(_dir, "guard.json"));
            using var reader = new StreamReader();
            await session.StartAsync();
            await reader.StartAsync(8796);
            session.SourceChanged += spec => { lock (changes) changes.Add((reader.Elapsed, spec.ProcessId)); };

            await Task.Delay(8000);                              // before: the original instance
            double killedAt = reader.Elapsed;
            first.Kill(true); first.WaitForExit();
            await Task.Delay(2000);                              // the app is closed for a moment
            second = StartTone(tool, 120);                       // reopened: new PID, new session
            double reopenedAt = reader.Elapsed;
            for (int i = 0; i < 200; i++) { lock (changes) if (changes.Count > 0) break; await Task.Delay(100); }
            double followedAt; lock (changes) followedAt = changes.Count > 0 ? changes[0].Sec : double.NaN;
            await Task.Delay(10000);                             // after: the new instance, the same session
            double endedAt = reader.Elapsed;

            await session.StopAsync();
            var pcm = reader.Snapshot();
            int rate = 48000;
            double nominal = 0.25 / Math.Sqrt(2);                // the tone child plays a 0.25-gain sine
            int totalFrames = pcm.Length / 4;
            double secPerFrame = 1.0 / rate;
            // map wall time to frames through the stream's own clock: the stream is real-time, so frame ≈ (elapsed - t0) * rate
            // Use the first received byte time as t0 (the reader started right after StartAsync).
            int FrameAt(double sec) => (int)Math.Clamp((sec - 0.0) * rate - (reader.Elapsed * rate - totalFrames), 0, totalFrames);

            var before = Analyse(pcm, FrameAt(2.0), FrameAt(killedAt - 0.5), nominal);
            var after = Analyse(pcm, FrameAt(double.IsNaN(followedAt) ? reopenedAt : followedAt) + rate, FrameAt(endedAt - 0.5), nominal);
            log.WriteLine($"cerrada a {killedAt:0.0}s, reabierta a {reopenedAt:0.0}s, seguida a {followedAt:0.0}s, fin {endedAt:0.0}s; fuentes: {changes.Count}");
            log.WriteLine($"ANTES : tono {before.ToneMs} ms, huecos {before.Holes} ({before.HoleMs} ms), silencio {before.SilentMs} ms");
            log.WriteLine($"DESPUÉS: tono {after.ToneMs} ms, huecos {after.Holes} ({after.HoleMs} ms), silencio {after.SilentMs} ms");

            Assert.False(double.IsNaN(followedAt), "the session never followed the reopened application");
            Assert.True(followedAt - reopenedAt < 12, $"followed too late ({followedAt - reopenedAt:0.0} s)");
            Assert.True(after.ToneMs > 5000, "after following, the tone must be captured");
            Assert.True(after.Holes <= before.Holes, $"holes before {before.Holes}, after {after.Holes}: following the new instance left the content worse");
            Assert.Single(changes);                              // one switch, no churn
        }
        finally
        {
            try { first.Kill(true); } catch (Exception) { }
            try { second?.Kill(true); } catch (Exception) { }
        }
    }
}
