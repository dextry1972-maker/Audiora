using System.Net;
using System.Net.Sockets;
using SonosFlow.Core.Sonos;

namespace SonosFlow.Core.Tests;

/// <summary>
/// The persistent search against a scripted probe (no network, no Sonos). Timings are scaled down (tens of milliseconds instead of seconds);
/// the assertions are about order and counts, with wide margins, never about exact times.
/// </summary>
public sealed class PersistentDiscoveryTests
{
    private static readonly SonosDevice Five = Device("RINCON_FIVE", "Salón", "10.0.0.5");
    private static readonly SonosDevice Move = Device("RINCON_MOVE", "Cocina", "10.0.0.6");

    private static SonosDevice Device(string udn, string room, string ip) => new(IPAddress.Parse(ip), room, "Sonos Five", "S23", udn);

    private static DiscoveryTimings Fast(int windowMs = 600, int intervalMs = 40, int settleMs = 80, int networkMs = 10) =>
        new(TimeSpan.FromMilliseconds(windowMs), TimeSpan.FromMilliseconds(intervalMs), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(settleMs), TimeSpan.FromMilliseconds(networkMs));

    private static IReadOnlyList<SonosDevice> None => [];

    private sealed class Script
    {
        private int _calls;
        private readonly Func<int, IReadOnlyList<SonosDevice>> _answer;
        public Script(Func<int, IReadOnlyList<SonosDevice>> answer) => _answer = answer;
        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent { get; private set; }
        private int _running;

        public Task<IReadOnlyList<SonosDevice>> Probe(TimeSpan listenFor, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _calls);
            int now = Interlocked.Increment(ref _running);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try { return Task.FromResult(_answer(n)); }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    [Fact]
    public async Task A_device_that_is_there_from_the_start_is_reported_at_once_and_the_search_ends_soon_after()
    {
        var script = new Script(_ => [Five]);
        using var search = new PersistentDiscovery(script.Probe, Fast(windowMs: 5000, settleMs: 100));
        var updates = new List<IReadOnlyList<SonosDevice>>();

        var result = await search.StartAsync(l => { lock (updates) updates.Add(l); });

        Assert.Equal(DiscoveryEnd.Found, result.End);
        Assert.Equal([Five], result.Devices);
        Assert.Single(updates);                                        // one report, not one per attempt
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(3), "it must not wait out the whole window once something answered");
        Assert.False(search.IsRunning);
    }

    [Fact]
    public async Task A_device_that_shows_up_late_is_found_by_a_later_attempt()
    {
        var script = new Script(n => n < 6 ? None : [Five]);
        using var search = new PersistentDiscovery(script.Probe, Fast());
        var firstReport = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = search.StartAsync(_ => firstReport.TrySetResult(script.Calls));
        int callsWhenReported = await firstReport.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await task;

        Assert.True(callsWhenReported >= 6, $"reported after {callsWhenReported} attempts");
        Assert.Equal(DiscoveryEnd.Found, result.End);
        Assert.Equal([Five], result.Devices);
    }

    [Fact]
    public async Task While_the_network_is_not_ready_failing_attempts_do_not_end_the_search_and_a_later_one_succeeds()
    {
        // an adapter that is still coming up: no usable interface → the probe throws; it appears during the search
        var script = new Script(n => n <= 4 ? throw new SocketException((int)SocketError.NetworkUnreachable) : [Five]);
        using var search = new PersistentDiscovery(script.Probe, Fast());

        var result = await search.StartAsync();

        Assert.Equal(DiscoveryEnd.Found, result.End);
        Assert.Equal([Five], result.Devices);
        Assert.True(result.Attempts >= 5);
    }

    [Fact]
    public async Task Without_any_answer_the_search_runs_the_whole_window_with_periodic_attempts_and_then_says_so()
    {
        var script = new Script(_ => None);
        var timings = Fast(windowMs: 500, intervalMs: 50);
        using var search = new PersistentDiscovery(script.Probe, timings);

        var result = await search.StartAsync();

        Assert.Equal(DiscoveryEnd.TimedOut, result.End);
        Assert.Empty(result.Devices);
        Assert.True(result.Elapsed >= timings.Window - TimeSpan.FromMilliseconds(50), $"ended after {result.Elapsed}");
        Assert.InRange(result.Attempts, 4, 11);                        // ≈ window / interval, never a busy loop
        Assert.False(search.IsRunning);
    }

