using System.Globalization;
using SonosFlow.Poc.Audio;
using SonosFlow.Poc.Control;
using SonosFlow.Poc.Discovery;
using SonosFlow.Poc.Net;

// Sonos Flow — prueba mínima: WASAPI loopback → HTTP (WAV) → Sonos (UPnP AVTransport).
// Uso: SonosFlow.Poc [--list] [--name Five] [--port 8780] [--duration 600] [--volume N] [--mute-local]

var opts = Options.Parse(args);
using var appCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; appCts.Cancel(); };

if (args.Contains("--beep-diag"))
{
    // Local check, no Sonos: does a beep we render show up in our own loopback capture?
    using var src = new LoopbackSource();
    using var mute = opts.MuteLocal ? new LocalOutputMute(src.Device) : null;
    var det = new ToneOnsetDetector(src.OutputFormat.SampleRate, 2000, 0.6, 0.003, 8);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    float rawPeak = 0;
    src.PcmAvailable += pcm =>
    {
        int frames = pcm.Length / 4;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            mono[f] = (BitConverter.ToInt16(pcm.Span.Slice(f * 4, 2)) + BitConverter.ToInt16(pcm.Span.Slice(f * 4 + 2, 2))) / 65536f;
            rawPeak = Math.Max(rawPeak, Math.Abs(mono[f]));
        }
        det.Process(mono, clock.Elapsed.TotalSeconds - (double)frames / src.OutputFormat.SampleRate);
    };
    src.Start();
    var beeper = new BeepProvider(src.OutputFormat.SampleRate);
    using (var output = new NAudio.Wave.WasapiOut(src.Device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 30))
    {
        NAudio.Wave.WaveExtensionMethods.Init(output, beeper);
        output.Play();
        for (int i = 0; i < 3; i++) { await Task.Delay(1000); beeper.Trigger(); }
        await Task.Delay(1000);
        Console.WriteLine($"Salida: estado={output.PlaybackState}");
    }
    src.Stop();
    Console.WriteLine($"Volumen Windows: {src.Device.AudioEndpointVolume.MasterVolumeLevelScalar * 100:F0}% ({src.Device.AudioEndpointVolume.MasterVolumeLevel:F1} dB)");
    Console.WriteLine($"Silenciado={src.Device.AudioEndpointVolume.Mute} beeper lecturas={beeper.Reads} muestras={beeper.BeepFramesPlayed}");
    Console.WriteLine($"loopback: picoBruto={rawPeak:F3} rmsMáx={det.MaxRms:F4} tonalidad={det.TonalityAtMaxRms:F2} tonalidadMáx≥nivel={det.MaxTonalityAboveLevel:F2} detecciones={det.Onsets.Count} [{string.Join(", ", det.Onsets.Select(t => t.ToString("F3")))}]");
    return 0;
}

Console.WriteLine("Buscando altavoces Sonos en la red local…");
var devices = await SonosDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3), appCts.Token);
if (devices.Count == 0)
{
    Console.WriteLine("No se encontró ningún Sonos. ¿Está el PC en la misma red/Wi-Fi y la red marcada como privada?");
    return 2;
}
foreach (var d in devices) Console.WriteLine($"  • {d}  [{d.ModelNumber}]");
if (opts.ListOnly) return 0;

var target = devices.FirstOrDefault(d =>
                 d.RoomName.Contains(opts.Name, StringComparison.OrdinalIgnoreCase) ||
                 d.ModelName.Contains(opts.Name, StringComparison.OrdinalIgnoreCase))
             ?? devices[0];
Console.WriteLine($"Destino: {target}");

var localIp = SonosDiscovery.LocalAddressFor(target.Address);
using var source = new LoopbackSource();
using var server = new WavStreamServer(localIp, opts.Port, source.OutputFormat, devices.Select(d => d.Address));
source.PcmAvailable += server.Write;

Console.WriteLine($"Captura: {source.DeviceName} — {source.SourceFormatDescription}");
Console.WriteLine($"Salida : WAV {source.OutputFormat.SampleRate} Hz, 16 bit, estéreo ({source.OutputFormat.AverageBytesPerSecond / 1000} kB/s)");
Console.WriteLine($"Stream : {server.StreamUrl} (solo acepta conexiones de los Sonos detectados)");

