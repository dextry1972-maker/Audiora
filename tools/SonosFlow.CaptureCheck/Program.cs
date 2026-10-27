using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SonosFlow.Core.Audio;

// Automated check of per-application capture, without making any audible sound:
// two child processes play different tones on an output nobody listens to (default: "Steam Streaming"),
// then we capture one process and measure which tones reach the capture.
//   SonosFlow.CaptureCheck [--device "Steam Streaming"]
//   SonosFlow.CaptureCheck tone <hz> <seconds> <deviceSubstring>   (child mode)

if (args is ["tone", var hz, var secs, var dev])
{
    using var device = FindDevice(dev) ?? throw new InvalidOperationException($"No hay salida que contenga '{dev}'");
    using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
    output.Init(new SignalGenerator(48000, 2) { Frequency = double.Parse(hz), Gain = 0.25, Type = SignalGeneratorType.Sin });
    output.Play();
    Thread.Sleep(TimeSpan.FromSeconds(double.Parse(secs)));
    return 0;
}

if (args is ["list"])
{
    foreach (var app in AudioApps.List())
        Console.WriteLine($"{(app.IsActive ? "▶ activa  " : "  inactiva")}  PID {app.ProcessId,-6} {app.Name}  ({app.ExeName})");
    return 0;
}

if (args is ["edge-isolation"])
{
    // A separate Edge profile must run as its own process tree (no window: --headless).
    var edge = AudioApps.EdgePath();
    if (edge is null) { Console.WriteLine("Edge no está instalado"); return 2; }
    var mainEdgeRoots = Process.GetProcessesByName("msedge").Select(p => AudioApps.RootProcessId(p.Id)).ToHashSet();
    using var iso = Process.Start(new ProcessStartInfo(edge,
        $"--user-data-dir=\"{AudioApps.IsolatedProfileDir}\" --headless=new --no-first-run about:blank") { UseShellExecute = false })!;
    Thread.Sleep(3000);
    var isoRoot = AudioApps.RootProcessId(iso.Id);
    var children = Process.GetProcessesByName("msedge").Count(p => AudioApps.RootProcessId(p.Id) == isoRoot);
    bool separate = !mainEdgeRoots.Contains(isoRoot);
    bool detected = AudioApps.IsIsolatedInstance(isoRoot);
    Console.WriteLine($"{(separate ? "OK   " : "FALLO")} instancia aislada en su propio árbol (raíz {isoRoot}, {children} procesos; raíces de tu Edge: {string.Join(",", mainEdgeRoots)})");
    Console.WriteLine($"{(detected ? "OK   " : "FALLO")} Audiora la reconoce como «YouTube Music (instancia aislada)»");
    foreach (var p in Process.GetProcessesByName("msedge").Where(p => AudioApps.RootProcessId(p.Id) == isoRoot)) { try { p.Kill(); } catch (InvalidOperationException) { } }
    return separate && detected ? 0 : 1;
}

