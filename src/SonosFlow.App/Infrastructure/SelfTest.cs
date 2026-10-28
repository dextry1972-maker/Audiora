using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SonosFlow.App.ViewModels;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.Infrastructure;

/// <summary>
/// End-to-end check with the real Sonos through the same view model the windows use
/// (<c>--selftest-session &lt;pid&gt; --selftest-out &lt;file&gt;</c>). The target process should play a very
/// quiet tone on an output nobody listens to. Settings are not saved; the only output muted (part B) is
/// the one the test tone plays on, and it is restored.
/// </summary>
public static class SelfTest
{
    private static Localization.Loc L => Localization.Loc.Instance;

    public static async Task<int> RunAsync(MainViewModel vm, int tonePid, string outFile)
    {
        var lines = new List<string>();
        int fails = 0;
        void Check(string what, bool ok)
        {
            if (!ok) fails++;
            var line = $"{(ok ? "OK   " : "FALLO")} {what}";
            lines.Add(line);
            AppLog.Write("selftest " + line);
        }
        string Peak(double db) => double.IsNegativeInfinity(db) ? "-inf" : db.ToString("0.0");
        string? toolPath = null; // CaptureCheck, which plays the test tone (part B2 starts another one)
        try { using var tone = System.Diagnostics.Process.GetProcessById(tonePid); toolPath = tone.MainModule?.FileName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }

        vm.MuteLocal = false; // never touch the PC's own output during the test
        vm.IsAppMode = false;
        await vm.StartAsync();
        await Task.Delay(7000);
        var s0 = vm.LastStats;
        Check($"1. Transmitir «Todo el PC»: Sonos {s0?.TransportState}, clientes {s0?.Clients}", s0 is { TransportState: "PLAYING", Clients: 1 });
        long bytes0 = s0?.BytesSent ?? 0;

        // "Margen anti-cortes": the Sonos should sit about that far behind live (estimate has ±1 s resolution),
        // and nothing should pile up waiting for it.
        var bufs = new List<double>();
        double backlog = 0;
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(1000);
            if (vm.LastStats is { } st) { if (st.BufferEstimateSeconds is { } b) bufs.Add(b); backlog = Math.Max(backlog, st.BacklogMaxMs); }
        }
        double avgBuf = bufs.Count > 0 ? bufs.Average() : 0;
        Check($"1a. Margen anti-cortes {vm.PrebufferMs} ms: búfer medio del Sonos ≈ {avgBuf:0.0} s (sin margen ≈ 0,5 s); cola máx. {backlog:0} ms",
            (vm.PrebufferMs == 0 || avgBuf > 0.4 + vm.PrebufferMs / 1000.0 * 0.6) && backlog < 500);

        // Mini player: shows, stays on top, never takes focus; hiding/showing windows doesn't touch the stream.
        var mini = Application.Current.Windows.OfType<MiniPlayerWindow>().FirstOrDefault();
        var main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        vm.MiniVisible = true;
        await Task.Delay(800);
        bool noActivate = mini is not null && (GetWindowLong(new WindowInteropHelper(mini).Handle, -20) & 0x08000000) != 0;
        Check($"1b. Mini reproductor visible={mini?.IsVisible}, encima={mini?.Topmost}, sin robar foco={noActivate}, opacidad={mini?.Opacity:0.00}",
            mini is { IsVisible: true } && mini.Topmost == vm.MiniTopmost && noActivate);
        main?.Show();
        await Task.Delay(500);
        main?.Hide();
        await Task.Delay(500);
        main?.Show();
        vm.MiniVisible = false;
        await Task.Delay(2500);
        var sw = vm.LastStats;
        Check($"1c. Mostrar/ocultar ventanas no reinicia la transmisión (oculto={mini?.IsVisible == false}, reconexiones {sw?.Reconnects}, {sw?.TransportState})",
            mini?.IsVisible == false && sw is { Reconnects: 0, Clients: 1, TransportState: "PLAYING" } && sw.BytesSent > bytes0);
        Check($"1d. Una sola captura activa (capturas={LoopbackSource.ActiveCount})", LoopbackSource.ActiveCount == 1);