using var sonos = new SonosClient(target.Address);
source.Start();

using var localMute = opts.MuteLocal ? new LocalOutputMute(source.Device) : null;
if (localMute is not null)
{
    // Restore the PC speakers even if the console window is closed instead of Ctrl+C.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => localMute.Dispose();
    Console.WriteLine("Altavoces del PC silenciados durante la transmisión (se restaurarán al terminar).");
}

var startedAt = DateTime.Now;
long sentAtPlaybackStart = 0;
async Task StartPlaybackAsync()
{
    await sonos.SetStreamAsync(server.StreamUrl, WavStreamServer.MimeType, "Sonos Flow — audio del PC", appCts.Token);
    sentAtPlaybackStart = Interlocked.Read(ref server.BytesSent);
    await sonos.PlayAsync(appCts.Token);
}

try
{
    if (opts.Volume is int vol) await sonos.SetVolumeAsync(vol, appCts.Token);
    await StartPlaybackAsync();
    Console.WriteLine($"Reproducción solicitada. Volumen del altavoz: {await sonos.GetVolumeAsync(appCts.Token)}");
}
catch (SonosSoapException ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    if (ex.UpnpErrorCode == 800)
        Console.WriteLine("El altavoz parece estar agrupado y no es el coordinador. Sácalo del grupo o elige el coordinador.");
    Console.WriteLine(ex.Body);
    return 3;
}

Directory.CreateDirectory("logs");
var inv = CultureInfo.InvariantCulture;

if (opts.LatencyTest)
{
    Console.WriteLine("Esperando a que el Sonos reproduzca el stream…");
    for (int i = 0; i < 30 && !(server.ClientCount > 0 && await sonos.GetTransportStateAsync(appCts.Token) == "PLAYING"); i++)
        await Task.Delay(500, appCts.Token);
    await Task.Delay(TimeSpan.FromSeconds(5), appCts.Token); // let the player's buffer settle

    Console.WriteLine("Midiendo latencia con pitidos de 2 kHz (micrófono por defecto)…");
    var result = await new LatencyTest(source).RunAsync(clicks: 10, spacing: TimeSpan.FromSeconds(4), appCts.Token);
    var lines = new List<string>
    {
        $"Fecha: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        $"Destino: {target}",
        $"Micrófono: {result.MicName}",
        $"Pitidos enviados: {result.ClicksSent}; detectados en loopback: {result.ClicksSeenLoopback}; emparejados en micrófono: {result.SamplesSeconds.Count}",
    };
    if (result.SamplesSeconds.Count > 0)
    {
        var s = result.SamplesSeconds.Order().ToList();
        double mean = s.Average();
        double sd = Math.Sqrt(s.Sum(x => (x - mean) * (x - mean)) / s.Count);
        lines.Add($"Latencia extremo a extremo (loopback → sonido del Sonos): mediana {s[s.Count / 2]:F3} s, mín {s[0]:F3} s, máx {s[^1]:F3} s, desv. {sd:F3} s");
        lines.Add("Muestras: " + string.Join(", ", s.Select(x => x.ToString("F3", inv))));
        lines.Add("No incluye el tramo aplicación → mezclador de Windows (normalmente decenas de ms).");
    }
    else
    {
        lines.Add("No se emparejó ningún pitido: el micrófono no oyó el Sonos (distancia/volumen) o la detección falló.");
    }
    lines.Add("Diagnóstico:");
    lines.AddRange(result.Diagnostics.Split('\n'));
    foreach (var l in lines) Console.WriteLine(l);
    var latencyPath = Path.Combine("logs", $"latency-{startedAt:yyyyMMdd-HHmmss}.txt");
    await File.WriteAllLinesAsync(latencyPath, lines);
    Console.WriteLine($"Resultado: {Path.GetFullPath(latencyPath)}");
    appCts.Cancel();
}

var logPath = Path.Combine("logs", $"poc-{startedAt:yyyyMMdd-HHmmss}.csv");
await using var log = new StreamWriter(logPath) { AutoFlush = true };
await log.WriteLineAsync("elapsed_s,transport_state,clients,bytes_sent,send_rate_kBps,captured_kB,silence_kB,peak_dbfs,dropped_chunks,rejected,reconnects,sonos_reltime_s,buffer_est_s");

