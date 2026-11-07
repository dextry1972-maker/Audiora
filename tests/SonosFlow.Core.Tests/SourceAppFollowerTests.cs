using SonosFlow.Core.Audio;
using SonosFlow.Core.Streaming;

namespace SonosFlow.Core.Tests;

/// <summary>
/// «Una aplicación»: following the selected app when it closes and opens again. The probe is a fake (no Windows, no audio);
/// the re-attach callback stands for "capture the new process and give the Sonos a fresh connection".
/// </summary>
public sealed class SourceAppFollowerTests
{
    private const string Brave = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";

    private sealed class FakeProbe : ISourceAppProbe
    {
        public HashSet<int> Running { get; } = [];
        public int? InstanceWithAudio { get; set; }
        public bool ExeRunning { get; set; }
        public int AnyProcessCalls, FindCalls;
        public bool IsRunning(int pid) => Running.Contains(pid);
        public bool AnyProcessOf(string exePath) { AnyProcessCalls++; return ExeRunning; }
        public int? FindInstanceWithAudio(string exePath, bool isolated) { FindCalls++; return InstanceWithAudio; }
    }

    private sealed class Reattacher
    {
        public List<(int Old, int New)> Calls { get; } = [];
        public Func<int, bool> Result { get; set; } = _ => true;
        public Task<bool> Invoke(int oldPid, int newPid) { Calls.Add((oldPid, newPid)); return Task.FromResult(Result(Calls.Count)); }
    }

    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private static (SourceAppFollower Follower, FakeProbe Probe, Reattacher Reattach) Setup(int pid = 100)
    {
        var probe = new FakeProbe();
        probe.Running.Add(pid);
        return (new SourceAppFollower(pid, Brave, false, probe), probe, new Reattacher());
    }

    // 1 ─ the selected app closes while streaming
    [Fact]
    public async Task When_the_selected_app_closes_it_is_reported_once_and_nothing_is_restarted()
    {
        var (f, probe, re) = Setup();
        Assert.Equal(FollowEvent.None, (await f.CheckAsync(S(0), re.Invoke)).Event);

        probe.Running.Clear();                                                  // Brave closed
        var step = await f.CheckAsync(S(3), re.Invoke);

        Assert.Equal(FollowEvent.SourceClosed, step.Event);
        Assert.Equal(100, step.OldPid);
        Assert.False(f.SourceAvailable);
        Assert.Empty(re.Calls);
    }

    // 2 ─ it stays closed: Audiora waits, without a retry loop
    [Fact]
    public async Task While_the_app_stays_closed_it_waits_with_only_a_cheap_check_and_no_retries()
    {
        var (f, probe, re) = Setup();
        probe.Running.Clear();
        var events = new List<FollowEvent>();
        for (int i = 1; i <= 200; i++) events.Add((await f.CheckAsync(S(i * 3), re.Invoke)).Event);   // 10 minutes closed

        Assert.Equal(1, events.Count(e => e == FollowEvent.SourceClosed));     // said once, not every check
        Assert.All(events.Skip(1), e => Assert.Equal(FollowEvent.None, e));
        Assert.Empty(re.Calls);                                                // no capture or reconnection attempts
        Assert.Equal(0, probe.FindCalls);                                      // the audio sessions are not even listed
        Assert.True(probe.AnyProcessCalls <= 200);
    }

    [Fact]
    public async Task An_instance_that_is_open_but_has_no_audio_yet_is_waited_for_not_attached()
    {
        var (f, probe, re) = Setup();
        probe.Running.Clear();
        await f.CheckAsync(S(3), re.Invoke);
        probe.ExeRunning = true;                                               // Brave opened, nothing playing yet
        for (int i = 2; i < 10; i++) Assert.Equal(FollowEvent.None, (await f.CheckAsync(S(i * 3), re.Invoke)).Event);
        Assert.Empty(re.Calls);
    }

    // 3 ─ a new instance with another PID: the audio is recovered
    [Fact]
    public async Task A_new_instance_with_another_pid_is_attached_once_and_followed_from_then_on()
    {
        var (f, probe, re) = Setup();
        probe.Running.Clear();
        await f.CheckAsync(S(3), re.Invoke);
        probe.ExeRunning = true;
        probe.InstanceWithAudio = 555;
        probe.Running.Add(555);

        var step = await f.CheckAsync(S(6), re.Invoke);

        Assert.Equal(FollowEvent.Recovered, step.Event);
        Assert.Equal((100, 555), (step.OldPid, step.NewPid));
        Assert.Equal(1, step.Attempt);
        Assert.NotNull(step.Elapsed);
        Assert.Equal([(100, 555)], re.Calls);
        Assert.Equal(555, f.CurrentPid);
        Assert.True(f.SourceAvailable);
        for (int i = 3; i < 20; i++) Assert.Equal(FollowEvent.None, (await f.CheckAsync(S(i * 3), re.Invoke)).Event);
        Assert.Single(re.Calls);                                               // nothing more once it is attached
    }

    [Fact]
    public async Task A_failed_attach_backs_off_and_gives_up_on_that_instance_after_a_few_tries()
    {
        var (f, probe, re) = Setup();
        probe.Running.Clear();
        await f.CheckAsync(S(0), re.Invoke);
        (probe.ExeRunning, probe.InstanceWithAudio) = (true, 555);
        re.Result = _ => false;

        var steps = new List<FollowStep>();
        for (int i = 1; i <= 120; i++) steps.Add(await f.CheckAsync(S(i), re.Invoke));   // a check every second for 2 min

        Assert.Equal(SourceAppFollower.MaxAttemptsPerInstance, re.Calls.Count);   // bounded, and spaced out (not 120)
        Assert.Equal(FollowEvent.GaveUpOnInstance, steps.Last(s => s.Event != FollowEvent.None).Event);
        var waits = steps.Where(s => s.Event == FollowEvent.RecoveryFailed).Select(s => s.NextTryIn!.Value.TotalSeconds).ToList();
        Assert.Equal([3, 6, 12, 24], waits);

        probe.InstanceWithAudio = 777;                                          // the user opens it again: a fresh start
        probe.Running.Add(777);
        re.Result = _ => true;
        Assert.Equal(FollowEvent.Recovered, (await f.CheckAsync(S(200), re.Invoke)).Event);
    }

