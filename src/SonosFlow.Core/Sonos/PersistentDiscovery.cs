using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SonosFlow.Core.Sonos;

/// <summary>
/// How long and how often a search looks for Sonos players. One place to tune it.
/// Right after the Windows hotspot is switched on the Sonos still has to join it, which takes tens of seconds:
/// one short search is not enough, so the search keeps trying for <see cref="Window"/>.
/// </summary>
/// <param name="Window">Longest a search runs.</param>
/// <param name="Interval">Time between the starts of two attempts (each attempt re-reads the network interfaces).</param>
/// <param name="AttemptTimeout">How long one attempt listens for answers.</param>
/// <param name="SettleAfterFirst">Once a player answered, how much longer the search goes on for further players before it ends.</param>
/// <param name="AfterNetworkChange">After the network changed (an adapter coming up), how long to wait for it to get its address before the next attempt.</param>
public sealed record DiscoveryTimings(TimeSpan Window, TimeSpan Interval, TimeSpan AttemptTimeout, TimeSpan SettleAfterFirst, TimeSpan AfterNetworkChange)
{
    public static DiscoveryTimings Default { get; } = new(
        Window: TimeSpan.FromSeconds(45),
        Interval: TimeSpan.FromSeconds(4),
        AttemptTimeout: TimeSpan.FromSeconds(2.5),
        SettleAfterFirst: TimeSpan.FromSeconds(8),
        AfterNetworkChange: TimeSpan.FromSeconds(1.5));
}

public enum DiscoveryEnd
{
    /// <summary>At least one player answered; the search ended after the settle time (or the window).</summary>
    Found,
    /// <summary>The window ran out with no answer.</summary>
    TimedOut,
    /// <summary><see cref="PersistentDiscovery.Cancel"/> was called.</summary>
    Cancelled,
}

public sealed record DiscoveryResult(IReadOnlyList<SonosDevice> Devices, DiscoveryEnd End, int Attempts, TimeSpan Elapsed);

/// <summary>One attempt: listen for <paramref name="listenFor"/> and return the players that answered.</summary>
public delegate Task<IReadOnlyList<SonosDevice>> DiscoveryProbe(TimeSpan listenFor, CancellationToken ct);

/// <summary>
/// A search that keeps looking: periodic attempts for up to <see cref="DiscoveryTimings.Window"/>, reporting players as soon as they
/// answer, with a single search at a time and clean cancellation. It only ever sends multicast discovery requests and reads the
/// players' descriptions: it never touches a stream or a player's settings.
/// </summary>
public sealed class PersistentDiscovery : IDisposable
{
    private readonly DiscoveryProbe _probe;
    private readonly DiscoveryTimings _timings;
    private readonly object _gate = new();
    private Run? _current;

    public PersistentDiscovery(DiscoveryProbe? probe = null, DiscoveryTimings? timings = null)
    {
        _probe = probe ?? ((listenFor, ct) => SonosDiscovery.DiscoverAsync(listenFor, ct));
        _timings = timings ?? DiscoveryTimings.Default;
    }

    public DiscoveryTimings Timings => _timings;

    public bool IsRunning { get { lock (_gate) return _current is not null; } }

    /// <summary>
    /// Starts a search, unless one is already running: then that one's task is returned (pressing "search" repeatedly never starts a second).
    /// <paramref name="onChange"/> is called, from a worker thread, with every player known so far each time the list changes.
    /// </summary>
    public Task<DiscoveryResult> StartAsync(Action<IReadOnlyList<SonosDevice>>? onChange = null)
    {
        lock (_gate)
        {
            if (_current is { } running) return running.Completion;
            var run = _current = new Run(onChange);
            run.Completion = Task.Run(() => ExecuteAsync(run));
            return run.Completion;
        }
    }

    /// <summary>Stops the running search (if any); its task completes with <see cref="DiscoveryEnd.Cancelled"/> and the players found so far.</summary>
    public void Cancel()
    {
        lock (_gate) _current?.Cancel();
    }

    /// <summary>The network changed (e.g. the hotspot's adapter came up): the next attempt comes forward instead of waiting out the interval.</summary>
    public void NetworkChanged()
    {
        Run? run;
        lock (_gate) run = _current;
        run?.Nudge();
    }

    public void Dispose() => Cancel();

