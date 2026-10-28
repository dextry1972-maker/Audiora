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

if (args is ["tone-default", var dhz, var dsecs, var dgain])
{
    // Like an ordinary app: plays on whatever Windows says is the default output for this process,
    // opened after a short wait so a per-app output set by the parent applies.
    Thread.Sleep(3000);
    using var en = new MMDeviceEnumerator();
    using var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    Console.WriteLine($"tone-default → {device.FriendlyName}");
    using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
    output.Init(new SignalGenerator(48000, 2) { Frequency = double.Parse(dhz), Gain = double.Parse(dgain, System.Globalization.CultureInfo.InvariantCulture), Type = SignalGeneratorType.Sin });
    output.Play();
    Thread.Sleep(TimeSpan.FromSeconds(double.Parse(dsecs)));
    return 0;
}

if (args is ["route-browser", var browserExe, var pageFile])
{
    // Live re-route of a Chromium browser that is ALREADY playing (what Audiora has to do with Brave/Chrome):
    // its own instance (temporary profile, window off-screen) plays a quiet tone on the default output; we send
    // its audio process to the unheard output and measure where it plays. The per-app output of that browser
    // executable is restored at the end.
    var silent = AudioDevices.FindSilentOutput(null) ?? throw new InvalidOperationException("Sin salida silenciosa");
    using var silentDev = AudioDevices.Resolve(silent.Id);
    using var defaultDev = AudioDevices.Resolve(null);
    var profile = Path.Combine(Path.GetTempPath(), "sf-route-browser-" + Environment.ProcessId);
    using var browser = Process.Start(new ProcessStartInfo(browserExe,
        $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --autoplay-policy=no-user-gesture-required " +
        $"--window-position=-32000,-32000 --window-size=300,200 --app=\"{new Uri(pageFile).AbsoluteUri}\"") { UseShellExecute = false })!;
    int root = browser.Id, sessionPid = 0;
    try
    {
        for (int i = 0; i < 40 && sessionPid == 0; i++) { Thread.Sleep(250); sessionPid = SessionPidUnder(root); }
        if (sessionPid == 0) { Console.WriteLine("FALLO el navegador no abrió una sesión de audio"); return 1; }
        Console.WriteLine($"Navegador {Path.GetFileName(browserExe)}: raíz {root}, sesión de audio en el PID {sessionPid}");
        Thread.Sleep(1000);
        string? previous = AppOutputRouting.Get(sessionPid);
        void Row(string what)
        {
            var (app, _) = Measure(LoopbackSource.ForProcess(root, "A", 48000));
            var (onSilent, _) = Measure(new LoopbackSource(silentDev));
            var (onDefault, _) = Measure(new LoopbackSource(defaultDev));
            Console.WriteLine($"{what,-34} captura {app,7:0.0} · {silent.Name[..Math.Min(24, silent.Name.Length)]} {onSilent,7:0.0} · predeterminada {onDefault,7:0.0} dBFS");
        }
        Row("Antes (salida predeterminada)");
        try
        {
            AppOutputRouting.Set(sessionPid, silent.Id);
            Thread.Sleep(1500);
            Row("Redirigido a la salida silenciosa");
            AppOutputRouting.Set(sessionPid, previous);
            Thread.Sleep(1500);
            Row("Restaurado");
        }
        finally
        {
            AppOutputRouting.Set(sessionPid, previous);
            Console.WriteLine($"Salida por aplicación final: {AppOutputRouting.Get(sessionPid) ?? "(predeterminada del sistema)"}");
        }
        return 0;
    }
    finally
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(browserExe)))
            if (AudioApps.RootProcessId(p.Id) == root) { try { p.Kill(); } catch (Exception) { } }
        Thread.Sleep(500);
        try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

