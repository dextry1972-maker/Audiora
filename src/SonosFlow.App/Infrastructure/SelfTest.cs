using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SonosFlow.App.ViewModels;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.Infrastructure;

/// <summary>
/// End-to-end check with the real Sonos through the same view model the windows use
/// (<c>--selftest-session &lt;pid&gt; --selftest-out &lt;file&gt;</c>). The target process should play a very
/// quiet tone. Settings are not saved and the PC's mute/volume are not touched during the run.
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

            try { System.Diagnostics.Process.GetProcessById(tonePid).Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            await Task.Delay(5000);
            var s3 = vm.LastStats;
            Check($"8. La aplicación se cierra: «{vm.SourceStatusText}»; el Sonos sigue conectado ({s3?.TransportState}, clientes {s3?.Clients})",
                s3 is { SourceAvailable: false, Clients: 1 });
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

        lines.Add(fails == 0 ? "RESULTADO: todo correcto" : $"RESULTADO: {fails} fallos");
        await File.WriteAllLinesAsync(outFile, lines);
        return fails;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
}