if (args is ["session", var sonosIp])
{
    // End-to-end with the real Sonos, through the same StreamingSession the app uses. The test tone is
    // played at −60 dBFS on an output nobody listens to: measurable in the stream, practically inaudible
    // on the Sonos. The PC's own audio settings are not touched (MuteLocal = false).
    var devices = await SonosFlow.Core.Sonos.SonosDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3));
    var sonos = devices.FirstOrDefault(d => d.Address.ToString() == sonosIp);
    if (sonos is null) { Console.WriteLine($"No se encontró el Sonos {sonosIp}"); return 2; }
    using var quiet = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "tone-quiet 200 60 \"Steam Streaming\"") { UseShellExecute = false, CreateNoWindow = true })!;
    Thread.Sleep(1500);
    var app = new AudioAppInfo(quiet.Id, "Tono de prueba", "SonosFlow.CaptureCheck.exe", Environment.ProcessPath, true, false);
    var logDir = Path.Combine(Path.GetTempPath(), "sf-session-test");
    Directory.CreateDirectory(logDir);
    var stats = new List<SonosFlow.Core.Streaming.StreamStats>();
    var session = new SonosFlow.Core.Streaming.StreamingSession(sonos, devices.Select(d => d.Address),
        SonosFlow.Core.Streaming.CaptureSpec.SystemOutput(null), new SonosFlow.Core.Streaming.StreamOptions(MuteLocal: false, LogDirectory: logDir),
        Path.Combine(logDir, "guard.json"));
    session.StatsUpdated += s => { lock (stats) stats.Add(s); };
    int fails = 0;
    void Check(string what, bool ok) { if (!ok) fails++; Console.WriteLine($"{(ok ? "OK   " : "FALLO")} {what}"); }
    SonosFlow.Core.Streaming.StreamStats Last() { lock (stats) return stats[^1]; }

    await session.StartAsync();
    await Task.Delay(6000);
    Check($"Todo el PC: Sonos {Last().TransportState}, {Last().Clients} cliente(s)", Last().TransportState == "PLAYING" && Last().Clients == 1);
    long bytesBefore = Last().BytesSent;

    Check("Cambio en caliente a la aplicación", await session.SwitchSourceAsync(SonosFlow.Core.Streaming.CaptureSpec.Application(app)));
    await Task.Delay(6000);
    var a = Last();
    Check($"Sigue la misma conexión (reconexiones {a.Reconnects}, clientes {a.Clients}, {a.SendRateKBps:0} kB/s)", a.Reconnects == 0 && a.Clients == 1 && a.BytesSent > bytesBefore);
    Check($"Llega el audio de la aplicación (pico {a.PeakDbfs:0.0} dBFS, esperado ≈ −60)", a.PeakDbfs is > -66 and < -54 && a.SourceName == "Tono de prueba");

    session.SetPaused(true);
    await Task.Delay(3000);
    var p = Last();
    Check($"Pausa: se envía silencio sin desconectar (pico {(double.IsInfinity(p.PeakDbfs) ? "-inf" : p.PeakDbfs.ToString("0.0"))}, {p.SendRateKBps:0} kB/s, {p.TransportState})",
        double.IsNegativeInfinity(p.PeakDbfs) && p.SendRateKBps > 150 && p.Clients == 1);
    session.SetPaused(false);
    await Task.Delay(3000);
    Check($"Reanudar: vuelve el audio (pico {Last().PeakDbfs:0.0} dBFS)", Last().PeakDbfs > -66);

    quiet.Kill();
    await Task.Delay(5000);
    var gone = Last();
    Check($"La aplicación se cierra: estado detectado (disponible={gone.SourceAvailable}), el Sonos sigue ({gone.TransportState})", !gone.SourceAvailable && gone.Clients == 1);

    Check("Volver a «Todo el PC» en caliente", await session.SwitchSourceAsync(SonosFlow.Core.Streaming.CaptureSpec.SystemOutput(null)));
    await Task.Delay(4000);
    var back = Last();
    Check($"Todo el PC de nuevo: {back.SourceName}, reconexiones {back.Reconnects}, {back.TransportState}", back.SourceName == "Todo el audio del PC" && back.Reconnects == 0 && back.TransportState == "PLAYING");

    await session.DisposeAsync();
    Check("Detener: sesión cerrada", session.State == SonosFlow.Core.Streaming.SessionState.Stopped);
    Console.WriteLine($"Registro: {Directory.GetFiles(logDir, "stream-*.csv").OrderBy(f => f).Last()}");
    return fails == 0 ? 0 : 1;
}

if (args is ["tone-quiet", var qhz, var qsecs, var qdev])
{
    using var device = FindDevice(qdev) ?? throw new InvalidOperationException($"No hay salida que contenga '{qdev}'");
    using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
    output.Init(new SignalGenerator(48000, 2) { Frequency = double.Parse(qhz), Gain = 0.001, Type = SignalGeneratorType.Sin });
    output.Play();
    Thread.Sleep(TimeSpan.FromSeconds(double.Parse(qsecs)));
    return 0;
}

if (args is ["soak", var minutesArg, ..])
{
    bool inject = args.Contains("--inject"); // positive control: a deliberate 300 ms cut at t = 20 s
    int margin = args.SkipWhile(a => a != "--margin").Skip(1).Select(int.Parse).FirstOrDefault();
    // PC-side end-to-end soak test: a continuous 1 kHz sine (unheard output) → our capture (per-app AND
    // whole-device, at the same time) → pacer → WavStreamServer on loopback → a local reader acting as the
    // Sonos. Any break of the sine is found to the sample (x[n] − 2cos(w)·x[n−1] + x[n−2] ≠ 0).
    double minutes = double.Parse(minutesArg, System.Globalization.CultureInfo.InvariantCulture);
    using var soakDev = FindDevice("Steam Streaming") ?? throw new InvalidOperationException("Sin salida de prueba");
    using var tone = StartTone(1000, minutes * 60 + 30, "Steam Streaming");
    Thread.Sleep(2000);
    var clock = Stopwatch.StartNew();
    using var loadCts = new CancellationTokenSource();
    var load = Task.Run(async () => { while (!loadCts.IsCancellationRequested) { try { AudioApps.List(); } catch (Exception) { } await Task.Delay(2500); } });

    var pipes = new[]
    {
        SoakPipe.Start("Una aplicación", LoopbackSource.ForProcess(tone.Id, "tono", 48000), 8791, clock, margin),
        SoakPipe.Start("Todo el PC (dispositivo)", new LoopbackSource(soakDev), 8792, clock, margin),
    };
    var end = TimeSpan.FromMinutes(minutes);
    if (inject)
    {
        Thread.Sleep(TimeSpan.FromSeconds(20) - clock.Elapsed);
        Console.WriteLine($"[control] corte deliberado de 300 ms a los {clock.Elapsed.TotalSeconds:0.000} s");
        SetSessionMute(soakDev, tone.Id, true);
        Thread.Sleep(300);
        SetSessionMute(soakDev, tone.Id, false);
    }
    while (clock.Elapsed < end)
    {
        Thread.Sleep(30000);
        Console.WriteLine($"[{clock.Elapsed:mm\\:ss}] " + string.Join(" · ", pipes.Select(p => $"{p.Name}: {p.Glitches.Count} cortes, máx hueco paquetes {p.MaxPacketGapMs:0} ms, máx lectura {p.MaxReadStallMs:0} ms")));
    }
    loadCts.Cancel();
    foreach (var p in pipes) p.Stop();
    tone.Kill();
    Console.WriteLine();
    foreach (var p in pipes) p.Report();
    return pipes.All(p => p.Glitches.Count == 0) ? 0 : 1;
}