    private async Task<DiscoveryResult> ExecuteAsync(Run run)
    {
        var ct = run.Token;
        var clock = Stopwatch.StartNew();
        var known = new Dictionary<string, SonosDevice>(StringComparer.OrdinalIgnoreCase);
        TimeSpan? firstAt = null;
        int attempts = 0;
        DiscoveryEnd end;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var left = Deadline(firstAt) - clock.Elapsed;
                if (left <= TimeSpan.Zero) break;

                var started = clock.Elapsed;
                attempts++;
                IReadOnlyList<SonosDevice> found;
                try { found = await _probe(Min(_timings.AttemptTimeout, left), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is SocketException or InvalidOperationException or NetworkInformationException or HttpRequestException or IOException)
                {
                    found = []; // the network is not ready yet (an adapter coming up): the next attempt reads the interfaces again
                }

                if (Merge(known, found))
                {
                    firstAt ??= clock.Elapsed;
                    run.OnChange?.Invoke(Snapshot(known));
                }

                var wait = Min(started + _timings.Interval, Deadline(firstAt)) - clock.Elapsed;
                if (wait > TimeSpan.Zero) await run.WaitAsync(wait, _timings.AfterNetworkChange).ConfigureAwait(false);
            }
            end = known.Count > 0 ? DiscoveryEnd.Found : DiscoveryEnd.TimedOut;
        }
        catch (OperationCanceledException)
        {
            end = DiscoveryEnd.Cancelled;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, run)) _current = null;
                run.Dispose();
            }
        }
        return new DiscoveryResult(Snapshot(known), end, attempts, clock.Elapsed);
    }

    /// <summary>The search ends at the window, or earlier: a settle time after the first answer.</summary>
    private TimeSpan Deadline(TimeSpan? firstAt) => firstAt is { } f ? Min(_timings.Window, f + _timings.SettleAfterFirst) : _timings.Window;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static string KeyOf(SonosDevice d) => string.IsNullOrWhiteSpace(d.Udn) ? d.Address.ToString() : d.Udn;

    /// <summary>Adds new players and updates changed ones (same Udn, new address or name). True when the list changed.</summary>
    private static bool Merge(Dictionary<string, SonosDevice> known, IReadOnlyList<SonosDevice> found)
    {
        bool changed = false;
        foreach (var device in found)
        {
            var key = KeyOf(device);
            if (known.TryGetValue(key, out var existing) && existing == device) continue;
            known[key] = device;
            changed = true;
        }
        return changed;
    }

    private static IReadOnlyList<SonosDevice> Snapshot(Dictionary<string, SonosDevice> known) =>
        known.Values.OrderBy(d => d.RoomName, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Udn, StringComparer.Ordinal).ToList();

    /// <summary>State of one search.</summary>
    private sealed class Run(Action<IReadOnlyList<SonosDevice>>? onChange) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly object _l = new();
        private TaskCompletionSource? _wake;
        private bool _nudged;
        private bool _disposed;

        public Action<IReadOnlyList<SonosDevice>>? OnChange { get; } = onChange;
        public Task<DiscoveryResult> Completion { get; set; } = null!;
        public CancellationToken Token => _cts.Token;

        public void Cancel() { if (!_disposed) _cts.Cancel(); }

        public void Nudge()
        {
            lock (_l)
            {
                _nudged = true;
                _wake?.TrySetResult();
            }
        }

        /// <summary>Sleeps for <paramref name="delay"/>, or less if the network changes (then it gives the adapter a moment first).</summary>
        public async Task WaitAsync(TimeSpan delay, TimeSpan afterNetworkChange)
        {
            Task wake;
            lock (_l)
            {
                if (_nudged) { _nudged = false; wake = System.Threading.Tasks.Task.CompletedTask; }
                else wake = (_wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            using var sleepCts = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var sleep = System.Threading.Tasks.Task.Delay(delay, sleepCts.Token);
            var first = await System.Threading.Tasks.Task.WhenAny(sleep, wake).ConfigureAwait(false);
            sleepCts.Cancel();
            lock (_l) { _wake = null; _nudged = false; }
            try { await sleep.ConfigureAwait(false); }
            catch (OperationCanceledException) { Token.ThrowIfCancellationRequested(); }
            if (first == wake && afterNetworkChange > TimeSpan.Zero)
                await System.Threading.Tasks.Task.Delay(afterNetworkChange, Token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _disposed = true;
            _cts.Dispose();
        }
    }
}
