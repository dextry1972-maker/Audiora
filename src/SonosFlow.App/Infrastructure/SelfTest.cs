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
                vm.SourceName == app.Name && vm.MiniStatusText == "Transmitiendo audio");
            Check($"5b. Tras cambiar de fuente sigue habiendo una sola captura (capturas={LoopbackSource.ActiveCount})", LoopbackSource.ActiveCount == 1);

            vm.PauseResumeCommand.Execute(null);
            await Task.Delay(3000);
            var s2 = vm.LastStats;
            Check($"6. Pausa: silencio sin desconectar (pico {Peak(s2?.PeakDbfs ?? 0)}, {s2?.SendRateKBps:0} kB/s, {s2?.TransportState}, estado «{vm.MiniStatusText}»)",
                s2 is { Clients: 1, SendRateKBps: > 150 } && double.IsNegativeInfinity(s2.PeakDbfs) && vm.MiniStatusText == "En pausa");
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

        // ── Part C: the user takes the Sonos from the Sonos app (simulated with the same transport commands the app
        // sends; never volume or EQ). Audiora must step aside instead of re-issuing its stream.
        if (vm.SelectedSpeaker is { } speaker)
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
                released && vm.StatusDetail.Contains("pausó") && afterPause == "PAUSED_PLAYBACK");

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
                gaveWay && vm.StatusDetail.Contains("detuvo") && afterStop == "STOPPED");
            if (vm.IsStreaming) await vm.StopAsync(reason: "fin del autotest (parte C)");
            Check($"21. Nada queda activo: capturas={LoopbackSource.ActiveCount}, puerto libre={!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 8780)}",
                LoopbackSource.ActiveCount == 0 && !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 8780));
        }

        lines.Add(fails == 0 ? "RESULTADO: todo correcto" : $"RESULTADO: {fails} fallos");
        await File.WriteAllLinesAsync(outFile, lines);
        return fails;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
}