if (args is ["follow-check"])
{
    // Does a plain WASAPI app on a fixed device follow a per-app output change? (Audiora's redirect check must spot
    // it when it doesn't.) Quiet tone on the default output, redirected, then: where is it playing? Nothing is muted.
    var silent = AudioDevices.FindSilentOutput(null) ?? throw new InvalidOperationException("Sin salida silenciosa");
    using var fc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "tone-default 1000 20 0.001")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    Console.WriteLine(fc.StandardOutput.ReadLine());
    Thread.Sleep(1000);
    int pid = fc.Id; // the tone process owns its session (its "root" is this process: same executable)
    string? prev = AppOutputRouting.Get(pid);
    try
    {
        AppOutputRouting.Set(pid, silent.Id);
        Thread.Sleep(4000);
        var playing = AudioApps.PlayingOn(Environment.ProcessId);
        bool moved = playing.Count > 0 && playing.All(id => id == silent.Id);
        Console.WriteLine($"{(moved ? "SIGUE" : "NO SIGUE")} el cambio de salida en caliente (suena en {playing.Count} salida(s); la silenciosa: {playing.Contains(silent.Id)})");
        Console.WriteLine(moved ? "Audiora mantendría la redirección" : "Audiora deshará la redirección y aplicará el plan B (silenciar su salida)");
        return 0;
    }
    finally
    {
        AppOutputRouting.Set(pid, prev);
        try { fc.Kill(); } catch (InvalidOperationException) { }
    }
}

if (args is ["browser-tone", var btExe, var btSeconds])
{
    // For SonosFlow's self-test: a private browser instance (temporary profile, window off-screen) playing a 1 kHz
    // tone at −60 dBFS on the default output. Prints "ROOT <pid>"; closes everything when stdin closes or on timeout.
    var html = Path.Combine(Path.GetTempPath(), $"sf-browser-tone-{Environment.ProcessId}.html");
    File.WriteAllText(html, """
        <!doctype html><meta charset="utf-8"><title>Tono</title><script>
        const c = new AudioContext(), o = c.createOscillator(), g = c.createGain();
        o.frequency.value = 1000; g.gain.value = 0.001; o.connect(g).connect(c.destination); o.start();
        setInterval(() => { if (c.state !== 'running') c.resume(); }, 500);
        </script>
        """);
    var btProfile = Path.Combine(Path.GetTempPath(), $"sf-browser-tone-{Environment.ProcessId}");
    using var bt = Process.Start(new ProcessStartInfo(btExe,
        $"--user-data-dir=\"{btProfile}\" --no-first-run --no-default-browser-check --autoplay-policy=no-user-gesture-required " +
        $"--window-position=-32000,-32000 --window-size=300,200 --app=\"{new Uri(html).AbsoluteUri}\"") { UseShellExecute = false })!;
    Console.WriteLine($"ROOT {bt.Id}");
    Console.Out.Flush();
    Task.Run(Console.In.ReadToEnd).Wait(TimeSpan.FromSeconds(double.Parse(btSeconds)));
    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(btExe)))
        if (AudioApps.RootProcessId(p.Id) == bt.Id) { try { p.Kill(); } catch (Exception) { } }
    Thread.Sleep(800);
    try { Directory.Delete(btProfile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    try { File.Delete(html); } catch (IOException) { }
    return 0;
}

if (args is ["redirect-and-crash", var rPid, var rExe, var rTarget, var rState])
{
    // Child of redirect-crash: redirect like Audiora does, then die without restoring (simulated crash).
    _ = new AppOutputRedirect(int.Parse(rPid), rExe, rTarget, rState);
    Environment.Exit(3);
}

if (args is ["redirect-crash", var crashBrowser, var crashPage])
{
    // Audiora's own redirect (AppOutputRedirect) on a private browser instance that is playing: a child process
    // redirects it and dies without restoring; RecoverPending must then put the browser back where it was.
    var silent = AudioDevices.FindSilentOutput(null) ?? throw new InvalidOperationException("Sin salida silenciosa");
    using var silentDev = AudioDevices.Resolve(silent.Id);
    using var defaultDev = AudioDevices.Resolve(null);
    var state = Path.Combine(Path.GetTempPath(), $"sf-redirect-crash-{Environment.ProcessId}.json");
    var profile = Path.Combine(Path.GetTempPath(), "sf-route-browser-" + Environment.ProcessId);
    using var browser = Process.Start(new ProcessStartInfo(crashBrowser,
        $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --autoplay-policy=no-user-gesture-required " +
        $"--window-position=-32000,-32000 --window-size=300,200 --app=\"{new Uri(crashPage).AbsoluteUri}\"") { UseShellExecute = false })!;
    int root = browser.Id, sessionPid = 0, fails = 0;
    string? before = null;
    void R(string what, bool ok) { if (!ok) fails++; Console.WriteLine($"{(ok ? "OK   " : "FALLO")} {what}"); }
    (double App, double Silent, double Default) Levels() =>
        (Measure(LoopbackSource.ForProcess(root, "A", 48000)).A, Measure(new LoopbackSource(silentDev)).A, Measure(new LoopbackSource(defaultDev)).A);
    try
    {
        for (int i = 0; i < 40 && sessionPid == 0; i++) { Thread.Sleep(250); sessionPid = SessionPidUnder(root); }
        if (sessionPid == 0) { Console.WriteLine("FALLO el navegador no abrió una sesión de audio"); return 1; }
        Thread.Sleep(1000);
        before = AppOutputRouting.Get(sessionPid);
        var l0 = Levels();
        R($"Antes: suena en la predeterminada ({l0.Default:0.0} dBFS), captura {l0.App:0.0}", l0.Default > -66 && l0.App > -66);
        using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                   $"redirect-and-crash {sessionPid} \"{crashBrowser}\" \"{silent.Id}\" \"{state}\"") { UseShellExecute = false, CreateNoWindow = true })!)
            child.WaitForExit(15000);
        Thread.Sleep(1500);
        var l1 = Levels();
        R($"Tras el «cuelgue»: sigue apartada (predeterminada {l1.Default:0.0}, {silent.Name} {l1.Silent:0.0}), captura {l1.App:0.0}, registro pendiente={File.Exists(state)}",
            l1.Default < l0.Default - 6 && l1.Silent > -66 && l1.App > -66 && File.Exists(state)); // other audio may be playing on the default output
        bool recovered = AppOutputRedirect.RecoverPending(state);
        Thread.Sleep(1500);
        var l2 = Levels();
        string? after = AppOutputRouting.Get(sessionPid);
        R($"RecoverPending={recovered}: vuelve a la predeterminada ({l2.Default:0.0} dBFS), salida por aplicación {after ?? "(predeterminada)"} = antes {before ?? "(predeterminada)"}, sin registro={!File.Exists(state)}",
            recovered && l2.Default > -66 && after == before && !File.Exists(state));
        return fails == 0 ? 0 : 1;
    }
    finally
    {
        if (sessionPid != 0) { try { AppOutputRouting.Set(sessionPid, before); } catch (Exception) { } } // never leave it redirected
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(crashBrowser)))
            if (AudioApps.RootProcessId(p.Id) == root) { try { p.Kill(); } catch (Exception) { } }
        Thread.Sleep(500);
        try { File.Delete(state); } catch (IOException) { }
        try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

