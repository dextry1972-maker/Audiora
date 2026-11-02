using System.Globalization;
using System.Net;
using NAudio.CoreAudioApi;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Net;
using SonosFlow.Core.Sonos;

namespace SonosFlow.Core.Streaming;

/// <remarks><see cref="Released"/>: the user took the Sonos for something else (another source, or paused it in the
/// Sonos app); the session ended on purpose and must not be re-issued.</remarks>
public enum SessionState { Starting, Streaming, Recovering, Stopped, Failed, Released }

public enum CaptureKind { SystemOutput, Application }

/// <summary>What to stream: everything an output device plays, or only one application (and its child processes).</summary>
/// <param name="DeviceId">SystemOutput: device to capture. Application: device the app plays on (muted locally
/// when "Silenciar el PC" is on). Null = Windows default.</param>
/// <param name="ProcessId">Application: root process to capture.</param>
/// <param name="ExePath">Application: lets the session follow the app if it restarts.</param>
public sealed record CaptureSpec(CaptureKind Kind, string DisplayName, string? DeviceId = null, int ProcessId = 0,
    string? ExePath = null, bool IsIsolatedYouTubeMusic = false)
{
    public static CaptureSpec SystemOutput(string? deviceId, string name = "Todo el audio del PC") => new(CaptureKind.SystemOutput, name, deviceId);

    public static CaptureSpec Application(AudioAppInfo app) =>
        new(CaptureKind.Application, app.Name, app.OutputDeviceId, app.ProcessId, app.ExePath, app.IsIsolatedYouTubeMusic);
}

