using System.Globalization;
using System.Net;
using NAudio.CoreAudioApi;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Net;
using SonosFlow.Core.Sonos;

namespace SonosFlow.Core.Streaming;

public enum SessionState { Starting, Streaming, Recovering, Stopped, Failed }

public enum CaptureKind { SystemOutput, Application }

/// <summary>What to stream: everything an output device plays, or only one application (and its child processes).</summary>
/// <param name="DeviceId">SystemOutput: device to capture, null = Windows default.</param>
/// <param name="ProcessId">Application: root process to capture.</param>
/// <param name="ExePath">Application: lets the session follow the app if it restarts.</param>
public sealed record CaptureSpec(CaptureKind Kind, string DisplayName, string? DeviceId = null, int ProcessId = 0,
    string? ExePath = null, bool IsIsolatedYouTubeMusic = false)
{
    public static CaptureSpec SystemOutput(string? deviceId, string name = "Todo el audio del PC") => new(CaptureKind.SystemOutput, name, deviceId);

    public static CaptureSpec Application(AudioAppInfo app) =>
        new(CaptureKind.Application, app.Name, null, app.ProcessId, app.ExePath, app.IsIsolatedYouTubeMusic);
}

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
    string? LastError,
    string SourceName = "",
    bool SourceAvailable = true,
    bool Paused = false,
    double PeakDbfs = double.NegativeInfinity);

