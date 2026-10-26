using System.Globalization;
using System.Net;
using NAudio.CoreAudioApi;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Net;
using SonosFlow.Core.Sonos;

namespace SonosFlow.Core.Streaming;

public enum SessionState { Starting, Streaming, Recovering, Stopped, Failed }

public sealed record StreamOptions(int Port = 8780, bool MuteLocal = true, bool FullLevel = false, string? LogDirectory = null);

/// <summary>Real values read from the pipeline and the Sonos once per second.</summary>
public sealed record StreamStats(
    SessionState State,
    string TransportState,
    TimeSpan Elapsed,
    double SendRateKBps,
    long BytesSent,
    double? BufferEstimateSeconds,
    long DroppedChunks,
    int Reconnects,
    int Clients,
    long SilenceBytes,
    string? LastError);

/// <summary>
/// One streaming run: WASAPI loopback → WAV over HTTP → Sonos pulls it.
/// Watches the player every second and re-issues the stream if it stops pulling it.
/// </summary>
public sealed class StreamingSession : IAsyncDisposable
{
    private const string StreamTitle = "Sonos Flow — audio del PC";

    private readonly SonosDevice _target;
    private readonly IReadOnlyCollection<IPAddress> _allowedPeers;
    private readonly string? _captureDeviceId;
    private MMDevice? _captureDevice;
    private readonly StreamOptions _options;
    private readonly string _guardStateFile;
    private readonly CancellationTokenSource _cts = new();

    private LoopbackSource? _source;
    private WavStreamServer? _server;
    private SonosClient? _sonos;
    private LocalOutputGuard? _guard;
    private Task? _monitor;
    private StreamWriter? _log;
    private DateTime _startedAt;
    private long _sentAtPlaybackStart;
    private int _reconnects;
    private string? _lastError;

    public SessionState State { get; private set; } = SessionState.Starting;
    public SonosDevice Target => _target;
    public string CaptureDeviceName { get; private set; } = "";
    public string SourceFormat => _source?.SourceFormatDescription ?? "";
    public string StreamFormat => _source is null ? "" : $"WAV PCM · {_source.OutputFormat.SampleRate / 1000.0:0.#} kHz · 16 bit · estéreo";
    public string StreamUrl => _server?.StreamUrl ?? "";
    public SpectrumAnalyzer? Spectrum { get; private set; }

    public event Action<StreamStats>? StatsUpdated;
    public event Action<SessionState>? StateChanged;

    /// <param name="captureDeviceId">Output device to capture; null = Windows default.</param>
    /// <remarks>Core Audio objects are created and released on thread-pool (MTA) threads only.</remarks>
    public StreamingSession(SonosDevice target, IEnumerable<IPAddress> allowedPeers, string? captureDeviceId, StreamOptions options, string guardStateFile)
    {
        _target = target;
        _allowedPeers = [.. allowedPeers.Append(target.Address).Distinct()];
        _captureDeviceId = captureDeviceId;
        _options = options;
        _guardStateFile = guardStateFile;
    }

    public Task StartAsync(CancellationToken ct = default) => Task.Run(() => StartCoreAsync(ct), ct);

    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            var localIp = SonosDiscovery.LocalAddressFor(_target.Address);
            _captureDevice = AudioDevices.Resolve(_captureDeviceId);
            CaptureDeviceName = _captureDevice.FriendlyName;
            _source = new LoopbackSource(_captureDevice);
            Spectrum = new SpectrumAnalyzer(_source.OutputFormat.SampleRate);
            _server = new WavStreamServer(localIp, _options.Port, _source.OutputFormat, _allowedPeers);
            _source.PcmAvailable += _server.Write;
            _source.PcmAvailable += pcm => Spectrum.Process(pcm.Span);
            _sonos = new SonosClient(_target.Address);

            _source.Start();
            if (_options.MuteLocal) _guard = new LocalOutputGuard(_captureDevice, _options.FullLevel, _guardStateFile);

