using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SonosFlow.Core.Diagnostics;

/// <summary>One second of the stream as seen by the diagnostics. Every time is monotonic (Stopwatch), never wall time.</summary>
public sealed record DiagSecond(
    double TSec,
    long CaptureBlocks, double CaptureGapMaxMs, double CaptureGapAfterAudioMaxMs, long CaptureFrames,
    long FillBytes, long SuspiciousFillBytes,
    long ProcessCount, double ProcessMaxMs,
    long Writes, double WriteMaxMs, long WriteStalls,
    long DroppedChunks, long DroppedBytes,
    double BacklogMs, double? BufferEstS, double SendRateKBps,
    string[] Reasons)
{
    public bool Anomaly => Reasons.Length > 0;
}

public sealed record DiagEvent(double TSec, string Kind, string Detail);

/// <summary>
/// Lightweight, local diagnostics of one streaming session. Recording calls are a few interlocked operations (no locks,
/// no allocation on the audio path); once a second <see cref="Tick"/> turns them into a <see cref="DiagSecond"/>,
/// finds anomalies, groups them into episodes and checks whether those episodes are periodic.
/// What goes to disk (<c>diag-*.jsonl</c>): events and ten-second summaries only. Never audio, never addresses,
/// never application names; capped in size and in number of files.
/// </summary>
public sealed class StreamDiagnostics : IDisposable
{
    public const int FormatVersion = 1;
    private const long StallMs = 250;
    private const double CaptureGapAnomalyMs = 250, SlowProcessMs = 30, BacklogAnomalyMs = 400;
    private const int RecentEventsKept = 512, MaxFiles = 30, SummarySeconds = 10;
    private static readonly long Freq = Stopwatch.Frequency;

    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly object _fileLock = new();
    private readonly Queue<DiagEvent> _recent = new();
    private readonly long _maxFileBytes;
    private StreamWriter? _file;
    private long _fileBytes;
    private bool _truncated;

    // capture
    private long _lastCaptureTs, _lastAudibleTs, _captureBlocks, _captureFrames;
    private readonly IntervalStats _captureGap = new();
    private long _gapAfterAudioMaxTicks;
    // pacer fill
    private long _fillBytes, _suspiciousFillBytes;
    // processing
    private readonly IntervalStats _process = new();
    // server
    private readonly IntervalStats _write = new();
    private long _writeStalls, _droppedChunks, _droppedBytes;

    private readonly EpisodeTracker _episodes = new();
    private readonly DriftEstimator _drift;
    private readonly SecondAggregate _window = new();
    private double _lastTickSec;
    private int _secondsSinceSummary;

    public string? FilePath { get; }
    public IReadOnlyList<double> EpisodeStarts => _episodes.Starts;
    public PeriodicityResult Periodicity => PeriodicityAnalyzer.Analyze(_episodes.Starts);
    public (double Ppm, double UncertaintyPpm)? Drift => _drift.Estimate();
    public bool FileDisabled { get; private set; }
    public bool Truncated => _truncated;