        // Changing the UI language mid-stream only swaps text: same session, no reconnect, audio keeps flowing.
        // Done on the Settings page, whose ComboBoxes are bound two-way to settings (a cleared selection would write null back).
        string lang0 = L.Code, page0 = vm.Page;
        var choices0 = (vm.CloseAction, vm.PrebufferKey, vm.SleepKey, vm.SelectedSpeaker, vm.Volume, vm.IsAppMode);
        vm.Page = "settings";
        await Task.Delay(800);
        long sentBeforeLang = vm.LastStats?.BytesSent ?? 0;
        foreach (var code in new[] { "ar", "ja", "de", lang0 }) { vm.SelectedLanguageCode = code; await Task.Delay(700); }
        await Task.Delay(1500);
        var sl = vm.LastStats;
        var choices1 = (vm.CloseAction, vm.PrebufferKey, vm.SleepKey, vm.SelectedSpeaker, vm.Volume, vm.IsAppMode);
        vm.Page = page0;
        Check($"1e. Cambiar de idioma en directo (ar → ja → de → {lang0}) no toca la transmisión ni los ajustes (reconexiones {sl?.Reconnects}, {sl?.TransportState}, idioma {L.Code}, cierre/margen/temporizador/altavoz/volumen/modo iguales: {choices0 == choices1})",
            sl is { Reconnects: 0, Clients: 1, TransportState: "PLAYING" } && sl.BytesSent > sentBeforeLang && L.Code == lang0
            && LoopbackSource.ActiveCount == 1 && choices0 == choices1);
        vm.MiniVisible = true;

        await vm.RefreshAppsNowAsync();
        var root = AudioApps.RootProcessId(tonePid);
        var app = vm.Apps.FirstOrDefault(a => a.Info.ProcessId == root);
        Check($"2. La aplicación de prueba aparece en la lista ({vm.Apps.Count} apps con audio)", app is not null);
        if (app is not null)
        {
            await vm.SelectAppForTestAsync(app);
            await Task.Delay(6000);
            var s1 = vm.LastStats;
            Check($"3. Cambio a la aplicación sin reiniciar (reconexiones {s1?.Reconnects}, clientes {s1?.Clients}, {s1?.SendRateKBps:0} kB/s)",
                s1 is { Reconnects: 0, Clients: 1 } && s1.BytesSent > bytes0 && s1.SendRateKBps > 150);
            Check($"4. Llega solo el tono de la aplicación (pico {Peak(s1?.PeakDbfs ?? double.NegativeInfinity)} dBFS, esperado ≈ −60; fuente «{s1?.SourceName}»)",
                s1 is { PeakDbfs: > -66 and < -54 } && s1.SourceName == app.Name);
            Check($"5. Mini reproductor y ventana comparten estado: «{vm.SourceName}» · {vm.MiniStatusText}",
                vm.SourceName == app.Name && vm.MiniStatusText == L.T("status.mini.streaming"));
            Check($"5b. Tras cambiar de fuente sigue habiendo una sola captura (capturas={LoopbackSource.ActiveCount})", LoopbackSource.ActiveCount == 1);

            vm.PauseResumeCommand.Execute(null);
            await Task.Delay(3000);
            var s2 = vm.LastStats;
            Check($"6. Pausa: silencio sin desconectar (pico {Peak(s2?.PeakDbfs ?? 0)}, {s2?.SendRateKBps:0} kB/s, {s2?.TransportState}, estado «{vm.MiniStatusText}»)",
                s2 is { Clients: 1, SendRateKBps: > 150 } && double.IsNegativeInfinity(s2.PeakDbfs) && vm.MiniStatusText == L.T("status.mini.paused"));
            vm.PauseResumeCommand.Execute(null);
            await Task.Delay(3000);
            Check($"7. Reanudar: vuelve el audio (pico {Peak(vm.LastStats?.PeakDbfs ?? double.NegativeInfinity)} dBFS)", vm.LastStats?.PeakDbfs > -66);
        }

        vm.IsAppMode = false;
        await Task.Delay(5000);
        var s4 = vm.LastStats;
        Check($"9. Volver a «Todo el PC» en caliente: {s4?.SourceName}, reconexiones {s4?.Reconnects}, {s4?.TransportState}",
            s4 is { SourceName: "Todo el audio del PC", Reconnects: 0, TransportState: "PLAYING" });