    [Fact]
    public async Task The_default_search_is_45_seconds_with_an_attempt_every_few_seconds()
    {
        var t = DiscoveryTimings.Default;
        Assert.Equal(TimeSpan.FromSeconds(45), t.Window);
        Assert.InRange(t.Interval.TotalSeconds, 3, 5);
        Assert.True(t.AttemptTimeout < t.Interval);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Pressing_search_again_while_one_runs_joins_it_instead_of_starting_another()
    {
        var script = new Script(_ => None);
        using var search = new PersistentDiscovery(script.Probe, Fast(windowMs: 400));

        var first = search.StartAsync();
        var second = search.StartAsync();
        var third = search.StartAsync();

        Assert.Same(first, second);
        Assert.Same(first, third);
        await first;
        Assert.Equal(1, script.MaxConcurrent);                         // attempts never overlap either
    }

    [Fact]
    public async Task A_new_search_can_start_once_the_previous_one_has_ended()
    {
        var script = new Script(_ => [Five]);
        using var search = new PersistentDiscovery(script.Probe, Fast(settleMs: 40));

        var one = await search.StartAsync();
        var two = await search.StartAsync();

        Assert.Equal(DiscoveryEnd.Found, one.End);
        Assert.Equal(DiscoveryEnd.Found, two.End);
    }

    [Fact]
    public async Task Cancelling_stops_the_attempts_keeps_what_was_found_and_leaves_nothing_running()
    {
        var script = new Script(n => n < 3 ? None : [Five]);
        using var search = new PersistentDiscovery(script.Probe, Fast(windowMs: 30_000, settleMs: 30_000));
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = search.StartAsync(_ => reported.TrySetResult());
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        search.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DiscoveryEnd.Cancelled, result.End);
        Assert.Equal([Five], result.Devices);
        Assert.False(search.IsRunning);
        int calls = script.Calls;
        await Task.Delay(200);
        Assert.Equal(calls, script.Calls);                             // no attempt after the cancellation
    }

    [Fact]
    public async Task Cancelling_during_an_attempt_reaches_the_probe_through_its_token()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var search = new PersistentDiscovery(async (listenFor, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return None;
        }, Fast(windowMs: 30_000));

        var task = search.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        search.Cancel();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DiscoveryEnd.Cancelled, (await task).End);
    }

    [Fact]
    public async Task Cancelling_with_nothing_running_does_nothing_and_a_search_can_still_start()
    {
        var script = new Script(_ => [Five]);
        using var search = new PersistentDiscovery(script.Probe, Fast(settleMs: 40));

        search.Cancel();
        search.NetworkChanged();
        var result = await search.StartAsync();

        Assert.Equal(DiscoveryEnd.Found, result.End);
    }

    [Fact]
    public async Task Two_devices_are_listed_once_each_in_order_and_each_arrival_is_reported()
    {
        var script = new Script(n => n switch { 1 => [Five], 2 => [Five, Five], _ => [Move, Five, Move] });
        using var search = new PersistentDiscovery(script.Probe, Fast(settleMs: 400));
        var updates = new List<string[]>();

        var result = await search.StartAsync(l => { lock (updates) updates.Add(l.Select(d => d.RoomName).ToArray()); });

        Assert.Equal(["Cocina", "Salón"], result.Devices.Select(d => d.RoomName));   // sorted, no duplicates
        Assert.Equal(["Salón"], updates[0]);
        Assert.Equal(2, updates.Count);                                              // the repeated answers did not re-report
        Assert.Equal(["Cocina", "Salón"], updates[1]);
    }

    [Fact]
    public async Task A_player_that_changes_address_is_updated_not_duplicated()
    {
        var moved = Five with { Address = IPAddress.Parse("10.0.0.99") };
        var script = new Script(n => n < 3 ? [Five] : [moved]);
        using var search = new PersistentDiscovery(script.Probe, Fast(settleMs: 200));

        var result = await search.StartAsync();

        var only = Assert.Single(result.Devices);
        Assert.Equal(IPAddress.Parse("10.0.0.99"), only.Address);
    }

    [Fact]
    public async Task A_network_change_brings_the_next_attempt_forward()
    {
        // long interval: without the nudge the second attempt would be 30 s away
        var script = new Script(n => n < 2 ? None : [Five]);
        var timings = new DiscoveryTimings(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        using var search = new PersistentDiscovery(script.Probe, timings);

        var task = search.StartAsync();
        while (script.Calls < 1) await Task.Delay(5);
        search.NetworkChanged();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DiscoveryEnd.Found, result.End);
        Assert.True(script.Calls >= 2);
    }

    [Fact]
    public async Task A_network_change_that_arrives_during_an_attempt_is_not_lost()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var timings = new DiscoveryTimings(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        using var search = new PersistentDiscovery(async (_, ct) =>
        {
            int n = Interlocked.Increment(ref calls);
            if (n == 1) await gate.Task.WaitAsync(ct);                 // the first attempt is still running when the network changes
            return n < 2 ? None : [Five];
        }, timings);

        var task = search.StartAsync();
        while (Volatile.Read(ref calls) < 1) await Task.Delay(5);
        search.NetworkChanged();
        gate.SetResult();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DiscoveryEnd.Found, result.End);
    }
}
