using System.Net.NetworkInformation;
using SonosFlow.App.Infrastructure;

namespace SonosFlow.App.ViewModels;

/// <summary>
/// Startup readiness: when Audiora should resume streaming at launch (e.g. at Windows sign-in) the Mobile Hotspot
/// and the Sonos may not be up yet. Retries discovery and start on a growing schedule for at most
/// <see cref="ReadyTimeout"/>; a network change brings the next attempt forward. Any manual action cancels it.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly int[] ReadyScheduleSeconds = [2, 3, 5, 8, 13, 20, 30];
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _readyCts;

    internal async Task WaitForSpeakerAndStartAsync()
    {
        CancelStartupWait();
        var cts = _readyCts = new CancellationTokenSource();
        var ct = cts.Token;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TaskCompletionSource? wake = null;
        void OnNetworkChanged(object? s, EventArgs e) => _ui.BeginInvoke(() => wake?.TrySetResult());
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        try
        {
            for (int attempt = 1; !ct.IsCancellationRequested; attempt++)
            {
                if (SelectedSpeaker is null) await DiscoverAsync();
                if (ct.IsCancellationRequested) return;
                if (SelectedSpeaker is not null)
                {
                    await StartAsync();
                    if (IsStreaming || ct.IsCancellationRequested) return;
                    if (!_lastStartRetryable) return; // a definitive problem (grouped speaker, app closed…): already shown
                }

                if (clock.Elapsed >= ReadyTimeout)
                {
                    StatusDetail = "No se encontró el Sonos en 5 minutos. Comprueba la zona Wi-Fi del PC y pulsa «Buscar altavoces».";
                    AppLog.Write($"arranque: sin Sonos tras {attempt} intentos");
                    return;
                }

                int delay = ReadyScheduleSeconds[Math.Min(attempt - 1, ReadyScheduleSeconds.Length - 1)];
                StatusDetail = $"Esperando a la red y al Sonos… nuevo intento en {delay} s (intento {attempt})";
                AppLog.Write($"arranque: intento {attempt} sin éxito, siguiente en {delay} s");
                wake = new TaskCompletionSource();
                var sleep = Task.Delay(TimeSpan.FromSeconds(delay), ct);
                if (await Task.WhenAny(sleep, wake.Task) == wake.Task)
                {
                    // An adapter changed (the hotspot coming up): give it a moment to get its address.
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
                }
                if (State == UiState.Error) SelectedSpeaker = null; // a failed start may mean a stale address: rediscover
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            if (_readyCts == cts) _readyCts = null;
            cts.Dispose();
        }
    }

    private void CancelStartupWait()
    {
        if (_readyCts is { } cts && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            AppLog.Write("arranque: espera cancelada");
        }
    }
}