if (args is ["endpoint"])
{
    // Does per-app capture depend on the OUTPUT DEVICE's mute / master volume (what "silenciar el PC" changes)?
    // Uses the unheard Steam Streaming output; its original mute/volume are restored at the end.
    using var epDev = FindDevice("Steam Streaming") ?? throw new InvalidOperationException("Sin salida de prueba");
    var vol = epDev.AudioEndpointVolume;
    bool origMute = vol.Mute;
    float origVol = vol.MasterVolumeLevelScalar;
    using var t = StartTone(1000, 40, "Steam Streaming");
    Thread.Sleep(1500);
    int fails = 0;
    void R(string what, bool ok, double a) { if (!ok) fails++; Console.WriteLine($"{(ok ? "OK   " : "FALLO")} {what}  [A {a:0.0} dBFS]"); }
    try
    {
        vol.Mute = false; vol.MasterVolumeLevelScalar = 1f;
        var (e0, _) = Measure(LoopbackSource.ForProcess(t.Id, "A", 48000));
        R("Referencia: salida sin silenciar al 100 %", e0 > -20, e0);

        vol.Mute = true;
        Thread.Sleep(300);
        var (e1, _) = Measure(LoopbackSource.ForProcess(t.Id, "A", 48000));
        R("Salida SILENCIADA en Windows: la captura de la app sigue recibiendo audio", e1 > -20, e1);
        var (d1, _) = Measure(new LoopbackSource(epDev));
        Console.WriteLine($"      (captura del dispositivo completo con la salida silenciada: A {d1:0.0} dBFS)");

        vol.Mute = false; vol.MasterVolumeLevelScalar = 0.10f;
        Thread.Sleep(300);
        var (e2, _) = Measure(LoopbackSource.ForProcess(t.Id, "A", 48000));
        R($"Volumen maestro de Windows al 10 %: la captura de la app no cambia (Δ {e2 - e0:+0.0;-0.0} dB)", Math.Abs(e2 - e0) < 1,  e2);
        var (d2, _) = Measure(new LoopbackSource(epDev));
        Console.WriteLine($"      (captura del dispositivo completo al 10 %: A {d2:0.0} dBFS, Δ {d2 - e0:+0.0;-0.0} dB)");
    }
    finally
    {
        vol.MasterVolumeLevelScalar = origVol;
        vol.Mute = origMute;
        t.Kill();
        Console.WriteLine($"Salida de prueba restaurada: silenciada={vol.Mute} volumen={vol.MasterVolumeLevelScalar * 100:0}%");
    }
    return fails == 0 ? 0 : 1;
}

string deviceName = args.SkipWhile(a => a != "--device").Skip(1).FirstOrDefault() ?? "Steam Streaming";
using var target = FindDevice(deviceName);
if (target is null)
{
    Console.WriteLine($"No se encontró una salida silenciosa que contenga «{deviceName}». Usa --device.");
    return 2;
}
Console.WriteLine($"Salida de prueba (no audible): {target.FriendlyName}");
const double FreqA = 1000, FreqB = 3150;
int failures = 0;

using var toneA = StartTone(FreqA, 30, deviceName);
using var toneB = StartTone(FreqB, 30, deviceName);
Thread.Sleep(1500);
Console.WriteLine($"Proceso A (tono {FreqA} Hz): PID {toneA.Id} · Proceso B (tono {FreqB} Hz): PID {toneB.Id}");

// 1. Capture only A.
var (a1, b1) = Measure(LoopbackSource.ForProcess(toneA.Id, "A", 48000));
Report("1. Captura de A: llega A", a1 > -20, a1, b1);
Report("   … y NO llega B (aislamiento)", b1 < -60, a1, b1);