    /// <param name="directory">Where <c>diag-*.jsonl</c> goes; null = in memory only.</param>
    /// <param name="sampleRate">Output sample rate (frames per second), for the drift estimate.</param>
    /// <param name="mode">"system" or "app": the kind of capture, nothing that identifies an application.</param>
    public StreamDiagnostics(string? directory, int sampleRate, string mode, long maxFileBytes = 1 << 20)
    {
        _maxFileBytes = maxFileBytes;
        _drift = new DriftEstimator(sampleRate);
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            PruneOldFiles(directory);
            FilePath = Path.Combine(directory, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
            _file = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileDisabled = true;
            _file = null;
        }
        Event("inicio", $"formato={FormatVersion} reloj=Stopwatch({Freq} Hz) modo={mode} muestreo={sampleRate} inicio_local={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    public static long Timestamp() => Stopwatch.GetTimestamp();
    public static double ToMs(long ticks) => ticks * 1000.0 / Freq;
    private double Sec(long ts) => (ts - _origin) / (double)Freq;
    public double NowSec => Sec(Stopwatch.GetTimestamp());

    // ── recording (audio / server threads) ────────────────────────────────────

    /// <summary>A block arrived from Windows. <paramref name="audible"/>: its peak is above the noise floor.</summary>
    public void CaptureBlock(int frames, bool audible)
    {
        long now = Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _lastCaptureTs, now);
        if (prev != 0)
        {
            long gap = now - prev;
            _captureGap.Add(gap);
            // A gap right after audio is a capture problem; a gap after silence is just a loopback with nothing to play.
            long audibleAt = Volatile.Read(ref _lastAudibleTs);
            if (audibleAt != 0 && prev - audibleAt < Freq / 2) MaxTicks(ref _gapAfterAudioMaxTicks, gap);
        }
        Interlocked.Increment(ref _captureBlocks);
        Interlocked.Add(ref _captureFrames, frames);
        if (audible) Volatile.Write(ref _lastAudibleTs, now);
    }

    /// <summary>The pacer had to send zeros because capture delivered nothing. Suspicious if audio was flowing just before.</summary>
    public void PacerFill(int bytes)
    {
        long now = Stopwatch.GetTimestamp();
        long audibleAt = Volatile.Read(ref _lastAudibleTs);
        Interlocked.Add(ref _fillBytes, bytes);
        if (audibleAt != 0 && now - audibleAt < 2 * Freq) Interlocked.Add(ref _suspiciousFillBytes, bytes);
    }

    /// <summary>Time one block took through the processing chain (server write, spectrum, peak).</summary>
    public void Processed(long startTimestamp) => _process.Add(Stopwatch.GetTimestamp() - startTimestamp);

    /// <summary>One socket write to a player completed after <paramref name="ticks"/>.</summary>
    public void ServerWrite(long ticks)
    {
        _write.Add(ticks);
        if (ToMs(ticks) >= StallMs) Interlocked.Increment(ref _writeStalls);
    }

    public void Dropped(int bytes)
    {
        Interlocked.Increment(ref _droppedChunks);
        Interlocked.Add(ref _droppedBytes, bytes);
    }

    /// <summary>A discrete event (connection opened/closed, recovery, ...). Written to disk while the cap allows.</summary>
    public void Event(string kind, string detail)
    {
        var e = new DiagEvent(NowSec, kind, detail);
        lock (_fileLock)
        {
            _recent.Enqueue(e);
            while (_recent.Count > RecentEventsKept) _recent.Dequeue();
            WriteLine(new Dictionary<string, object?> { ["t"] = Math.Round(e.TSec, 3), ["k"] = kind, ["d"] = detail });
        }
    }

    public IReadOnlyList<DiagEvent> RecentEvents() { lock (_fileLock) return [.. _recent]; }

    // ── once a second ─────────────────────────────────────────────────────────

    /// <param name="paused">The session is sending silence on purpose: not a capture problem, and no drift sample.</param>
    public DiagSecond Tick(double backlogMs, double? bufferEstS, double sendRateKBps, bool paused = false)
    {
        double t = NowSec;
        var gap = _captureGap.Take();
        var proc = _process.Take();
        var write = _write.Take();
        long frames = Interlocked.Exchange(ref _captureFrames, 0);
        long blocks = Interlocked.Exchange(ref _captureBlocks, 0);
        long fill = Interlocked.Exchange(ref _fillBytes, 0), suspicious = Interlocked.Exchange(ref _suspiciousFillBytes, 0);
        long stalls = Interlocked.Exchange(ref _writeStalls, 0);
        long dropped = Interlocked.Exchange(ref _droppedChunks, 0), droppedBytes = Interlocked.Exchange(ref _droppedBytes, 0);
        double gapAfterAudio = ToMs(Interlocked.Exchange(ref _gapAfterAudioMaxTicks, 0));

        var reasons = new List<string>();
        if (!paused)
        {
            if (gapAfterAudio > CaptureGapAnomalyMs) reasons.Add($"hueco_captura {gapAfterAudio:0}ms");
            if (suspicious > 0) reasons.Add($"relleno_sospechoso {suspicious * 1000L / (48000L * 4):0}ms");
        }
        if (write.MaxMs >= StallMs || stalls > 0) reasons.Add($"escritura_lenta {write.MaxMs:0}ms");
        if (backlogMs > BacklogAnomalyMs) reasons.Add($"cola {backlogMs:0}ms");
        if (dropped > 0) reasons.Add($"descartes {dropped}");
        if (proc.MaxMs > SlowProcessMs) reasons.Add($"proceso_lento {proc.MaxMs:0}ms");

        var s = new DiagSecond(t, blocks, gap.MaxMs, gapAfterAudio, frames, fill, suspicious, proc.Count, proc.MaxMs,
            write.Count, write.MaxMs, stalls, dropped, droppedBytes, backlogMs, bufferEstS, sendRateKBps, [.. reasons]);

        _drift.AddSecond(t, frames, continuous: !paused && blocks >= 10 && fill == 0 && t - _lastTickSec < 1.6);
        _lastTickSec = t;

        if (s.Anomaly)
        {
            bool opened = _episodes.Anomaly(t);
            Event(opened ? "episodio" : "anomalia", string.Join("; ", reasons));
            if (opened && Periodicity.Enough) Event("periodicidad", Periodicity.ToString());
        }

        _window.Add(s);
        if (++_secondsSinceSummary >= SummarySeconds)
        {
            _secondsSinceSummary = 0;
            lock (_fileLock) WriteLine(_window.ToLine(t));
            _window.Reset();
        }
        lock (_fileLock) _file?.Flush();
        return s;
    }

    /// <summary>Closing line: totals, the periodicity verdict and the drift estimate.</summary>
    public string Finish()
    {
        var drift = Drift;
        string text = $"{Periodicity}; deriva de captura: " +
            (drift is { } d ? $"{d.Ppm:+0;-0} ppm (±{d.UncertaintyPpm:0})" : "sin datos suficientes (se necesitan ≥120 s de captura continua)");
        Event("resumen", text);
        return text;
    }

    // ── file ──────────────────────────────────────────────────────────────────

    private void WriteLine(object line)
    {
        if (_file is null || _truncated) return;
        try
        {
            string json = JsonSerializer.Serialize(line);
            int bytes = Encoding.UTF8.GetByteCount(json) + 1;
            if (_fileBytes + bytes > _maxFileBytes)
            {
                _truncated = true;
                _file.WriteLine("{\"k\":\"truncado\",\"d\":\"límite de tamaño del diagnóstico alcanzado\"}");
                _file.Flush();
                return;
            }
            _file.WriteLine(json);
            _fileBytes += bytes;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            FileDisabled = true; // diagnostics must never take the session down
            _file = null;
        }
    }

    private static void PruneOldFiles(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("diag-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var old in files.Skip(MaxFiles - 1))
        {
            try { old.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void MaxTicks(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    public void Dispose()
    {
        lock (_fileLock)
        {
            try { _file?.Dispose(); } catch (IOException) { }
            _file = null;
        }
    }

    /// <summary>Ten seconds of data in one compact line.</summary>
    private sealed class SecondAggregate
    {
        private int _n, _anomalies;
        private long _blocks, _fill, _suspicious, _writes, _stalls, _dropped, _droppedBytes;
        private double _gapMax, _gapAudioMax, _procMax, _writeMax, _backlogMax;
        private double? _bufMin; private double _rateSum;

        public void Add(DiagSecond s)
        {
            _n++; if (s.Anomaly) _anomalies++;
            _blocks += s.CaptureBlocks; _fill += s.FillBytes; _suspicious += s.SuspiciousFillBytes;
            _writes += s.Writes; _stalls += s.WriteStalls; _dropped += s.DroppedChunks; _droppedBytes += s.DroppedBytes;
            _gapMax = Math.Max(_gapMax, s.CaptureGapMaxMs); _gapAudioMax = Math.Max(_gapAudioMax, s.CaptureGapAfterAudioMaxMs);
            _procMax = Math.Max(_procMax, s.ProcessMaxMs); _writeMax = Math.Max(_writeMax, s.WriteMaxMs);
            _backlogMax = Math.Max(_backlogMax, s.BacklogMs);
            if (s.BufferEstS is { } b) _bufMin = _bufMin is null ? b : Math.Min(_bufMin.Value, b);
            _rateSum += s.SendRateKBps;
        }

        public object ToLine(double t) => new Dictionary<string, object?>
        {
            ["t"] = Math.Round(t, 3), ["k"] = "resumen10s",
            ["d"] = new Dictionary<string, object?>
            {
                ["s"] = _n, ["anomalias"] = _anomalies, ["bloques"] = _blocks,
                ["hueco_captura_max_ms"] = Math.Round(_gapMax, 1), ["hueco_tras_audio_max_ms"] = Math.Round(_gapAudioMax, 1),
                ["relleno_kB"] = _fill / 1000, ["relleno_sospechoso_kB"] = _suspicious / 1000,
                ["proceso_max_ms"] = Math.Round(_procMax, 2),
                ["escrituras"] = _writes, ["escritura_max_ms"] = Math.Round(_writeMax, 1), ["escrituras_lentas"] = _stalls,
                ["descartes"] = _dropped, ["descartes_kB"] = _droppedBytes / 1000,
                ["cola_max_ms"] = Math.Round(_backlogMax, 0), ["buffer_est_min_s"] = _bufMin is null ? null : Math.Round(_bufMin.Value, 2),
                ["ritmo_medio_kBps"] = _n == 0 ? 0 : Math.Round(_rateSum / _n, 1),
            },
        };

        public void Reset() { _n = _anomalies = 0; _blocks = _fill = _suspicious = _writes = _stalls = _dropped = _droppedBytes = 0; _gapMax = _gapAudioMax = _procMax = _writeMax = _backlogMax = 0; _bufMin = null; _rateSum = 0; }
    }
}

/// <summary>Count, mean, maximum and a coarse histogram of durations; recorded with interlocked operations only.</summary>
public sealed class IntervalStats
{
    public static readonly double[] BucketUpperMs = [5, 10, 20, 40, 80, 160, 320, 640];
    private long _count, _sumTicks, _maxTicks;
    private readonly long[] _histogram = new long[BucketUpperMs.Length + 1];

    public void Add(long ticks)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sumTicks, ticks);
        long current;
        while (ticks > (current = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, ticks, current) != current) { }
        double ms = StreamDiagnostics.ToMs(ticks);
        int bucket = 0;
        while (bucket < BucketUpperMs.Length && ms > BucketUpperMs[bucket]) bucket++;
        Interlocked.Increment(ref _histogram[bucket]);
    }

    public IntervalSnapshot Take()
    {
        long count = Interlocked.Exchange(ref _count, 0), sum = Interlocked.Exchange(ref _sumTicks, 0), max = Interlocked.Exchange(ref _maxTicks, 0);
        var hist = new long[_histogram.Length];
        for (int i = 0; i < hist.Length; i++) hist[i] = Interlocked.Exchange(ref _histogram[i], 0);
        return new IntervalSnapshot(count, count == 0 ? 0 : StreamDiagnostics.ToMs(sum) / count, StreamDiagnostics.ToMs(max), hist);
    }
}

public readonly record struct IntervalSnapshot(long Count, double MeanMs, double MaxMs, long[] Histogram);