long lastSent = 0;
int reconnects = 0;
int notPlayingStreak = 0;
Console.WriteLine("Transmitiendo. Ctrl+C para detener." + (opts.DurationSeconds > 0 ? $" Duración: {opts.DurationSeconds} s." : ""));

while (!appCts.IsCancellationRequested)
{
    try { await Task.Delay(1000, appCts.Token); } catch (OperationCanceledException) { break; }

    var elapsed = (DateTime.Now - startedAt).TotalSeconds;
    string state;
    try { state = await sonos.GetTransportStateAsync(appCts.Token); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException) { state = "UNREACHABLE"; }

    long sent = Interlocked.Read(ref server.BytesSent);
    double rate = (sent - lastSent) / 1000.0;
    lastSent = sent;
    double peakDb = source.LastPeak > 0 ? 20 * Math.Log10(source.LastPeak) : double.NegativeInfinity;

    // ESTIMATE (±1 s, RelTime resolution): audio delivered to the Sonos minus what it reports having played.
    TimeSpan? relTime = null;
    try { relTime = await sonos.GetRelTimeAsync(appCts.Token); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException) { }
    double? bufferEst = relTime is { } rt
        ? (sent - sentAtPlaybackStart) / (double)source.OutputFormat.AverageBytesPerSecond - rt.TotalSeconds
        : null;

    Console.Write($"\r[{elapsed,6:F0}s] {state,-16} clientes={server.ClientCount} envío={rate,6:F1} kB/s pico={(double.IsInfinity(peakDb) ? "  -inf" : peakDb.ToString("F1", inv)),6} dBFS descartes={server.DroppedChunks} reconex={reconnects}   ");
    await log.WriteLineAsync(string.Join(',',
        elapsed.ToString("F0", inv), state, server.ClientCount, sent, rate.ToString("F1", inv),
        (source.CapturedBytes / 1000).ToString(inv), (source.SilenceBytes / 1000).ToString(inv),
        double.IsInfinity(peakDb) ? "" : peakDb.ToString("F1", inv),
        server.DroppedChunks, server.RejectedConnections, reconnects,
        relTime?.TotalSeconds.ToString("F0", inv) ?? "", bufferEst?.ToString("F1", inv) ?? ""));

    if (source.CaptureError is { } capErr)
    {
        Console.WriteLine($"\nLa captura se detuvo: {capErr.Message}");
        break;
    }

    // Recovery: if the player stops pulling our stream (network drop, Sonos app took over), re-issue it.
    notPlayingStreak = state is "PLAYING" or "TRANSITIONING" && server.ClientCount > 0 ? 0 : notPlayingStreak + 1;
    if (notPlayingStreak >= 5)
    {
        notPlayingStreak = 0;
        reconnects++;
        Console.WriteLine($"\nEl Sonos no está reproduciendo el stream ({state}); reintentando…");
        try { await StartPlaybackAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException)
        {
            Console.WriteLine($"Reintento fallido: {ex.Message}");
        }
    }

    if (opts.DurationSeconds > 0 && elapsed >= opts.DurationSeconds) break;
}

Console.WriteLine("\nDeteniendo…");
try { await sonos.StopAsync(); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException) { }
source.Stop();
if (localMute is not null)
{
    localMute.Dispose();
    Console.WriteLine($"Altavoces del PC restaurados (silenciado ahora: {source.Device.AudioEndpointVolume.Mute}).");
}
if (server.LastClientInfo is { } info) Console.WriteLine($"Última petición del Sonos: {info}");
Console.WriteLine($"Registro: {Path.GetFullPath(logPath)}");
return 0;

sealed record Options(bool ListOnly, string Name, int Port, int DurationSeconds, int? Volume, bool MuteLocal, bool LatencyTest)
{
    public static Options Parse(string[] args)
    {
        string? Value(string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        return new Options(
            args.Contains("--list"),
            Value("--name") ?? "Five",
            int.TryParse(Value("--port"), out var p) ? p : 8780,
            int.TryParse(Value("--duration"), out var d) ? d : 0,
            int.TryParse(Value("--volume"), out var v) ? v : null,
            args.Contains("--mute-local"),
            args.Contains("--latency-test"));
    }
}
