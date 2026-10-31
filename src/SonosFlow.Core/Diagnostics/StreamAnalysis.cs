namespace SonosFlow.Core.Diagnostics;

/// <summary>Result of <see cref="PeriodicityAnalyzer.Analyze"/>.</summary>
/// <param name="Enough">Enough episodes to say anything at all (otherwise every other field is meaningless).</param>
/// <param name="Periodic">A single interval accounts for most of the gaps between episodes.</param>
/// <param name="Near5To6Min">That interval is between 270 and 400 s.</param>
public sealed record PeriodicityResult(int Episodes, bool Enough, bool Periodic, double MedianIntervalSec, double Share, bool Near5To6Min)
{
    public override string ToString() => !Enough
        ? $"{Episodes} episodios: insuficientes para hablar de periodicidad"
        : Periodic
            ? $"{Episodes} episodios, periódicos cada ≈{MedianIntervalSec:0} s ({Share:P0} de los intervalos){(Near5To6Min ? " — en el rango de 5–6 min" : "")}"
            : $"{Episodes} episodios sin periodicidad (intervalo mediano {MedianIntervalSec:0} s, {Share:P0} coherente)";
}

/// <summary>
/// Are the interruptions regular? Takes the start time (monotonic seconds) of each episode. Regular means: at least
/// four episodes and a single interval that explains most of the gaps between them, within a tolerance. It never claims
/// a period from fewer episodes than that — chance produces "patterns" in three points.
/// </summary>
public static class PeriodicityAnalyzer
{
    public const int MinEpisodes = 4;

    public static PeriodicityResult Analyze(IReadOnlyList<double> episodeStartsSec, double toleranceFraction = 0.15, double minToleranceSec = 10)
    {
        var starts = episodeStartsSec.OrderBy(t => t).ToList();
        if (starts.Count < MinEpisodes) return new(starts.Count, false, false, 0, 0, false);

        var intervals = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
        var sorted = intervals.OrderBy(x => x).ToList();
        double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        double tolerance = Math.Max(minToleranceSec, median * toleranceFraction);
        double share = intervals.Count(x => Math.Abs(x - median) <= tolerance) / (double)intervals.Count;
        bool periodic = share >= 0.6 && median >= 20;
        return new(starts.Count, true, periodic, median, share, periodic && median is >= 270 and <= 400);
    }
}

/// <summary>
/// Groups consecutive anomalous seconds into episodes: an anomaly less than <paramref name="mergeGapSec"/> after the
/// previous one belongs to the same episode.
/// </summary>
public sealed class EpisodeTracker(double mergeGapSec = 8)
{
    private double _lastAnomaly = double.NegativeInfinity;
    public List<double> Starts { get; } = [];

    /// <returns>True when this anomaly opened a new episode.</returns>
    public bool Anomaly(double tSec)
    {
        bool isNew = tSec - _lastAnomaly > mergeGapSec;
        if (isNew) Starts.Add(tSec);
        _lastAnomaly = tSec;
        return isNew;
    }
}

/// <summary>
/// Drift of the capture clock against the monotonic clock: cumulative captured frames over elapsed time, only over
/// stretches where audio was being delivered continuously (a loopback that has nothing to play delivers nothing, which
/// is not drift). The result carries its own uncertainty: one capture block of timing error spread over the window.
/// </summary>
public sealed class DriftEstimator(int sampleRate = 48000, double blockMs = 50)
{
    private double _runStart = double.NaN, _lastT = double.NaN;
    private long _runFrames;
    private double _totalSec; private long _totalFrames;

    /// <summary>One sample per second: <paramref name="frames"/> captured this second, and whether it was a clean second
    /// (capture delivered, no pacer filling, not paused).</summary>
    public void AddSecond(double tSec, long frames, bool continuous)
    {
        if (!continuous) { Close(); return; }
        if (double.IsNaN(_runStart)) { _runStart = tSec - 1; _runFrames = 0; }
        _runFrames += frames;
        _lastT = tSec;
    }

    private void Close()
    {
        if (!double.IsNaN(_runStart) && !double.IsNaN(_lastT) && _lastT - _runStart >= 30)
        {
            _totalSec += _lastT - _runStart;
            _totalFrames += _runFrames;
        }
        _runStart = _lastT = double.NaN; _runFrames = 0;
    }

    /// <summary>Seconds of continuous capture accumulated so far (including the run in progress).</summary>
    public double ObservedSec => _totalSec + (double.IsNaN(_runStart) ? 0 : _lastT - _runStart);

    /// <summary>Capture clock minus monotonic clock, in parts per million (positive = the device runs fast); null until
    /// at least 120 s of continuous capture were seen.</summary>
    public (double Ppm, double UncertaintyPpm)? Estimate()
    {
        double sec = ObservedSec;
        if (sec < 120) return null;
        long frames = _totalFrames + (double.IsNaN(_runStart) ? 0 : _runFrames);
        double ppm = (frames / (sec * sampleRate) - 1) * 1e6;
        double uncertainty = blockMs / 1000.0 / sec * 1e6; // one block of boundary error over the whole window
        return (ppm, uncertainty);
    }
}