/// <summary>
/// One streaming run: capture (whole output or one app) → WAV over HTTP → Sonos pulls it.
/// The source can be switched and paused without reconnecting the Sonos. Watches the player
/// every second and re-issues the stream if it stops pulling it.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StreamingSession : IAsyncDisposable
{
    private const string StreamTitle = "Audiora — audio del PC";
    /// <summary>Rate used for application capture (Windows converts the app's audio to it).</summary>
    public const int AppCaptureSampleRate = 48000;

    private readonly SonosDevice _target;
    private readonly IReadOnlyCollection<IPAddress> _allowedPeers;
    private readonly StreamOptions _options;
    private readonly string _guardStateFile;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly object _sourceLock = new();

    private CaptureSpec _spec;
    private MMDevice? _captureDevice;
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
    private volatile bool _paused;
    private volatile bool _sourceAvailable = true;
    private byte[] _silence = new byte[19200];
    private float _peakSinceLog;

    public SessionState State { get; private set; } = SessionState.Starting;
    public SonosDevice Target => _target;
    public CaptureSpec Source => _spec;
    public string CaptureDeviceName { get; private set; } = "";
    public string SourceFormat => _source?.SourceFormatDescription ?? "";
    public string StreamFormat => _server is null ? "" : $"WAV PCM · {_streamRate / 1000.0:0.#} kHz · 16 bit · estéreo";
    public string StreamUrl => _server?.StreamUrl ?? "";
    public SpectrumAnalyzer? Spectrum { get; private set; }
    public bool Paused => _paused;
    private int _streamRate;

    public event Action<StreamStats>? StatsUpdated;
    public event Action<SessionState>? StateChanged;
    /// <summary>Raised after the captured source changed (manual switch or the app restarted).</summary>
    public event Action<CaptureSpec>? SourceChanged;

    /// <remarks>Core Audio objects are created and released on thread-pool (MTA) threads only.</remarks>
    public StreamingSession(SonosDevice target, IEnumerable<IPAddress> allowedPeers, CaptureSpec source, StreamOptions options, string guardStateFile)
    {
        _target = target;
        _allowedPeers = [.. allowedPeers.Append(target.Address).Distinct()];
        _spec = source;
        _options = options;
        _guardStateFile = guardStateFile;
    }

    public Task StartAsync(CancellationToken ct = default) => Task.Run(() => StartCoreAsync(ct), ct);

    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            var localIp = SonosDiscovery.LocalAddressFor(_target.Address);
            _source = CreateSource(_spec, out _captureDevice);
            _streamRate = _source.OutputFormat.SampleRate;
            Spectrum = new SpectrumAnalyzer(_streamRate);
            _server = new WavStreamServer(localIp, _options.Port, _source.OutputFormat, _allowedPeers);
            _source.PcmAvailable += OnPcm;
            _sonos = new SonosClient(_target.Address);

            _source.Start();
            ApplyGuard();

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

    /// <summary>
    /// Replaces the captured source while the Sonos keeps the same connection. Returns false when the
    /// new source can't feed the current stream format (a different device sample rate); the caller
    /// then restarts the stream.
    /// </summary>
    public Task<bool> SwitchSourceAsync(CaptureSpec spec) => Task.Run(async () =>
    {
        await _switchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_server is null || State is SessionState.Stopped or SessionState.Failed) return false;
            var next = CreateSource(spec, out var nextDevice, _streamRate);
            if (next.OutputFormat.SampleRate != _streamRate)
            {
                next.Dispose();
                nextDevice?.Dispose();
                return false;
            }

            LoopbackSource? old;
            MMDevice? oldDevice;
            lock (_sourceLock)
            {
                old = _source;
                oldDevice = _captureDevice;
                if (old is not null) old.PcmAvailable -= OnPcm;
                _source = next;
                _captureDevice = nextDevice;
                _spec = spec;
                _sourceAvailable = true;
            }
            next.PcmAvailable += OnPcm;
            next.Start(); // start the new capture before stopping the old one: no gap in the stream
            old?.Dispose();

            _guard?.Dispose(); // restore the previous output before (maybe) guarding a new one
            _guard = null;
            oldDevice?.Dispose();
            ApplyGuard();

            _log?.WriteLine($"# fuente cambiada a: {spec.DisplayName} ({CaptureDeviceName})");
            SourceChanged?.Invoke(spec);
            return true;
        }
        finally
        {
            _switchLock.Release();
        }
    });

    /// <summary>Sends silence instead of the captured audio. The Sonos stays connected, so resuming is instant.</summary>
    public void SetPaused(bool paused) => _paused = paused;

    private LoopbackSource CreateSource(CaptureSpec spec, out MMDevice? device, int? requiredRate = null)
    {
        if (spec.Kind == CaptureKind.Application)
        {
            device = null;
            CaptureDeviceName = spec.DisplayName;
            return LoopbackSource.ForProcess(spec.ProcessId, spec.DisplayName, requiredRate ?? AppCaptureSampleRate);
        }
        device = AudioDevices.Resolve(spec.DeviceId);
        CaptureDeviceName = device.FriendlyName;
        return new LoopbackSource(device);
    }

    /// <summary>
    /// "Silenciar el PC" mutes the captured output device, which only makes sense when capturing the
    /// whole output: with a single app it would also silence games, Discord, etc.
    /// </summary>
    private void ApplyGuard()
    {
        if (_options.MuteLocal && _spec.Kind == CaptureKind.SystemOutput && _captureDevice is not null)
            _guard = new LocalOutputGuard(_captureDevice, _options.FullLevel, _guardStateFile);
    }

    private void OnPcm(ReadOnlyMemory<byte> pcm)
    {
        if (_paused)
        {
            if (_silence.Length < pcm.Length) _silence = new byte[pcm.Length];
            pcm = _silence.AsMemory(0, pcm.Length);
        }
        _server?.Write(pcm);
        Spectrum?.Process(pcm.Span);
        var span = pcm.Span;
        float peak = 0;
        for (int i = 0; i + 1 < span.Length; i += 2) peak = MathF.Max(peak, MathF.Abs(BitConverter.ToInt16(span.Slice(i, 2)) / 32768f));
        if (peak > _peakSinceLog) _peakSinceLog = peak; // what was actually sent, after pause
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
        await _switchLock.WaitAsync().ConfigureAwait(false);
        try { await TearDownAsync(stopSonos: true).ConfigureAwait(false); }
        finally { _switchLock.Release(); }
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
        int unhealthy = 0, tick = 0;
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
                    bufferEst = (sent - _sentAtPlaybackStart) / (double)(_streamRate * 4) - rel.TotalSeconds;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException && !ct.IsCancellationRequested)
            {
                transport = "UNREACHABLE";
                _lastError = "El Sonos no responde en la red";
            }

            if (_source?.CaptureError is { } capErr)
            {
                _lastError = $"La captura de audio se detuvo: {capErr.Message}";
                Publish(transport, sent, lastSent, bufferEst); // error reaches listeners before the state change
                SetState(SessionState.Failed);
                return;
            }

            // The selected app closed: keep the stream alive (silence) and follow it if it comes back.
            if (_spec.Kind == CaptureKind.Application && ++tick % 3 == 0)
                await FollowApplicationAsync().ConfigureAwait(false);

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

    private async Task FollowApplicationAsync()
    {
        if (AudioApps.IsRunning(_spec.ProcessId))
        {
            _sourceAvailable = true;
            return;
        }
        _sourceAvailable = false;
        if (_spec.ExePath is null) return;
        var again = AudioApps.FindByExe(_spec.ExePath, _spec.IsIsolatedYouTubeMusic);
        if (again is not null && await SwitchSourceAsync(CaptureSpec.Application(again)).ConfigureAwait(false))
            _lastError = null;
    }

    private void Publish(string transport, long sent, long lastSent, double? bufferEst)
    {
        var stats = new StreamStats(State, transport, DateTime.Now - _startedAt, (sent - lastSent) / 1000.0, sent, bufferEst,
            Interlocked.Read(ref _server!.DroppedChunks), _reconnects, _server.ClientCount, _source?.SilenceBytes ?? 0, _lastError,
            _spec.DisplayName, _sourceAvailable, _paused,
            _peakSinceLog > 0 ? 20 * Math.Log10(_peakSinceLog) : double.NegativeInfinity);
        _peakSinceLog = 0;
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
            _log.WriteLine("elapsed_s,state,transport,clients,send_rate_kBps,bytes_sent,silence_kB,dropped_chunks,reconnects,buffer_est_s,error,source,paused,peak_dbfs");
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
                s.LastError?.Replace(',', ';') ?? "", s.SourceName.Replace(',', ';'), s.Paused ? 1 : 0,
                double.IsNegativeInfinity(s.PeakDbfs) ? "" : s.PeakDbfs.ToString("F1", inv)));
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
        lock (_sourceLock)
        {
            _source?.Dispose();
            _source = null;
        }
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
        _switchLock.Dispose();
    }
}