if (args is ["policy-get", .. var pids])
{
    // Read-only: the per-app output Windows has for each PID (or the error it answers).
    foreach (var p in pids)
    {
        try { Console.WriteLine($"PID {p}: {AppOutputRouting.Get(int.Parse(p)) ?? "(predeterminada del sistema)"}"); }
        catch (Exception ex) { Console.WriteLine($"PID {p}: 0x{ex.HResult:X8} {ex.GetType().Name}"); }
    }
    return 0;
}

if (args is ["route"])
{
    // Per-app output: send ONE process to an unheard output and check that (a) Audiora's per-app capture still
    // gets it, (b) it now plays on the unheard output and (c) not on the user's default output. Quiet tone
    // (−60 dBFS); the app's previous per-app output is restored at the end.
    if (!AppOutputRouting.IsSupported) { Console.WriteLine("Windows sin soporte (build < 21390)"); return 2; }
    var silent = AudioDevices.FindSilentOutput(null) ?? throw new InvalidOperationException("Sin salida silenciosa");
    using var silentDev = AudioDevices.Resolve(silent.Id);
    using var defaultDev = AudioDevices.Resolve(null);
    Console.WriteLine($"Salida silenciosa: {silent.Name} · predeterminada: {defaultDev.FriendlyName}");
    var (baseline, _) = Measure(new LoopbackSource(defaultDev));
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "tone-default 1000 25 0.001")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    Thread.Sleep(700); // the audio service rejects a process that is still starting (E_INVALIDARG)
    string? previous = AppOutputRouting.Get(child.Id);
    Console.WriteLine($"Salida por aplicación antes: {previous ?? "(predeterminada del sistema)"}");
    try
    {
        AppOutputRouting.Set(child.Id, silent.Id);
        Console.WriteLine($"Salida por aplicación ahora: {AppOutputRouting.Get(child.Id)}");
        Console.WriteLine(child.StandardOutput.ReadLine());
        Thread.Sleep(1000);
        var (app, _) = Measure(LoopbackSource.ForProcess(child.Id, "A", 48000));
        var (onSilent, _) = Measure(new LoopbackSource(silentDev));
        var (onDefault, _) = Measure(new LoopbackSource(defaultDev));
        Console.WriteLine($"Captura de la app {app:0.0} dBFS · en {silent.Name} {onSilent:0.0} dBFS · en la predeterminada {onDefault:0.0} dBFS (antes del tono {baseline:0.0})");
        bool ok = app > -66 && onSilent > -66 && onDefault < -80;
        Console.WriteLine(ok ? "OK    solo esa app deja de oírse en el PC y la captura sigue recibiéndola" : "FALLO el enrutado por aplicación no se comportó como se esperaba");
        return ok ? 0 : 1;
    }
    finally
    {
        AppOutputRouting.Set(child.Id, previous);
        Console.WriteLine($"Restaurada: {AppOutputRouting.Get(child.Id) ?? "(predeterminada del sistema)"}");
        try { child.Kill(); } catch (InvalidOperationException) { }
    }
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