        Check($"9b. Una sola captura activa tras volver (capturas={LoopbackSource.ActiveCount})", LoopbackSource.ActiveCount == 1);
        await vm.StopAsync(reason: "fin del autotest");
        await Task.Delay(500);
        bool portFree = !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 8780);
        Check($"10. Detener: sesión cerrada, capturas={LoopbackSource.ActiveCount}, puerto 8780 liberado={portFree}", !vm.IsStreaming && LoopbackSource.ActiveCount == 0 && portFree);
        vm.MiniVisible = false;

        // ── Part B: "Una aplicación" + "Silenciar el PC" (+ "Nivel completo", which must be ignored here).
        // The tone plays on an unheard output, so that is the "PC output" Audiora mutes; the user's real
        // default output and the Windows master volume must not change at all.
        if (app is not null)
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var defaultDev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            await vm.RefreshAppsNowAsync();
            app = vm.Apps.FirstOrDefault(a => a.Info.ProcessId == root) ?? app;
            using var appDev = app.Info.OutputDeviceId is { } id ? enumerator.GetDevice(id) : null;
            bool defMute0 = defaultDev.AudioEndpointVolume.Mute;
            float defVol0 = defaultDev.AudioEndpointVolume.MasterVolumeLevelScalar;
            bool appMute0 = appDev?.AudioEndpointVolume.Mute ?? false;
            float appVol0 = appDev?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0;
            Check($"11. Salida de la aplicación detectada: {appDev?.FriendlyName ?? "ninguna"} (predeterminada: {defaultDev.FriendlyName})",
                appDev is not null && appDev.ID != defaultDev.ID);

            vm.MuteLocal = true;
            vm.FullLevel = true; // must have no effect in application mode
            await vm.SelectAppForTestAsync(app);
            await vm.StartAsync();
            await Task.Delay(6000);
            var b1 = vm.LastStats;
            Check($"12. «Una aplicación» con «Silenciar el PC»: Sonos {b1?.TransportState}, fuente «{b1?.SourceName}»", b1 is { TransportState: "PLAYING", Clients: 1 } && b1.SourceName == app.Name);
            Check($"13. La salida de la app queda silenciada en Windows (silenciada={appDev?.AudioEndpointVolume.Mute})", appDev?.AudioEndpointVolume.Mute == true);
            Check($"14. …y el Sonos sigue recibiendo su audio (pico {Peak(b1?.PeakDbfs ?? double.NegativeInfinity)} dBFS, esperado ≈ −60)", b1 is { PeakDbfs: > -66 and < -54 });
            Check($"15. No se toca el volumen maestro de Windows: predeterminada {defaultDev.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}% (antes {defVol0 * 100:0}%), silenciada={defaultDev.AudioEndpointVolume.Mute} (antes {defMute0}); salida de la app {appDev?.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}% (antes {appVol0 * 100:0}%)",
                Math.Abs(defaultDev.AudioEndpointVolume.MasterVolumeLevelScalar - defVol0) < 0.005 && defaultDev.AudioEndpointVolume.Mute == defMute0
                && appDev is not null && Math.Abs(appDev.AudioEndpointVolume.MasterVolumeLevelScalar - appVol0) < 0.005);

            try { System.Diagnostics.Process.GetProcessById(tonePid).Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            await Task.Delay(5000);
            var b2 = vm.LastStats;
            Check($"16. La aplicación se cierra: «{vm.SourceStatusText}»; el Sonos sigue conectado ({b2?.TransportState}, clientes {b2?.Clients})",
                b2 is { SourceAvailable: false, Clients: 1 });

            await vm.StopAsync(reason: "fin del autotest (parte B)");
            await Task.Delay(500);
            Check($"17. Al detener se restaura la salida de la app (silenciada={appDev?.AudioEndpointVolume.Mute}, antes {appMute0}) y la predeterminada sigue igual ({defaultDev.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%, silenciada={defaultDev.AudioEndpointVolume.Mute}); capturas={LoopbackSource.ActiveCount}",
                appDev?.AudioEndpointVolume.Mute == appMute0 && defaultDev.AudioEndpointVolume.Mute == defMute0
                && Math.Abs(defaultDev.AudioEndpointVolume.MasterVolumeLevelScalar - defVol0) < 0.005 && LoopbackSource.ActiveCount == 0);
        }

        // ── Part B2: the app plays on the user's default output (the usual case). "Silenciar el PC" must send only that
        // app to an unheard output: the Sonos keeps getting it, it goes quiet locally, and no output is muted.
        // The app is a private Chromium instance (CaptureCheck browser-tone: own profile, window off-screen) playing a
        // very quiet tone (−60 dBFS), audible at most faintly until it is sent away. A browser the user isn't running
        // is used, because Windows applies the redirect to every instance of that executable.
        string? browserExe = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            AudioApps.EdgePath() ?? "",
        }.FirstOrDefault(p => p.Length > 0 && File.Exists(p) && System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(p)).Length == 0);
        if (toolPath is not null && browserExe is not null && AppOutputRouting.IsSupported && AudioDevices.FindSilentOutput(null) is { } silent)
        {
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(toolPath, $"browser-tone \"{browserExe}\" 120")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true })!;
            try
            {
                int browserRoot = 0, sessionPid = 0;
                var rootLine = await child.StandardOutput.ReadLineAsync();
                if (rootLine is not null && rootLine.StartsWith("ROOT ")) browserRoot = int.Parse(rootLine[5..]);
                for (int i = 0; i < 60 && browserRoot != 0 && sessionPid == 0; i++) { await Task.Delay(250); sessionPid = await Task.Run(() => AudioApps.SessionProcessOf(browserRoot)); }
                await Task.Delay(1000);
                await vm.RefreshAppsNowAsync();
                var defaultApp = vm.Apps.FirstOrDefault(a => a.Info.ProcessId == browserRoot);
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var defaultDev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                bool defMute0 = defaultDev.AudioEndpointVolume.Mute;
                string? before = sessionPid != 0 ? await Task.Run(() => AppOutputRouting.Get(sessionPid)) : null;
                double local0 = await LocalLevelAsync(defaultDev, 1000);
                Check($"17a. Navegador ({Path.GetFileName(browserExe)}) en la salida predeterminada ({defaultDev.FriendlyName}): sesión en el PID {sessionPid}, suena en local ({local0:0.0} dBFS)",
                    defaultApp is not null && sessionPid != 0 && local0 > -70);
                if (defaultApp is not null && sessionPid != 0)
                {
                    vm.MuteLocal = true;
                    await vm.SelectAppForTestAsync(defaultApp);
                    await vm.StartAsync();
                    await Task.Delay(9000); // past the redirect check (2 s grace + one monitor round)
                    var c1 = vm.LastStats;
                    string? routed = await Task.Run(() => AppOutputRouting.Get(sessionPid));
                    double local1 = await LocalLevelAsync(defaultDev, 1000);
                    Check($"17b. Solo esa app se aparta a «{silent.Name}» (Windows: {(routed == silent.Id ? "sí" : routed ?? "no")}); Sonos {c1?.TransportState}, pico {Peak(c1?.PeakDbfs ?? double.NegativeInfinity)} dBFS (≈ −60)",
                        routed == silent.Id && c1 is { TransportState: "PLAYING", Clients: 1, PeakDbfs: > -66 and < -54 });
                    Check($"17c. En el PC deja de sonar (salida predeterminada {local1:0.0} dBFS, antes {local0:0.0}) sin silenciar ninguna salida (predeterminada silenciada={defaultDev.AudioEndpointVolume.Mute}, antes {defMute0})",
                        local1 < local0 - 20 && defaultDev.AudioEndpointVolume.Mute == defMute0);
                    await vm.StopAsync(reason: "fin del autotest (parte B2)");
                    await Task.Delay(800);
                    string? after = await Task.Run(() => AppOutputRouting.Get(sessionPid));
                    Check($"17d. Al detener, la app vuelve a su salida (Windows: {after ?? "predeterminada"}; antes {before ?? "predeterminada"})",
                        after == before);
                }
            }
            finally
            {
                vm.MuteLocal = false;
                try { child.StandardInput.Close(); child.WaitForExit(15000); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            }
        }
        else lines.Add("—     17a–17d omitidas: sin Chrome/Edge libre, sin salida silenciosa o Windows anterior a 21H2");

        // ── Part C: the user takes the Sonos from the Sonos app (simulated with the same transport commands the app
        // sends; never volume or EQ). Audiora must step aside instead of re-issuing its stream.
        if (vm.SelectedSpeaker is { } speaker)
        try
        {
            using var sonos = new SonosFlow.Core.Sonos.SonosClient(speaker.Device.Address);
            async Task<bool> WaitFor(Func<bool> condition, int seconds)
            {
                for (int i = 0; i < seconds * 4 && !condition(); i++) await Task.Delay(250);
                return condition();
            }

            vm.MuteLocal = false;
            vm.IsAppMode = false;
            await vm.StartAsync();
            await Task.Delay(8000);
            await sonos.PauseAsync();
            bool released = await WaitFor(() => !vm.IsStreaming, 20);
            await Task.Delay(6000);
            var afterPause = await sonos.GetTransportStateAsync();
            Check($"18. Pausa desde la app de Sonos: Audiora se retira («{vm.StatusDetail}») y no reemite (Sonos {afterPause})",
                released && vm.StatusDetail == L.Message(SonosFlow.Core.Streaming.SessionMessages.PausedFromSonos) && afterPause == "PAUSED_PLAYBACK");

            await vm.StartAsync();
            await Task.Delay(8000);
            await sonos.StopAsync(); // once: could be the network → one re-issue
            bool reissued = await WaitFor(() => vm.LastStats is { Reconnects: 1, TransportState: "PLAYING" }, 25);
            Check($"19. Un Stop aislado se trata como fallo de red: se reemite una vez (reconexiones {vm.LastStats?.Reconnects}, {vm.LastStats?.TransportState})", reissued);
            await Task.Delay(3000);
            await sonos.StopAsync(); // again right after our re-issue → the user
            bool gaveWay = await WaitFor(() => !vm.IsStreaming, 25);
            await Task.Delay(6000);
            var afterStop = await sonos.GetTransportStateAsync();
            Check($"20. Stop repetido: Audiora se retira («{vm.StatusDetail}») y el Sonos sigue detenido ({afterStop})",
                gaveWay && vm.StatusDetail == L.Message(SonosFlow.Core.Streaming.SessionMessages.StoppedFromSonos) && afterStop == "STOPPED");
            if (vm.IsStreaming) await vm.StopAsync(reason: "fin del autotest (parte C)");
            Check($"21. Nada queda activo: capturas={LoopbackSource.ActiveCount}, puerto libre={!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 8780)}",
                LoopbackSource.ActiveCount == 0 && !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 8780));
        }
        catch (Exception ex) // e.g. the Sonos rejects Pause on a live stream: record it, keep the results of parts A and B
        {
            Check($"C. La parte C no pudo completarse: {ex.GetType().Name}: {ex.Message}", false);
            if (vm.IsStreaming) await vm.StopAsync(reason: "autotest parte C interrumpida");
        }

        lines.Add(fails == 0 ? "RESULTADO: todo correcto" : $"RESULTADO: {fails} fallos");
        await File.WriteAllLinesAsync(outFile, lines);
        return fails;
    }

    /// <summary>Level (dBFS) of a tone in what a device plays, measured on its loopback for 1.5 s (Goertzel).</summary>
    private static async Task<double> LocalLevelAsync(NAudio.CoreAudioApi.MMDevice device, double hz)
    {
        var samples = new List<float>();
        var source = new LoopbackSource(device);
        source.PcmAvailable += pcm =>
        {
            var span = pcm.Span;
            lock (samples)
                for (int i = 0; i + 3 < span.Length; i += 4)
                    samples.Add((BitConverter.ToInt16(span.Slice(i, 2)) + BitConverter.ToInt16(span.Slice(i + 2, 2))) / 65536f);
        };
        int rate = source.OutputFormat.SampleRate;
        source.Start();
        await Task.Delay(1500);
        source.Stop();
        source.Dispose();
        float[] x;
        lock (samples) x = samples.Skip(rate / 10).ToArray();
        if (x.Length == 0) return double.NegativeInfinity;
        double coeff = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0;
        foreach (var v in x) { double s0 = v + coeff * s1 - s2; s2 = s1; s1 = s0; }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return 20 * Math.Log10(2 * Math.Sqrt(Math.Max(power, 0)) / x.Length + 1e-12);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
}