/// <param name="PrebufferMs">"Margen anti-cortes": audio handed to the Sonos at once when it connects, so it plays
/// that far behind live and rides out network pauses shorter than it. 0 = pure live (minimum latency).</param>
public sealed record StreamOptions(int Port = 8780, bool MuteLocal = true, bool FullLevel = false, string? LogDirectory = null,
    int PrebufferMs = 0);

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
    double PeakDbfs = double.NegativeInfinity,
    double BacklogMaxMs = 0,
    int Stalls = 0,
    double StallWorstMs = 0,
    int StallsOverMargin = 0);

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
    private bool _muteLocal, _fullLevel; // start from _options; "Silenciar el PC" can change while streaming
    private volatile bool _tornDown;
    private volatile bool _wantedMute;
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
    private MMDevice? _guardDevice; // device muted in application mode when the app can't be redirected
    private AppOutputRedirect? _redirect; // application mode: only that app sent to an unheard output
    private volatile bool _redirectPending;
    private string? _redirectTarget;
    private DateTime _redirectAt;
    private int _redirectStrikes;
    private volatile bool _redirectVerified;
    private readonly string _redirectStateFile;
    private Task? _monitor;
    private TextWriter? _log;
    private SonosFlow.Core.Diagnostics.StreamDiagnostics? _diag;
    private DateTime _startedAt;
    private long _sentAtPlaybackStart;
    private int _reconnects;
    private string? _lastError;
    private volatile bool _paused;
    private volatile bool _sourceAvailable = true;
    private byte[] _silence = new byte[19200];
    private float _peakSinceLog;
    private TimeSpan _lastCpuTime = ReadCpuTime();
    private long _lastCpuSample = System.Diagnostics.Stopwatch.GetTimestamp();

    public SessionState State { get; private set; } = SessionState.Starting;
    public SonosDevice Target => _target;
    public CaptureSpec Source => _spec;
    public string CaptureDeviceName { get; private set; } = "";
    public string SourceFormat => _source?.SourceFormatDescription ?? "";
    public string StreamFormat => _server is null ? "" : $"WAV PCM · {_streamRate / 1000.0:0.#} kHz · 16 bit · 2 ch";
    public string StreamUrl => _server?.StreamUrl ?? "";
    public SpectrumAnalyzer? Spectrum { get; private set; }
    public bool Paused => _paused;
    private int _streamRate;
    private StallTracker? _stalls;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private const string SpeakerUnreachable = SessionMessages.SpeakerUnreachable;

    public event Action<StreamStats>? StatsUpdated;
    public event Action<SessionState>? StateChanged;
    /// <summary>Connection-level events (player connects/disconnects), for the app's event log.</summary>
    public event Action<string>? Diagnostic;
    /// <summary>Raised after the captured source changed (manual switch or the app restarted).</summary>
    public event Action<CaptureSpec>? SourceChanged;

    /// <remarks>Core Audio objects are created and released on thread-pool (MTA) threads only.</remarks>
    public StreamingSession(SonosDevice target, IEnumerable<IPAddress> allowedPeers, CaptureSpec source, StreamOptions options, string guardStateFile)
    {
        _target = target;
        _allowedPeers = [.. allowedPeers.Append(target.Address).Distinct()];
        _spec = source;
        _options = options;
        (_muteLocal, _wantedMute, _fullLevel) = (options.MuteLocal, options.MuteLocal, options.FullLevel);
        _guardStateFile = guardStateFile;
        _redirectStateFile = RedirectStateFileFor(guardStateFile);
    }

    /// <summary>Where the app redirect keeps its undo information (next to the output guard's).</summary>
    public static string RedirectStateFileFor(string guardStateFile) =>
        Path.Combine(Path.GetDirectoryName(guardStateFile) ?? "", "app-output-guard.json");

    public Task StartAsync(CancellationToken ct = default) => Task.Run(() => StartCoreAsync(ct), ct);

    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            var localIp = SonosDiscovery.LocalAddressFor(_target.Address);
            _source = CreateSource(_spec, out _captureDevice);
            _streamRate = _source.OutputFormat.SampleRate;
            Spectrum = new SpectrumAnalyzer(_streamRate);
            _diag = new SonosFlow.Core.Diagnostics.StreamDiagnostics(_options.LogDirectory, _streamRate, _spec.Kind == CaptureKind.Application ? "app" : "system", prebufferMs: _options.PrebufferMs);
            _source.Diagnostics = _diag;
            _server = new WavStreamServer(localIp, _options.Port, _source.OutputFormat, _allowedPeers, _options.PrebufferMs) { Diagnostics = _diag };
            _server.ConnectionEvent += msg =>
            {
                try { _log?.WriteLine($"# {DateTime.Now:HH:mm:ss.fff} {msg}"); } catch (IOException) { } catch (ObjectDisposedException) { }
                Diagnostic?.Invoke(msg);
            };
            _source.PcmAvailable += OnPcm;
            _sonos = new SonosClient(_target.Address);

            _source.Start();
            await _switchLock.WaitAsync(ct).ConfigureAwait(false); // SetMuteLocalAsync may run meanwhile
            try { ApplyGuard(); }
            finally { _switchLock.Release(); }

            await StartPlaybackAsync(ct).ConfigureAwait(false);
            _startedAt = DateTime.Now;
            OpenLog();
            SetState(SessionState.Streaming);
            _monitor = Task.Run(() => MonitorAsync(_cts.Token));
        }
        catch
        {
            await _switchLock.WaitAsync().ConfigureAwait(false);
            try { await TearDownAsync(stopSonos: false).ConfigureAwait(false); }
            finally { _switchLock.Release(); }
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
            if (_server is null || State is SessionState.Stopped or SessionState.Failed or SessionState.Released) return false;
            var oldName = CaptureDeviceName;
            var next = CreateSource(spec, out var nextDevice, _streamRate);
            if (next.OutputFormat.SampleRate != _streamRate)
            {
                CaptureDeviceName = oldName; // the rejected source must not rename the running one
                next.Dispose();
                nextDevice?.Dispose();
                return false;
            }

            // Activate the new capture first, unhooked (activation can take seconds), while the old one keeps
            // feeding the stream; then hand over in one step. If activation fails nothing has changed.
            try { next.Start(); }
            catch
            {
                CaptureDeviceName = oldName;
                next.Dispose();
                nextDevice?.Dispose();
                throw;
            }

            LoopbackSource? old;
            MMDevice? oldDevice;
            lock (_sourceLock)
            {
                old = _source;
                oldDevice = _captureDevice;
                if (old is not null) old.PcmAvailable -= OnPcm;
                next.Diagnostics = _diag;
                next.PcmAvailable += OnPcm;
                _source = next;
                _captureDevice = nextDevice;
                _spec = spec;
                _sourceAvailable = true;
            }
            old?.Dispose();

            ReleaseGuard(); // restore the previous output before guarding the new one
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

    /// <summary>
    /// "Silenciar el PC" changed while streaming: applied at once (redirect / mute, or their undo); the capture and
    /// the Sonos are not touched. Turning it off also drops "Nivel completo" for this run, which is only safe while
    /// the PC is muted. False when muting could not be applied (the PC then stays as it was).
    /// </summary>
    public Task<bool> SetMuteLocalAsync(bool mute)
    {
        _wantedMute = mute; // quick on/off/on presses: whichever task gets the lock applies the latest wish
        return Task.Run(async () =>
        {
            await _switchLock.WaitAsync().ConfigureAwait(false);
            try { return ReconcileMute(); }
            finally { _switchLock.Release(); }
        });
    }

    /// <summary>
    /// Gives the PC back right now, synchronously, without stopping the stream (Windows sign-out / shutdown: the process
    /// can be killed as soon as the handler returns, so the normal asynchronous stop may never finish). Nothing guards
    /// the PC again afterwards; the usual stop still runs and finds it already restored. Waits at most
    /// <paramref name="timeout"/> for a switch or toggle in progress, then restores anyway.
    /// </summary>
    public void RestoreLocalNow(TimeSpan timeout)
    {
        bool locked = false;
        _tornDown = true; // first: from now on no switch, toggle or monitor retry may guard the PC again, lock or no lock
        try
        {
            try { locked = _switchLock.Wait(timeout); } catch (ObjectDisposedException) { return; }
            try { ReleaseGuard(); }
            catch (Exception ex) { Note($"silencio local: no se pudo restaurar al cerrar sesión ({ex.GetType().Name})"); }
        }
        finally
        {
            if (locked) { try { _switchLock.Release(); } catch (ObjectDisposedException) { } }
        }
    }

    /// <summary>True while the session can still guard the PC (not stopped, failed, released or being torn down).</summary>
    public bool IsActive => !_tornDown && State is not (SessionState.Stopped or SessionState.Failed or SessionState.Released);

    /// <summary>Brings the PC in line with <see cref="_wantedMute"/>. Call with <c>_switchLock</c> held.</summary>
    private bool ReconcileMute()
    {
        bool mute = _wantedMute;
        if (!IsActive) return false;
        if (_muteLocal == mute) return true;
        _muteLocal = mute;
        if (!mute)
        {
            _fullLevel = false;
            ReleaseGuard();
            Note("silencio local desactivado durante la transmisión");
            return true;
        }
        try
        {
            ApplyGuard();
            Note("silencio local activado durante la transmisión");
            return true;
        }
        catch (Exception ex)
        {
            // Whatever failed, leave the PC as it was: nothing guarded, nothing pinned.
            try { ReleaseGuard(); } catch (Exception) { }
            _muteLocal = false;
            Note($"silencio local: no se pudo activar ({ex.GetType().Name})");
            return false;
        }
    }

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
    /// "Silenciar el PC": what is heard locally goes quiet and is restored afterwards.
    /// Whole output: the captured device is muted, optionally at 100 % ("Nivel completo") because that capture
    /// is taken after the Windows volume. Application: only that app is sent to an output nobody hears
    /// (<see cref="AppOutputRedirect"/>); other apps, the device's mute and the Windows volume stay as they are.
    /// Only when this PC can't (no virtual output, Windows before 21H2) is the app's device muted instead, as before —
    /// per-process capture is independent of the device's mute and volume (measured), unlike the session's own.
    /// </summary>
    private void ApplyGuard()
    {
        if (!_muteLocal || _tornDown) return; // never guard a session that is already being torn down
        if (_guard is not null || _redirect is not null || _redirectPending) return; // already guarded
        if (_spec.Kind == CaptureKind.SystemOutput)
        {
            if (_captureDevice is not null) _guard = new LocalOutputGuard(_captureDevice, _fullLevel, _guardStateFile);
            return;
        }
        GuardApplication();
    }

    /// <summary>Application mode: redirect the app, or wait for its audio session, or (this PC can't) mute its device.</summary>
    private void GuardApplication()
    {
        if (_tornDown) return; // also reached from the monitor's retry, which doesn't go through ApplyGuard
        if (TryRedirectApp() || _redirectPending) return; // pending: done as soon as the app opens its audio
        var device = AudioDevices.Resolve(_spec.DeviceId);
        if (AudioDevices.IsSilentOutputName(device.FriendlyName))
        {
            // Muting an output nobody hears protects nothing, and the real one would keep playing: say so instead.
            Note($"silencio local: «{_spec.DisplayName}» está en una salida silenciosa y no hay otra a la que redirigirla; no se silencia nada");
            device.Dispose();
            return;
        }
        _guardDevice = device;
        _guard = new LocalOutputGuard(_guardDevice, fullLevel: false, _guardStateFile);
        Note($"silencio local: salida «{_guardDevice.FriendlyName}» silenciada (no se pudo apartar solo «{_spec.DisplayName}»)");
    }

    /// <summary>
    /// Sends only the selected app to an unheard output. False when this PC can't; <see cref="_redirectPending"/>
    /// when the app has no audio session yet (Windows only accepts a process that has one).
    /// </summary>
    private bool TryRedirectApp()
    {
        _redirectPending = false;
        if (!AppOutputRouting.IsSupported || _spec.ExePath is null) return false;
        try
        {
            var silent = AudioDevices.FindSilentOutput();
            if (silent is null) return false;
            int sessionPid = AudioApps.SessionProcessOf(_spec.ProcessId);
            if (sessionPid == 0) { _redirectPending = true; return false; }
            _redirect = new AppOutputRedirect(sessionPid, _spec.ExePath, silent.Id, _redirectStateFile);
            (_redirectTarget, _redirectAt, _redirectStrikes, _redirectVerified) = (silent.Id, DateTime.UtcNow, 0, false);
            Note($"silencio local: «{_spec.DisplayName}» (PID {sessionPid}) → «{silent.Name}»; el resto del PC no cambia");
            // Windows applies it per executable: another instance (a second profile, the isolated YouTube Music) goes too.
            if (AudioApps.OtherInstancesWithAudio(_spec.ProcessId, _spec.ExePath) is > 0 and var others)
                Note($"silencio local: Windows lo aplica por ejecutable; {others} instancia(s) más de {Path.GetFileName(_spec.ExePath)} tampoco sonará(n) en el PC");
            return true;
        }
        catch (Exception ex) when (AppOutputRedirect.IsRoutingFailure(ex))
        {
            Note($"silencio local: no se pudo apartar «{_spec.DisplayName}» ({ex.GetType().Name} 0x{ex.HResult:X8})");
            return false;
        }
    }

    /// <summary>
    /// Apps that opened a specific output (instead of following Windows' choice) keep playing locally after the
    /// redirect — Chromium follows it, a plain WASAPI app on a fixed device doesn't (measured). If the app is still
    /// playing elsewhere on two checks after a short grace, the redirect is undone and its device is muted (plan B).
    /// </summary>
    private void VerifyRedirect()
    {
        if (_redirect is null || _redirectVerified || DateTime.UtcNow - _redirectAt < TimeSpan.FromSeconds(2)) return;
        try
        {
            var playing = AudioApps.PlayingOn(_spec.ProcessId);
            if (playing.Count == 0) return; // quiet right now: nothing to judge yet
            if (playing.All(id => string.Equals(id, _redirectTarget, StringComparison.OrdinalIgnoreCase)))
            {
                _redirectVerified = true;
                Note("silencio local: la app ya suena solo en la salida silenciosa");
                return;
            }
            if (++_redirectStrikes < 2) return;
            Note($"silencio local: «{_spec.DisplayName}» no cambió de salida en caliente; se deshace y se silencia su salida");
            _redirect.Dispose();
            _redirect = null;
            _guardDevice = AudioDevices.Resolve(_spec.DeviceId);
            _guard = new LocalOutputGuard(_guardDevice, fullLevel: false, _guardStateFile);
        }
        catch (Exception ex) when (AppOutputRedirect.IsRoutingFailure(ex))
        {
            Note($"silencio local: no se pudo comprobar la redirección ({ex.GetType().Name})");
        }
    }

    private void ReleaseGuard()
    {
        _guard?.Dispose(); // restores mute (and volume) before anything else
        _guard = null;
        _guardDevice?.Dispose();
        _guardDevice = null;
        if (_redirect is not null)
        {
            _redirect.Dispose();
            Note(_redirect.Restored ? "silencio local: salida de la app restaurada"
                                    : "silencio local: Windows no aceptó aún la restauración (la app no tiene audio abierto); se reintenta sola");
            _redirect = null;
        }
        _redirectPending = false;
    }

    private void OnPcm(ReadOnlyMemory<byte> pcm)
    {
        long started = SonosFlow.Core.Diagnostics.StreamDiagnostics.Timestamp();
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
        _diag?.Processed(started);
    }

    public Task StopAsync() => Task.Run(StopCoreAsync);

    private async Task StopCoreAsync()
    {
        if (State is SessionState.Stopped) return;
        _cts.Cancel();
        if (_monitor is not null)
        {
            // Whatever ended the monitor, teardown below must run: it restores the PC and frees the port.
            try { await _monitor.ConfigureAwait(false); } catch (Exception) { }
        }
        await _switchLock.WaitAsync().ConfigureAwait(false);
        // Released: the Sonos now plays what the user chose; sending Stop would cut it.
        try { await TearDownAsync(stopSonos: State != SessionState.Released).ConfigureAwait(false); }
        finally { _switchLock.Release(); }
        SetState(SessionState.Stopped);
    }

    private async Task StartPlaybackAsync(CancellationToken ct)
    {
        // Before SetAVTransportURI: the Sonos already connects (and takes the margin) while handling it.
        _sentAtPlaybackStart = Interlocked.Read(ref _server!.BytesSent);
        await _sonos!.SetStreamAsync(_server.StreamUrl, WavStreamServer.MimeType, StreamTitle, ct);
        await _sonos.PlayAsync(ct);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            await MonitorLoopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested) { } // stopping: whatever failed mid-stop is not a session failure
        catch (Exception ex)
        {
            // Never die silently: the app must stop the session, which restores the PC and frees the port.
            _lastError = SessionMessages.With(SessionMessages.InternalError, ex.Message);
            Diagnostic?.Invoke($"monitor detenido por {ex.GetType().Name}: {ex.Message}");
            SetState(SessionState.Failed);
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        long lastSent = Interlocked.Read(ref _server!.BytesSent);
        int tick = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastSentAt = TimeSpan.Zero;
        var watchdog = new PlaybackWatchdog(_options.PrebufferMs);
        var policy = new RecoveryPolicy();
        var lastVerdict = PlaybackVerdict.Healthy;
        TimeSpan? lastStoppedReissue = null, healthySince = null;
        watchdog.Reset(clock.Elapsed);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            long sent = Interlocked.Read(ref _server.BytesSent);
            var sentAt = clock.Elapsed;
            var interval = sentAt - lastSentAt;
            lastSentAt = sentAt;
            var (transport, rel) = await ProbeAsync(ct).ConfigureAwait(false);
            // ESTIMATE (±1 s): audio delivered to the Sonos minus the position it reports.
            double? bufferEst = rel is { } r ? (sent - _sentAtPlaybackStart) / (double)(_streamRate * 4) - r.TotalSeconds : null;

            if (_source?.CaptureError is { } capErr)
            {
                _lastError = SessionMessages.With(SessionMessages.CaptureStopped, capErr.Message);
                Publish(transport, sent, lastSent, bufferEst, interval); // error reaches listeners before the state change
                SetState(SessionState.Failed);
                return;
            }

            // The selected app closed: keep the stream alive (silence) and follow it if it comes back.
            if (_spec.Kind == CaptureKind.Application && ++tick % 3 == 0)
            {
                await FollowApplicationAsync().ConfigureAwait(false);
                if ((_redirectPending || (_redirect is not null && !_redirectVerified)) && await _switchLock.WaitAsync(0).ConfigureAwait(false))
                {
                    try
                    {
                        if (_redirectPending) GuardApplication();
                        else VerifyRedirect();
                    }
                    finally { _switchLock.Release(); }
                }
            }

            var now = clock.Elapsed;
            var verdict = watchdog.Observe(now, transport, _server.ClientCount, _server.SinceLastClientProgress, rel);
            bool healthy = verdict == PlaybackVerdict.Healthy;
            if (verdict != lastVerdict) Note(healthy ? "vigilante: reproducción correcta" : $"vigilante: {verdict} (Sonos {transport}, clientes {_server.ClientCount})");
            lastVerdict = verdict;
            if (healthy && State == SessionState.Recovering) { SetState(SessionState.Streaming); _lastError = null; }
            if (transport != "UNREACHABLE" && _lastError == SpeakerUnreachable) _lastError = null; // a blip must not linger in the UI
            // Two STOPs only mean "the user" when close together: after 30 s playing fine, start counting afresh.
            healthySince = healthy ? healthySince ?? now : null;
            if (healthySince is { } hs && now - hs >= RecoveryPolicy.HealthyReset) lastStoppedReissue = null;

            switch (policy.Observe(now, healthy))
            {
                case RecoveryDecision.Reconnect:
                    // STOPPED with our stream right after we re-issued it for the same reason: someone pressed Stop.
                    bool stoppedAgain = transport == "STOPPED" && lastStoppedReissue is { } at && now - at < TimeSpan.FromSeconds(60);
                    if ((stoppedAgain ? SessionMessages.StoppedFromSonos : await SpeakerTakenElsewhereAsync(transport, ct).ConfigureAwait(false)) is { } why)
                    {
                        // The user chose something else on the Sonos: step aside instead of fighting it.
                        _lastError = why;
                        Note($"fin: {why}");
                        Publish(transport, sent, lastSent, bufferEst, interval);
                        SetState(SessionState.Released);
                        return;
                    }
                    _reconnects++;
                    lastStoppedReissue = transport == "STOPPED" ? now : null;
                    SetState(SessionState.Recovering);
                    Note($"reintento {policy.Attempts}/{RecoveryPolicy.MaxAttempts} ({verdict}, Sonos {transport})");
                    _server.AbortClients(); // a stuck connection must not keep counting as a client
                    try
                    {
                        await StartPlaybackAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException or System.Xml.XmlException && !ct.IsCancellationRequested)
                    {
                        _lastError = SessionMessages.With(SessionMessages.RetryFailed, ex.Message);
                    }
                    watchdog.Reset(clock.Elapsed);
                    _stalls?.RestartWarmup(DateTime.Now - _startedAt);
                    break;

                case RecoveryDecision.GiveUp:
                    _lastError = SessionMessages.With(SessionMessages.GaveUp, policy.Attempts.ToString(CultureInfo.InvariantCulture));
                    Note($"fin: {_lastError}");
                    Publish(transport, sent, lastSent, bufferEst, interval);
                    SetState(SessionState.Failed);
                    return;
            }

            Publish(transport, sent, lastSent, bufferEst, interval);
            lastSent = sent;
        }
    }

    /// <summary>Transport state and position, each with a short timeout so a slow reply can't stall the monitor.</summary>
    private async Task<(string Transport, TimeSpan? Rel)> ProbeAsync(CancellationToken ct)
    {
        string transport;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(ProbeTimeout);
            transport = await _sonos!.GetTransportStateAsync(probe.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SonosSoapException or System.Xml.XmlException && !ct.IsCancellationRequested)
        {
            _lastError = SpeakerUnreachable;
            return ("UNREACHABLE", null);
        }
        if (transport is not ("PLAYING" or "TRANSITIONING")) return (transport, null);
        try
        {
            // Separate from the transport probe: a failed position read must not turn PLAYING into UNREACHABLE.
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(ProbeTimeout);
            return (transport, await _sonos.GetRelTimeAsync(probe.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SonosSoapException or System.Xml.XmlException && !ct.IsCancellationRequested)
        {
            return (transport, null);
        }
    }

    /// <summary>Non-null (the reason) when the Sonos now plays another source or was paused from the Sonos app.</summary>
    private async Task<string?> SpeakerTakenElsewhereAsync(string transport, CancellationToken ct)
    {
        string uri;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(ProbeTimeout);
            uri = await _sonos!.GetCurrentUriAsync(probe.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SonosSoapException or System.Xml.XmlException && !ct.IsCancellationRequested)
        {
            return null; // unknown: unreachable speakers are retried
        }
        if (!PlaybackWatchdog.IsOurStream(uri, _server!.StreamUrl)) return SessionMessages.OtherSource;
        // Sonos never pauses by itself (network trouble shows as STOPPED/TRANSITIONING): this was the user.
        if (transport == "PAUSED_PLAYBACK") return SessionMessages.PausedFromSonos;
        return null;
    }

    private void Note(string message)
    {
        try { _log?.WriteLine($"# {DateTime.Now:HH:mm:ss.fff} {message}"); } catch (IOException) { } catch (ObjectDisposedException) { }
        // The diagnostics file keeps the event but not what the user named («…» holds application names).
        _diag?.Event("nota", message); // sanitised by the diagnostics: no names, paths, addresses or programs reach the file
        Diagnostic?.Invoke(message);
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
        if (again is null) return;
        try
        {
            if (await SwitchSourceAsync(CaptureSpec.Application(again)).ConfigureAwait(false)) _lastError = null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException
                                       or PlatformNotSupportedException or NotSupportedException or System.ComponentModel.Win32Exception
                                       or ArgumentException or UnauthorizedAccessException)
        {
            Note($"no se pudo volver a capturar «{_spec.DisplayName}»: {ex.Message}"); // retried on the next check
        }
    }

    /// <param name="interval">Real time between <paramref name="lastSent"/> and <paramref name="sent"/>: a monitor
    /// tick lasts 1 s plus the speaker's reply time, so dividing by 1 s would overstate the rate.</param>
    private void Publish(string transport, long sent, long lastSent, double? bufferEst, TimeSpan interval)
    {
        double seconds = Math.Max(0.5, interval.TotalSeconds);
        var stats = new StreamStats(State, transport, DateTime.Now - _startedAt, (sent - lastSent) / 1000.0 / seconds, sent, bufferEst,
            Interlocked.Read(ref _server!.DroppedChunks), _reconnects, _server.ClientCount, _source?.SilenceBytes ?? 0, _lastError,
            _spec.DisplayName, _sourceAvailable, _paused,
            _peakSinceLog > 0 ? 20 * Math.Log10(_peakSinceLog) : double.NegativeInfinity,
            _server.TakeMaxBacklogMs());
        _peakSinceLog = 0;
        if (_server.SampleTcp() is { } tcp) _diag?.Tcp(tcp.RttMs, tcp.ReceiveWindowBytes, tcp.RetransBytes, tcp.TimeoutEpisodes);
        _diag?.Tick(stats.BacklogMaxMs, stats.BufferEstimateSeconds, stats.SendRateKBps, _paused);
        _stalls ??= new StallTracker(_streamRate * 4 / 1000.0, _options.PrebufferMs);
        if (_stalls.Observe(stats) is { } shortfall)
        {
            var msg = $"atasco de red: ~{shortfall:0} ms sin entregar (margen {_options.PrebufferMs} ms, cola {stats.BacklogMaxMs:0} ms, {stats.SendRateKBps:0} kB/s)";
            try { _log?.WriteLine($"# {DateTime.Now:HH:mm:ss.fff} {msg}"); } catch (IOException) { } catch (ObjectDisposedException) { }
            Diagnostic?.Invoke(msg);
        }
        stats = stats with { Stalls = _stalls.Count, StallWorstMs = _stalls.WorstMs, StallsOverMargin = _stalls.OverMarginCount };
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
            // Written from the monitor, the server's connection events and Note(): serialize the writes.
            _log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true });
            _log.WriteLine($"# target={_target.RoomName} {_target.ModelName} {_target.Address}; capture={CaptureDeviceName}; {SourceFormat}; margen={_options.PrebufferMs} ms");
            _log.WriteLine("elapsed_s,state,transport,clients,send_rate_kBps,bytes_sent,silence_kB,dropped_chunks,reconnects,buffer_est_s,error,source,paused,peak_dbfs,backlog_ms,capture_gaps,private_memory_mb,working_set_mb,process_cpu_pct");
        }
        catch (IOException) { _log = null; }
    }

    private static TimeSpan ReadCpuTime()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private void WriteLog(StreamStats s)
    {
        if (_log is null) return;
        var inv = CultureInfo.InvariantCulture;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_lastCpuSample, now).TotalSeconds;
            double cpuPercent = Math.Clamp((cpu - _lastCpuTime).TotalSeconds / Math.Max(0.001, seconds)
                / Environment.ProcessorCount * 100, 0, 100);
            _lastCpuTime = cpu;
            _lastCpuSample = now;
            _log.WriteLine(string.Join(',',
                s.Elapsed.TotalSeconds.ToString("F0", inv), s.State, s.TransportState, s.Clients,
                s.SendRateKBps.ToString("F1", inv), s.BytesSent, (s.SilenceBytes / 1000).ToString(inv),
                s.DroppedChunks, s.Reconnects, s.BufferEstimateSeconds?.ToString("F1", inv) ?? "",
                s.LastError?.Replace(',', ';') ?? "", s.SourceName.Replace(',', ';'), s.Paused ? 1 : 0,
                double.IsNegativeInfinity(s.PeakDbfs) ? "" : s.PeakDbfs.ToString("F1", inv),
                s.BacklogMaxMs.ToString("F0", inv), _source?.CaptureDiscontinuities ?? 0,
                (process.PrivateMemorySize64 / 1048576.0).ToString("F1", inv),
                (process.WorkingSet64 / 1048576.0).ToString("F1", inv), cpuPercent.ToString("F2", inv)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async Task TearDownAsync(bool stopSonos)
    {
        _tornDown = true; // from here on nothing may guard the PC again (see SetMuteLocalAsync)
        if (stopSonos && _sonos is not null)
        {
            try { await _sonos.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException) { }
        }
        // Restore the PC speakers first; then each step runs even if an earlier one throws, so the capture,
        // the port and the log are always released.
        Step(ReleaseGuard);
        Step(() =>
        {
            lock (_sourceLock)
            {
                var source = _source;
                _source = null;
                source?.Dispose();
            }
        });
        Step(() => { var s = _server; _server = null; s?.Dispose(); });
        Step(() => { var c = _sonos; _sonos = null; c?.Dispose(); });
        Step(() => { var d = _captureDevice; _captureDevice = null; d?.Dispose(); });
        Step(() =>
        {
            var d = _diag;
            if (d is null) return;
            _diag = null;
            try
            {
                var summary = d.Finish();
                try { _log?.WriteLine($"# {DateTime.Now:HH:mm:ss.fff} diagnóstico: {summary}"); } catch (IOException) { } catch (ObjectDisposedException) { }
                Diagnostic?.Invoke($"diagnóstico: {summary}");
                if (d.FilePath is not null) Diagnostic?.Invoke($"diagnóstico detallado: {Path.GetFileName(d.FilePath)}");
            }
            finally { d.Dispose(); }
        });
        Step(() => { var l = _log; _log = null; l?.Dispose(); });

        void Step(Action step)
        {
            try { step(); }
            catch (Exception ex) { Diagnostic?.Invoke($"limpieza: {ex.GetType().Name}: {ex.Message}"); }
        }
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
