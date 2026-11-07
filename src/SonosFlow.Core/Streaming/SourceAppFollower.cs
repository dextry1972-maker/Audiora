using System.Diagnostics;

namespace SonosFlow.Core.Streaming;

/// <summary>What one check of the selected application found.</summary>
public enum FollowEvent
{
    /// <summary>Nothing to do: the app is running (playing, paused or silent alike), or it is still closed.</summary>
    None,
    /// <summary>The app's process ended. Reported once; the session stays up and waits for it.</summary>
    SourceClosed,
    /// <summary>A new instance (another process) was picked up: capture and connection were re-created.</summary>
    Recovered,
    /// <summary>A new instance was found but re-attaching failed; the next try waits (<see cref="FollowStep.NextTryIn"/>).</summary>
    RecoveryFailed,
    /// <summary>Re-attaching to that instance failed too often: it is left alone until another instance appears.</summary>
    GaveUpOnInstance,
}

/// <param name="Attempt">Attempt number for this new instance (1 = first).</param>
/// <param name="Elapsed">Time the re-attachment took (capture + reconnection), measured around it.</param>
public sealed record FollowStep(FollowEvent Event, int OldPid = 0, int NewPid = 0, int Attempt = 0,
    TimeSpan? Elapsed = null, TimeSpan? NextTryIn = null, string? Error = null);

/// <summary>Queries the follower needs; the session passes the Windows ones, tests a fake.</summary>
public interface ISourceAppProbe
{
    /// <summary>The process is still alive.</summary>
    bool IsRunning(int pid);

    /// <summary>Cheap: is any process of this executable running at all (no audio sessions looked at)?</summary>
    bool AnyProcessOf(string exePath);

    /// <summary>Root process of a running instance of this executable that already has an audio session, or null.</summary>
    int? FindInstanceWithAudio(string exePath, bool isolatedYouTubeMusic);
}

/// <summary>
/// «Una aplicación»: follows the selected app across restarts. The signal is the app's PROCESS, never its audio: while the
/// process lives (playing, paused, silent, briefly interrupted) nothing is touched. When it ends, the session stays up
/// (silence keeps the Sonos connected) and waits, looking only with a cheap process check — no capture attempt, no retry —
/// until a process of the same executable exists again and has opened its audio. Then it re-attaches once, through the
/// callback; a failure backs off (3, 6, 12, 24, 30 s) and gives up on that instance after
/// <see cref="MaxAttemptsPerInstance"/> tries (another instance starts afresh). Pure: no clock of its own, no Windows calls.
/// </summary>
public sealed class SourceAppFollower
{
    public const int MaxAttemptsPerInstance = 5;
    private static readonly int[] BackoffSeconds = [3, 6, 12, 24, 30];

    private readonly ISourceAppProbe _probe;
    private readonly string? _exePath;
    private readonly bool _isolated;
    private TimeSpan? _closedSince;
    private TimeSpan _nextTryAt;
    private int _attemptPid, _attempts, _gaveUpPid;

    public SourceAppFollower(int processId, string? exePath, bool isolatedYouTubeMusic, ISourceAppProbe probe)
    {
        CurrentPid = processId;
        _exePath = exePath;
        _isolated = isolatedYouTubeMusic;
        _probe = probe;
    }

    /// <summary>Process currently captured.</summary>
    public int CurrentPid { get; private set; }

    /// <summary>False from the moment the app's process ended until a new instance is attached.</summary>
    public bool SourceAvailable => _closedSince is null;

    /// <param name="now">Time of the check (monotonic).</param>
    /// <param name="reattach">Re-creates the capture (and whatever else must follow it) for the new process; gets the old
    /// and the new process id. True when it worked; an exception counts as a failure.</param>
    /// <param name="ct">The session's token: when it is cancelled the attempt is abandoned, not counted.</param>
    public async Task<FollowStep> CheckAsync(TimeSpan now, Func<int, int, Task<bool>> reattach, CancellationToken ct = default)
    {
        if (_probe.IsRunning(CurrentPid)) return new FollowStep(FollowEvent.None);

        if (_closedSince is null)
        {
            _closedSince = now;
            return new FollowStep(FollowEvent.SourceClosed, CurrentPid);
        }
        if (_exePath is null || now < _nextTryAt) return new FollowStep(FollowEvent.None);
        if (!_probe.AnyProcessOf(_exePath)) return new FollowStep(FollowEvent.None);              // still closed
        if (_probe.FindInstanceWithAudio(_exePath, _isolated) is not { } pid || pid == CurrentPid || pid == _gaveUpPid)
            return new FollowStep(FollowEvent.None);                                                  // opened, no audio yet

        if (pid != _attemptPid) (_attemptPid, _attempts) = (pid, 0);
        _attempts++;
        int old = CurrentPid;
        var clock = Stopwatch.StartNew();
        bool ok;
        string? error = null;
        try { ok = await reattach(old, pid).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // stopping: not a failed attempt (a timeout is)
        catch (Exception ex) { ok = false; error = $"{ex.GetType().Name}: {ex.Message}"; }
        var elapsed = clock.Elapsed;

        if (ok)
        {
            int attempt = _attempts;
            CurrentPid = pid;
            _closedSince = null;
            (_attemptPid, _attempts, _nextTryAt) = (0, 0, TimeSpan.Zero);
            return new FollowStep(FollowEvent.Recovered, old, pid, attempt, elapsed);
        }
        if (_attempts >= MaxAttemptsPerInstance)
        {
            _gaveUpPid = pid;
            return new FollowStep(FollowEvent.GaveUpOnInstance, old, pid, _attempts, elapsed, Error: error);
        }
        var wait = TimeSpan.FromSeconds(BackoffSeconds[Math.Min(_attempts - 1, BackoffSeconds.Length - 1)]);
        _nextTryAt = now + wait;
        return new FollowStep(FollowEvent.RecoveryFailed, old, pid, _attempts, elapsed, wait, error);
    }
}