// 2. Capture only B.
var (a2, b2) = Measure(LoopbackSource.ForProcess(toneB.Id, "B", 48000));
Report("2. Captura de B: llega B y no A", b2 > -20 && a2 < -60, a2, b2);

// 3. Whole-device loopback still sees both (the existing "todo el PC" mode).
var (a3, b3) = Measure(new LoopbackSource(target));
Report("3. Modo dispositivo completo: llegan A y B", a3 > -30 && b3 > -30, a3, b3);

// 4. Mute A's session locally: does the per-app capture still receive it?
bool? sessionMuteKeepsCapture = null;
if (SetSessionMute(target, toneA.Id, true))
{
    Thread.Sleep(300);
    var (a4, b4) = Measure(LoopbackSource.ForProcess(toneA.Id, "A", 48000));
    SetSessionMute(target, toneA.Id, false);
    sessionMuteKeepsCapture = a4 > -20;
    Console.WriteLine($"4. Con A silenciada en el mezclador de Windows la captura de A recibe {(sessionMuteKeepsCapture == true ? "AUDIO" : "SILENCIO")} (A {a4:0.0} dBFS)");
}
else Console.WriteLine("4. No se encontró la sesión de audio de A (prueba omitida)");

// 5. Target closes mid-capture: no crash, capture keeps delivering (silence).
{
    using var toneC = StartTone(FreqA, 30, deviceName);
    Thread.Sleep(1000);
    using var src = LoopbackSource.ForProcess(toneC.Id, "C", 48000);
    long bytes = 0;
    src.PcmAvailable += m => Interlocked.Add(ref bytes, m.Length);
    src.Start();
    Thread.Sleep(800);
    toneC.Kill();
    Thread.Sleep(1500);
    long after = Interlocked.Read(ref bytes);
    Thread.Sleep(1000);
    bool keepsRunning = Interlocked.Read(ref bytes) > after && src.CaptureError is null;
    src.Stop();
    Report("5. Si la app se cierra, la captura sigue sin error (rellena silencio)", keepsRunning, 0, 0);
}

toneA.Kill();
toneB.Kill();
Console.WriteLine(failures == 0 ? "RESULTADO: todas las comprobaciones superadas" : $"RESULTADO: {failures} comprobaciones fallidas");
if (sessionMuteKeepsCapture is { } k) Console.WriteLine($"SESSION_MUTE_KEEPS_CAPTURE={k}");
return failures == 0 ? 0 : 1;

void Report(string what, bool ok, double a, double b)
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "OK   " : "FALLO")} {what}  [A {a:0.0} dBFS · B {b:0.0} dBFS]");
}

static Process StartTone(double hz, double seconds, string device) =>
    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"tone {hz} {seconds} \"{device}\"") { UseShellExecute = false, CreateNoWindow = true })!;

static MMDevice? FindDevice(string part)
{
    using var en = new MMDeviceEnumerator();
    return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
        .FirstOrDefault(d => d.FriendlyName.Contains(part, StringComparison.OrdinalIgnoreCase));
}

static bool SetSessionMute(MMDevice device, int pid, bool mute)
{
    var mgr = device.AudioSessionManager;
    mgr.RefreshSessions();
    var sessions = mgr.Sessions;
    for (int i = 0; i < sessions.Count; i++)
    {
        if ((int)sessions[i].GetProcessID != pid) continue;
        sessions[i].SimpleAudioVolume.Mute = mute;
        return true;
    }
    return false;
}

// Captures 2 s and returns the level (dBFS) of each test tone, measured with Goertzel.
static (double A, double B) Measure(LoopbackSource source)
{
    var samples = new List<float>(48000 * 3);
    source.PcmAvailable += pcm =>
    {
        var span = pcm.Span;
        lock (samples)
            for (int i = 0; i + 3 < span.Length; i += 4)
                samples.Add((BitConverter.ToInt16(span.Slice(i, 2)) + BitConverter.ToInt16(span.Slice(i + 2, 2))) / 65536f);
    };
    source.Start();
    Thread.Sleep(2000);
    source.Stop();
    source.Dispose();
    float[] x;
    lock (samples) x = samples.Skip(4800).ToArray(); // skip the first 100 ms
    return (Level(x, FreqA, 48000), Level(x, FreqB, 48000));
}

static double Level(float[] x, double hz, int rate)
{
    if (x.Length == 0) return double.NegativeInfinity;
    double coeff = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0;
    foreach (var v in x) { double s0 = v + coeff * s1 - s2; s2 = s1; s1 = s0; }
    double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
    double amplitude = 2 * Math.Sqrt(Math.Max(power, 0)) / x.Length;
    return 20 * Math.Log10(amplitude + 1e-12);
}
