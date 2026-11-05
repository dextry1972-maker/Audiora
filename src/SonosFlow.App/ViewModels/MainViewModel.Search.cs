using System.Net.NetworkInformation;
using System.Windows.Input;
using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Sonos;

namespace SonosFlow.App.ViewModels;

/// <summary>
/// The search for speakers as the user sees it. It keeps looking for up to <see cref="DiscoveryTimings.Window"/> (45 s), reports each
/// speaker the moment it answers (the list is updated in place, the selection is kept, and "connect" works at once), can be cancelled,
/// and never runs twice at the same time. A search only sends discovery requests: it does not touch a stream that is running, and it
/// ends by itself when the user connects or the app closes.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Why a search was stopped from outside.</summary>
    private enum SearchStop { None, User, Superseded }

    /// <summary>Wait this long with no speaker before checking the Windows hotspot (a read-only query).</summary>
    private static readonly TimeSpan EarlyHotspotCheckAfter = TimeSpan.FromSeconds(5);

    private PersistentDiscovery _discovery = new();
    private readonly HashSet<string> _seenInSearch = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _firstResult = CompletedSignal();
    private CancellationTokenSource? _searchHelpersCts;
    private SearchStop _searchStop;
    private bool _speakerPickedByUser;
    private string? _preferredUdn;
    private HotspotPrompt? _heldHotspotPrompt;
    private bool _isSearching;
    private bool _simulateSearching;
    private ICommand? _cancelSearchCommand;

    private static TaskCompletionSource CompletedSignal()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    /// <summary>Test and capture hook: swaps the search (probe and timings).</summary>
    internal void UseDiscovery(PersistentDiscovery discovery) => _discovery = discovery;

    /// <summary>Capture-only: a search that never finds anything, to look at the "searching" state. Never awaited by start-up.</summary>
    internal void SimulateSearching()
    {
        _simulateSearching = true;
        UseDiscovery(new PersistentDiscovery(async (listenFor, ct) => { await Task.Delay(listenFor, ct); return []; }));
    }

    /// <summary>True from the moment a search starts until it has ended (found, timed out, cancelled).</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!Set(ref _isSearching, value)) return;
            OnPropertyChanged(nameof(ShowSearchCancel));
            OnPropertyChanged(nameof(ShowSearchRetry));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool ShowSearchCancel => IsSearching;

    /// <summary>The search ended with nothing: offer to try again (unless the hotspot notice already has its own "search again").</summary>
    public bool ShowSearchRetry => State == UiState.NoSpeaker && !IsSearching && !ShowHotspotRescan;

    public ICommand CancelSearchCommand => _cancelSearchCommand ??= new RelayCommand(() => CancelSearch(SearchStop.User), () => IsSearching);

    /// <summary>
    /// Searches for speakers. Completes as soon as the first speaker is known or the search ends, whichever comes first; the search itself
    /// goes on in the background for a few seconds more to pick up further speakers. Already searching: joins that search.
    /// </summary>
    public Task DiscoverAsync()
    {
        if (IsStreaming) return Task.CompletedTask;           // a stream is running: it is never disturbed
        if (_isSearching) return _firstResult.Task;           // pressing "search" again does not start a second search

        if (NoSpeakersForCapture)
        {
            Speakers.Clear();
            SelectedSpeaker = null;
            State = UiState.NoSpeaker;
            Detail(() => L.T("status.detail.noSpeakerHint"));
            _ = CheckHotspotAsync();
            return Task.CompletedTask;
        }

        BeginSearch();
        _ = RunSearchAsync();
        return _simulateSearching ? Task.CompletedTask : _firstResult.Task;
    }

    private void BeginSearch()
    {
        _searchStop = SearchStop.None;
        _firstResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _seenInSearch.Clear();
        _preferredUdn = _settings.LastSpeakerUdn;
        _speakerPickedByUser = false;
        _heldHotspotPrompt = null;
        IsSearching = true;
        // Speakers already listed stay (and stay selectable) while the search runs; only a list with nothing usable shows "searching".
        if (SelectedSpeaker is null || State is UiState.NoSpeaker or UiState.Error)
        {
            State = UiState.Discovering;
            Detail(() => L.T("status.detail.searching"));
        }
        else Detail(() => L.T("status.detail.searchingMore"));
    }

    private async Task RunSearchAsync()
    {
        _searchHelpersCts?.Dispose();
        var helpers = _searchHelpersCts = new CancellationTokenSource();
        void OnNetworkChanged(object? s, EventArgs e) => _discovery.NetworkChanged();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _ = EarlyHotspotCheckAsync(helpers.Token);
        DiscoveryResult result;
        try { result = await _discovery.StartAsync(devices => _ui.BeginInvoke(() => OnSearchUpdate(devices))); }
        catch (Exception ex)
        {
            AppLog.Write($"búsqueda: error inesperado ({ex.GetType().Name})");
            result = new DiscoveryResult([], DiscoveryEnd.TimedOut, 0, TimeSpan.Zero);
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            helpers.Cancel();
        }
        FinishSearch(result);
    }

    private void CancelSearch(SearchStop reason)
    {
        if (!_isSearching) return;
        if (_searchStop != SearchStop.Superseded) _searchStop = reason;
        _discovery.Cancel();
    }

    /// <summary>A report from the running search: the speakers known so far.</summary>
    private void OnSearchUpdate(IReadOnlyList<SonosDevice> devices)
    {
        // Reports queued before the search was stopped (connect, close) must not touch a stream that is starting.
        if (!_isSearching || _searchStop != SearchStop.None || IsStreaming) return;
        ApplyDevices(devices);
    }

    private void ApplyDevices(IReadOnlyList<SonosDevice> devices)
    {
        if (devices.Count == 0) return;
        var merged = SpeakerListSync.Merge(Speakers, devices);
        foreach (var key in merged.Seen) _seenInSearch.Add(key);
        foreach (var (old, fresh) in merged.Replaced)
            if (SelectedSpeaker == old) Select(fresh); // same player, new address

        AutoSelect();
        if (State is UiState.Discovering or UiState.NoSpeaker)
        {
            ResetHotspotPrompt();
            State = UiState.Ready;
        }
        Detail(() => L.T("status.detail.searchingMore"));
        _firstResult.TrySetResult();
    }

    /// <summary>
    /// Picks the speaker to use among those seen in this search: the one used last time if it answered, else the current one if it
    /// answered, else a Sonos Five, else the first. A speaker the user chose by hand is never replaced.
    /// </summary>
    private void AutoSelect()
    {
        if (_speakerPickedByUser && SelectedSpeaker is { } chosen && Speakers.Contains(chosen)) return;
        var seen = Speakers.Where(s => _seenInSearch.Contains(SpeakerListSync.KeyOf(s.Device))).ToList();
        var pick = seen.FirstOrDefault(s => s.Device.Udn == _preferredUdn)
                   ?? (SelectedSpeaker is { } current && seen.Contains(current) ? current : null)
                   ?? seen.FirstOrDefault(s => s.Model.Contains("Five", StringComparison.OrdinalIgnoreCase))
                   ?? seen.FirstOrDefault();
        if (pick is not null && pick != SelectedSpeaker) Select(pick);
    }

    private void FinishSearch(DiscoveryResult result)
    {
        try
        {
            // Connecting or closing: the state belongs to that now.
            if (_searchStop != SearchStop.Superseded && !IsStreaming) SettleAfterSearch(result);
        }
        finally
        {
            IsSearching = false;
            _firstResult.TrySetResult();
        }
        AppLog.Write($"búsqueda: {result.End}, {result.Devices.Count} altavoz(ces), {result.Attempts} intentos, {result.Elapsed.TotalSeconds:0.#} s");
    }

    private void SettleAfterSearch(DiscoveryResult result)
    {
        ApplyDevices(result.Devices); // the final list (idempotent) before judging the outcome

        // A search that ran to the end drops the speakers it did not see (they are gone); a cancelled one keeps what it had.
        if (result.End != DiscoveryEnd.Cancelled)
        {
            var seen = new HashSet<string>(result.Devices.Select(SpeakerListSync.KeyOf), StringComparer.OrdinalIgnoreCase);
            SpeakerListSync.Prune(Speakers, seen);
            if (SelectedSpeaker is { } selected && !Speakers.Contains(selected)) { SelectedSpeaker = null; AutoSelect(); }
        }

        if (SelectedSpeaker is null || Speakers.Count == 0)
        {
            SelectedSpeaker = null;
            State = UiState.NoSpeaker;
            bool cancelled = result.End == DiscoveryEnd.Cancelled;
            Detail(cancelled ? () => L.T("status.detail.noSpeakerHint") : () => L.T("status.detail.searchTimedOut"));
            if (!cancelled) ShowHotspotNoticeAfterEmptySearch();
        }
        else
        {
            if (State is UiState.Discovering or UiState.NoSpeaker) State = UiState.Ready;
            int speakerCount = Speakers.Count;
            Detail(() => L.Plural("status.detail.speakersFound", speakerCount));
        }
    }

    /// <summary>
    /// With no speaker after a few seconds: say that devices may still be joining the network, and look at the hotspot (read-only)
    /// while the search goes on. Nothing is claimed about the hotspot beyond what the query returns.
    /// </summary>
    private async Task EarlyHotspotCheckAsync(CancellationToken ct)
    {
        try { await Task.Delay(EarlyHotspotCheckAfter, ct); }
        catch (OperationCanceledException) { return; }
        if (!_isSearching || Speakers.Count > 0 || _searchStop != SearchStop.None) return;
        if (State == UiState.Discovering) Detail(() => L.T("status.detail.waitingDevices"));
        await CheckHotspotAsync(early: true);
    }

    private void ShowHotspotNoticeAfterEmptySearch()
    {
        if (_heldHotspotPrompt is { } held && _hotspotPrompt != HotspotPrompt.Enabling)
        {
            _heldHotspotPrompt = null;
            HotspotPrompt = held;
        }
        else _ = CheckHotspotAsync();
    }
}