if (args is ["local-silence"])
{
    // Can the selected app be silenced LOCALLY without silencing what Audiora captures from it?
    // For each candidate, measure the per-app capture (what the Sonos would get) and the device loopback
    // (what the PC's speakers would play). Uses the unheard Steam Streaming output; every change is undone.
    using var silenceDev = FindDevice("Steam Streaming") ?? throw new InvalidOperationException("Sin salida de prueba");
    using var t = StartTone(1000, 40, "Steam Streaming");
    Thread.Sleep(1500);
    var session = FindSession(silenceDev, t.Id) ?? throw new InvalidOperationException("Sin sesión del tono");
    var sv = session.SimpleAudioVolume;
    bool origMute = sv.Mute; float origVol = sv.Volume;
    void Row(string what)
    {
        var (app, _) = Measure(LoopbackSource.ForProcess(t.Id, "A", 48000));
        var (local, _) = Measure(new LoopbackSource(silenceDev));
        Console.WriteLine($"{what,-46} captura de la app {app,7:0.0} dBFS · salida local {local,7:0.0} dBFS");
    }
    try
    {
        Row("Referencia (sesión normal)");
        sv.Mute = true; Thread.Sleep(300);
        Row("Sesión SILENCIADA (ISimpleAudioVolume.Mute)");
        sv.Mute = false; sv.Volume = 0f; Thread.Sleep(300);
        Row("Volumen de la sesión al 0 %");
        sv.Volume = 0.1f; Thread.Sleep(300);
        Row("Volumen de la sesión al 10 %");
    }
    finally
    {
        sv.Volume = origVol; sv.Mute = origMute;
        t.Kill();
        Console.WriteLine($"Sesión restaurada: silenciada={sv.Mute} volumen={sv.Volume * 100:0}%");
    }
    return 0;
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

// PID of a process with an active audio session whose root is rootPid (0 if none yet).
static int SessionPidUnder(int rootPid)
{
    using var en = new MMDeviceEnumerator();
    foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        using (d)
        {
            var mgr = d.AudioSessionManager;
            mgr.RefreshSessions();
            for (int i = 0; i < mgr.Sessions.Count; i++)
            {
                var s = mgr.Sessions[i];
                int pid = (int)s.GetProcessID;
                if (pid != 0 && s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive
                    && AudioApps.RootProcessId(pid) == rootPid) return pid;
            }
        }
    }
    return 0;
}

static AudioSessionControl? FindSession(MMDevice device, int pid)
{
    var mgr = device.AudioSessionManager;
    mgr.RefreshSessions();
    var sessions = mgr.Sessions;
    for (int i = 0; i < sessions.Count; i++)
        if ((int)sessions[i].GetProcessID == pid) return sessions[i];
    return null;
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
