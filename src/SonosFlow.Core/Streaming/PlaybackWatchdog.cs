namespace SonosFlow.Core.Streaming;

public enum PlaybackVerdict
{
    Healthy,
    /// <summary>Not PLAYING/TRANSITIONING, nobody connected, or stuck in TRANSITIONING.</summary>
    NotPlaying,
    /// <summary>Connected but no player has taken audio for longer than the margin can cover.</summary>
    NotConsuming,
    /// <summary>Says PLAYING and takes audio, but its playback position stopped advancing.</summary>
    Frozen,
}

/// <summary>
/// Decides whether the Sonos is really playing our stream. Silence is never a failure: silence frames are
/// always sent, so a healthy player keeps taking bytes and its position keeps advancing during silence.
/// Pure logic with an injected monotonic clock, so it is unit-tested without a speaker.
/// </summary>
public sealed class PlaybackWatchdog(int marginMs)
{
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FrozenAfter = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan TransitioningLimit = TimeSpan.FromSeconds(15);
    /// <summary>How long the control channel (SOAP) may fail while audio keeps flowing before it counts.</summary>
    public static readonly TimeSpan UnreachableTolerance = TimeSpan.FromSeconds(30);

    public TimeSpan NotConsumingAfter { get; } = TimeSpan.FromMilliseconds(Math.Max(5000, marginMs + 3000));

    private TimeSpan _connectedAt;
    private TimeSpan? _transitioningSince;
    private TimeSpan? _unreachableSince;
    private TimeSpan? _relTime;
    private TimeSpan _relChangedAt;
    private int _relAdvances;

    /// <summary>Call after (re)issuing the stream: the player reconnects and ramps up again.</summary>
    public void Reset(TimeSpan now)
    {
        _connectedAt = now;
        _transitioningSince = null;
        _relTime = null;
        _relAdvances = 0;
    }

    /// <param name="sinceClientProgress">Time since any connected player last took audio.</param>
    /// <param name="relTime">Position the player reports (1 s resolution); null when unknown.</param>
    public PlaybackVerdict Observe(TimeSpan now, string transport, int clients, TimeSpan sinceClientProgress, TimeSpan? relTime)
    {
        TrackPosition(now, relTime);

        if (transport == "TRANSITIONING") _transitioningSince ??= now;
        else _transitioningSince = null;

        if (transport == "UNREACHABLE")
        {
            // A control-plane hiccup is not a playback failure: while the player keeps taking audio, leave the
            // data connection alone (re-issuing would itself cause the cut we try to avoid).
            _unreachableSince ??= now;
            bool audioFlowing = clients > 0 && sinceClientProgress < NotConsumingAfter;
            return audioFlowing && now - _unreachableSince < UnreachableTolerance ? PlaybackVerdict.Healthy : PlaybackVerdict.NotPlaying;
        }
        _unreachableSince = null;

        if (transport is not ("PLAYING" or "TRANSITIONING") || clients == 0) return PlaybackVerdict.NotPlaying;
        if (now - _transitioningSince > TransitioningLimit) return PlaybackVerdict.NotPlaying;
        if (now - _connectedAt < Warmup) return PlaybackVerdict.Healthy;
        if (sinceClientProgress >= NotConsumingAfter) return PlaybackVerdict.NotConsuming;
        // Armed only after the position was seen advancing twice: some firmware may not report it at all.
        if (transport == "PLAYING" && _relAdvances >= 2 && now - _relChangedAt >= FrozenAfter) return PlaybackVerdict.Frozen;
        return PlaybackVerdict.Healthy;
    }

    private void TrackPosition(TimeSpan now, TimeSpan? relTime)
    {
        if (relTime is not { } rel)
        {
            _relChangedAt = now; // unknown: neither progress nor a freeze (failed reads must not add up to "frozen")
            return;
        }
        if (_relTime is not { } last || rel < last)
        {
            _relTime = rel; // first value, or the player restarted the position: new baseline
            _relChangedAt = now;
            _relAdvances = 0;
        }
        else if (rel > last)
        {
            _relTime = rel;
            _relChangedAt = now;
            _relAdvances++;
        }
    }

    /// <summary>Whether the player's current URI is our stream (anything else means the user chose another source).</summary>
    public static bool IsOurStream(string currentUri, string streamUrl) =>
        currentUri.Length == 0 || currentUri.Contains(new Uri(streamUrl).Authority + new Uri(streamUrl).AbsolutePath, StringComparison.OrdinalIgnoreCase);
}

public enum RecoveryDecision { None, Reconnect, GiveUp }

/// <summary>
/// Bounded re-issue of the stream while the player is unhealthy: waits 5 s before the first attempt, then
/// backs off (5, 10, 20, 40, 60 s), and gives up after <see cref="MaxAttempts"/> attempts or
/// <see cref="GiveUpAfter"/> without recovering. 30 s healthy resets the count.
/// </summary>
public sealed class RecoveryPolicy
{
    public const int MaxAttempts = 8;
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan HealthyReset = TimeSpan.FromSeconds(30);
    private static readonly int[] BackoffSeconds = [5, 10, 20, 40, 60];

    private TimeSpan? _unhealthySince, _healthySince;
    private TimeSpan _nextAttemptAt;

    public int Attempts { get; private set; }

    public RecoveryDecision Observe(TimeSpan now, bool healthy)
    {
        if (healthy)
        {
            _unhealthySince = null;
            _healthySince ??= now;
            if (now - _healthySince >= HealthyReset) Attempts = 0;
            return RecoveryDecision.None;
        }

        _healthySince = null;
        _unhealthySince ??= now;
        if (now - _unhealthySince < Debounce) return RecoveryDecision.None;
        if (now - _unhealthySince >= GiveUpAfter) return RecoveryDecision.GiveUp;
        if (Attempts > 0 && now < _nextAttemptAt) return RecoveryDecision.None; // every attempt, the last one too, gets its wait
        if (Attempts >= MaxAttempts) return RecoveryDecision.GiveUp;

        _nextAttemptAt = now + TimeSpan.FromSeconds(BackoffSeconds[Math.Min(Attempts, BackoffSeconds.Length - 1)]);
        Attempts++;
        return RecoveryDecision.Reconnect;
    }
}
