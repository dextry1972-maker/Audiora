namespace SonosFlow.Core.Streaming;

/// <summary>
/// Turns the per-second stats into "atascos": seconds in which the network or the Sonos stopped taking the
/// audio on time (audio piling up in the server, or far less than one second's worth delivered). Consecutive
/// bad seconds are one episode. Windows' TCP buffers absorb the first part of a stall, so short ones go unseen:
/// this is a lower bound, not a cut detector.
/// </summary>
public sealed class StallTracker(double expectedKBps, int marginMs)
{
    public const double BacklogThresholdMs = 300;
    public const double LowRateFraction = 0.6;
    private const double WarmupSeconds = 3; // connection start: header, margin burst, Sonos ramp-up

    private bool _inEpisode;
    private double _episodeWorst;
    private double _warmupEnd = WarmupSeconds;

    /// <summary>After the stream is re-issued: the reconnect and its margin burst are not network stalls.</summary>
    public void RestartWarmup(TimeSpan elapsed)
    {
        _warmupEnd = elapsed.TotalSeconds + WarmupSeconds;
        _inEpisode = false;
    }

    public int Count { get; private set; }
    public double WorstMs { get; private set; }
    /// <summary>Episodes whose shortfall exceeded the margin, so the Sonos probably ran dry (audible).</summary>
    public int OverMarginCount { get; private set; }
    public int MarginMs { get; } = marginMs;

    /// <summary>Feeds one second of stats; returns the shortfall (ms) when a new episode starts, else null.</summary>
    public double? Observe(StreamStats s)
    {
        double shortfall = 0;
        if (s.Elapsed.TotalSeconds > _warmupEnd && s.Clients > 0)
        {
            double rateDeficit = expectedKBps > 0 ? Math.Max(0, 1 - s.SendRateKBps / expectedKBps) * 1000 : 0;
            if (s.BacklogMaxMs > BacklogThresholdMs) shortfall = s.BacklogMaxMs;
            if (s.SendRateKBps < expectedKBps * LowRateFraction) shortfall = Math.Max(shortfall, rateDeficit);
        }

        if (shortfall <= 0)
        {
            _inEpisode = false;
            return null;
        }

        bool started = !_inEpisode;
        if (started)
        {
            _inEpisode = true;
            _episodeWorst = 0;
            Count++;
        }
        bool wasOver = _episodeWorst > MarginMs;
        _episodeWorst = Math.Max(_episodeWorst, shortfall);
        if (!wasOver && _episodeWorst > MarginMs) OverMarginCount++;
        WorstMs = Math.Max(WorstMs, shortfall);
        return started ? shortfall : null;
    }
}