            await StartPlaybackAsync(ct).ConfigureAwait(false);
            _startedAt = DateTime.Now;
            OpenLog();
            SetState(SessionState.Streaming);
            _monitor = Task.Run(() => MonitorAsync(_cts.Token));
        }
        catch
        {
            await TearDownAsync(stopSonos: false).ConfigureAwait(false);
            SetState(SessionState.Failed);
            throw;
        }
    }

    public Task StopAsync() => Task.Run(StopCoreAsync);

    private async Task StopCoreAsync()
    {
        if (State is SessionState.Stopped) return;
        _cts.Cancel();
        if (_monitor is not null)
        {
            try { await _monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await TearDownAsync(stopSonos: true).ConfigureAwait(false);
        SetState(SessionState.Stopped);
    }

    private async Task StartPlaybackAsync(CancellationToken ct)
    {
        await _sonos!.SetStreamAsync(_server!.StreamUrl, WavStreamServer.MimeType, StreamTitle, ct);
        _sentAtPlaybackStart = Interlocked.Read(ref _server.BytesSent);
        await _sonos.PlayAsync(ct);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        long lastSent = Interlocked.Read(ref _server!.BytesSent);
        int unhealthy = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            string transport;
            double? bufferEst = null;
            long sent = Interlocked.Read(ref _server.BytesSent);
            try
            {
                transport = await _sonos!.GetTransportStateAsync(ct);
                // ESTIMATE (±1 s): audio delivered to the Sonos minus the position it reports.
                if (await _sonos.GetRelTimeAsync(ct) is { } rel)
                    bufferEst = (sent - _sentAtPlaybackStart) / (double)_source!.OutputFormat.AverageBytesPerSecond - rel.TotalSeconds;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException && !ct.IsCancellationRequested)
            {
                transport = "UNREACHABLE";
                _lastError = "El Sonos no responde en la red";
            }

            if (_source!.CaptureError is { } capErr)
            {
                _lastError = $"La captura de audio se detuvo: {capErr.Message}";
                Publish(transport, sent, lastSent, bufferEst); // error reaches listeners before the state change
                SetState(SessionState.Failed);
                return;
            }

            bool healthy = transport is "PLAYING" or "TRANSITIONING" && _server.ClientCount > 0;
            unhealthy = healthy ? 0 : unhealthy + 1;
            if (healthy && State == SessionState.Recovering) { SetState(SessionState.Streaming); _lastError = null; }

            // Not pulling for 5 s (Wi-Fi drop, another app took the speaker): re-issue our stream.
            if (unhealthy >= 5)
            {
                unhealthy = 0;
                _reconnects++;
                SetState(SessionState.Recovering);
                try
                {
                    await StartPlaybackAsync(ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException && !ct.IsCancellationRequested)
                {
                    _lastError = $"Reintento fallido: {ex.Message}";
                }
            }

            Publish(transport, sent, lastSent, bufferEst);
            lastSent = sent;
        }
    }

    private void Publish(string transport, long sent, long lastSent, double? bufferEst)
    {
        var stats = new StreamStats(State, transport, DateTime.Now - _startedAt, (sent - lastSent) / 1000.0, sent, bufferEst,
            Interlocked.Read(ref _server!.DroppedChunks), _reconnects, _server.ClientCount, _source!.SilenceBytes, _lastError);
        WriteLog(stats);
        StatsUpdated?.Invoke(stats);
    }

    private void OpenLog()
    {
        if (_options.LogDirectory is null) return;
        try
        {
            Directory.CreateDirectory(_options.LogDirectory);
            var path = Path.Combine(_options.LogDirectory, $"stream-{_startedAt:yyyyMMdd-HHmmss}.csv");
            _log = new StreamWriter(path) { AutoFlush = true };
            _log.WriteLine($"# target={_target.RoomName} {_target.ModelName} {_target.Address}; capture={CaptureDeviceName}; {SourceFormat}");
            _log.WriteLine("elapsed_s,state,transport,clients,send_rate_kBps,bytes_sent,silence_kB,dropped_chunks,reconnects,buffer_est_s,error");
        }
        catch (IOException) { _log = null; }
    }

    private void WriteLog(StreamStats s)
    {
        if (_log is null) return;
        var inv = CultureInfo.InvariantCulture;
        try
        {
            _log.WriteLine(string.Join(',',
                s.Elapsed.TotalSeconds.ToString("F0", inv), s.State, s.TransportState, s.Clients,
                s.SendRateKBps.ToString("F1", inv), s.BytesSent, (s.SilenceBytes / 1000).ToString(inv),
                s.DroppedChunks, s.Reconnects, s.BufferEstimateSeconds?.ToString("F1", inv) ?? "",
                s.LastError?.Replace(',', ';') ?? ""));
        }
        catch (IOException) { }
    }

    private async Task TearDownAsync(bool stopSonos)
    {
        if (stopSonos && _sonos is not null)
        {
            try { await _sonos.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException) { }
        }
        // Restore the PC speakers before anything else can fail.
        _guard?.Dispose();
        _guard = null;
        _source?.Dispose();
        _source = null;
        _server?.Dispose();
        _server = null;
        _sonos?.Dispose();
        _sonos = null;
        _captureDevice?.Dispose();
        _captureDevice = null;
        _log?.Dispose();
        _log = null;
    }

    private void SetState(SessionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