    [Fact]
    public async Task An_exception_while_attaching_counts_as_a_failed_attempt_and_is_reported()
    {
        var (f, probe, _) = Setup();
        probe.Running.Clear();
        await f.CheckAsync(S(0), (_, _) => Task.FromResult(true));
        (probe.ExeRunning, probe.InstanceWithAudio) = (true, 555);

        var step = await f.CheckAsync(S(3), (_, _) => throw new InvalidOperationException("captura no disponible"));

        Assert.Equal(FollowEvent.RecoveryFailed, step.Event);
        Assert.Contains("captura no disponible", step.Error);
    }

    [Fact]
    public async Task Stopping_the_session_during_an_attach_is_not_counted_as_a_failure()
    {
        var (f, probe, _) = Setup();
        probe.Running.Clear();
        await f.CheckAsync(S(0), (_, _) => Task.FromResult(true));
        (probe.ExeRunning, probe.InstanceWithAudio) = (true, 555);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.CheckAsync(S(3), (_, _) => throw new OperationCanceledException(cts.Token), cts.Token));
    }

    // 4 ─ another app (not the source) closes
    [Fact]
    public async Task Closing_an_app_that_is_not_the_source_changes_nothing()
    {
        var (f, probe, re) = Setup(pid: 100);
        probe.Running.Add(300);                                                 // Discord, not selected
        await f.CheckAsync(S(0), re.Invoke);

        probe.Running.Remove(300);                                              // Discord closes
        probe.ExeRunning = true;
        probe.InstanceWithAudio = 400;                                          // and something else opens
        for (int i = 1; i < 20; i++) Assert.Equal(FollowEvent.None, (await f.CheckAsync(S(i * 3), re.Invoke)).Event);

        Assert.Empty(re.Calls);
        Assert.True(f.SourceAvailable);
        Assert.Equal(0, probe.FindCalls);                                       // with the source alive nothing else is looked at
    }

    // 5 ─ the source stays open but paused or silent
    [Fact]
    public async Task A_running_source_is_never_reattached_whether_it_plays_pauses_or_is_silent()
    {
        var (f, probe, re) = Setup();
        // The follower does not look at audio at all: an hour of checks while the process lives, whatever it plays.
        for (int i = 0; i < 1200; i++) Assert.Equal(FollowEvent.None, (await f.CheckAsync(S(i * 3), re.Invoke)).Event);
        Assert.Empty(re.Calls);
        Assert.Equal(0, probe.AnyProcessCalls);
        Assert.Equal(0, probe.FindCalls);
        Assert.True(f.SourceAvailable);
    }

    [Fact]
    public async Task The_same_process_found_again_is_not_a_new_instance()
    {
        var (f, probe, re) = Setup();
        probe.Running.Clear();
        await f.CheckAsync(S(0), re.Invoke);
        (probe.ExeRunning, probe.InstanceWithAudio) = (true, 100);              // a stale answer naming the old process
        Assert.Equal(FollowEvent.None, (await f.CheckAsync(S(3), re.Invoke)).Event);
        Assert.Empty(re.Calls);
    }
}

/// <summary>6 ─ «Silenciar el PC» survives the app restart: the new process is sent to the silent output with nothing restored.</summary>
public sealed class AppOutputRedirectRetargetTests
{
    private const string Silent = "{0.0.0.00000000}.{silent-output}";

    private sealed class Routing : AppOutputRedirect.IRouting
    {
        public Dictionary<int, string?> Outputs { get; } = new() { [10] = "benq" };
        public List<(int Pid, string? Device)> Writes { get; } = [];
        public string? Get(int pid) => Outputs.GetValueOrDefault(pid);
        public void Set(int pid, string? deviceId) { Outputs[pid] = deviceId; Writes.Add((pid, deviceId)); }
        public IReadOnlyList<int> ProcessesOf(string exePath) => Outputs.Keys.Reverse().ToList();
    }

    [Fact]
    public void The_restarted_app_goes_straight_to_the_silent_output_and_the_original_output_is_still_restored_at_the_end()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        var routing = new Routing();
        try
        {
            var redirect = new AppOutputRedirect(10, "brave.exe", Silent, file, routing);
            Assert.Equal(Silent, routing.Outputs[10]);

            routing.Outputs.Remove(10);                                         // Brave closes…
            routing.Outputs[20] = Silent;                                       // …and comes back (Windows remembers per exe)
            redirect.Retarget(20);

            Assert.Equal(Silent, routing.Outputs[20]);
            Assert.DoesNotContain(routing.Writes, w => w.Device == "benq");     // never sent back to the speakers in between
            Assert.True(File.Exists(file));                                     // the undo record (with "benq") is kept

            redirect.Dispose();                                                 // stop streaming
            Assert.True(redirect.Restored);
            Assert.Equal("benq", routing.Outputs[20]);                          // the app gets its own output back
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Retarget_after_stopping_is_refused()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        try
        {
            var redirect = new AppOutputRedirect(10, "brave.exe", Silent, file, new Routing());
            redirect.Dispose();
            Assert.Throws<ObjectDisposedException>(() => redirect.Retarget(20));
        }
        finally { File.Delete(file); }
    }
}
