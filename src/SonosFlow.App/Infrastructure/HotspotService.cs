using System.Diagnostics;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace SonosFlow.App.Infrastructure;

/// <summary>What Windows says about the Mobile Hotspot. <see cref="Changing"/> = it is switching on/off right now.</summary>
public enum HotspotState { Unknown, Off, On, Changing }

public enum HotspotPrompt { None, Offer, AlreadyOn, Unknown, Enabling, Enabled, Failed }

public interface IHotspotController
{
    Task<HotspotState> GetStateAsync();
    /// <summary>Asks Windows to switch the hotspot on; true when it ended up on.</summary>
    Task<bool> EnableAsync();
    void OpenSettings();
}

/// <summary>Capture-mode stand-in (--fake-hotspot off|on|unknown|fail): never touches the real hotspot.</summary>
public sealed class FakeHotspot(string mode) : IHotspotController
{
    private HotspotState _state = mode switch { "off" or "fail" => HotspotState.Off, "on" => HotspotState.On, _ => HotspotState.Unknown };
    public Task<HotspotState> GetStateAsync() => Task.FromResult(_state);
    public async Task<bool> EnableAsync() { await Task.Delay(800); if (mode == "fail") return false; _state = HotspotState.On; return true; }
    public void OpenSettings() { }
}

/// <summary>Windows 10/11 Mobile Hotspot through the public WinRT tethering API (no admin rights, no registry).</summary>
public sealed class WindowsHotspot : IHotspotController
{
    private static readonly TimeSpan EnableTimeout = TimeSpan.FromSeconds(20);

    // Needs a connection profile that can share its connection; null = Windows does not allow it right now.
    private static NetworkOperatorTetheringManager? Manager()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null) return null;
        if (NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile) != TetheringCapability.Enabled) return null;
        return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }

    public static HotspotState Map(TetheringOperationalState s) => s switch
    {
        TetheringOperationalState.On => HotspotState.On,
        TetheringOperationalState.Off => HotspotState.Off,
        TetheringOperationalState.InTransition => HotspotState.Changing,
        _ => HotspotState.Unknown,
    };

    public Task<HotspotState> GetStateAsync() => Task.Run(() =>
    {
        try { return Manager() is { } m ? Map(m.TetheringOperationalState) : HotspotState.Unknown; }
        catch (Exception ex) { AppLog.Write($"hotspot: no se pudo consultar ({ex.GetType().Name})"); return HotspotState.Unknown; }
    });

    public async Task<bool> EnableAsync()
    {
        try
        {
            var work = Task.Run(async () =>
            {
                if (Manager() is not { } m) return false;
                if (m.TetheringOperationalState == TetheringOperationalState.On) return true;
                var result = await m.StartTetheringAsync();
                return result.Status == TetheringOperationStatus.Success;
            });
            if (await Task.WhenAny(work, Task.Delay(EnableTimeout)) != work)
            {
                // Windows is still working on it: don't leave an unobserved fault, and report what the hotspot really is now.
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return await GetStateAsync() == HotspotState.On;
            }
            return await work;
        }
        catch (Exception ex) { AppLog.Write($"hotspot: no se pudo activar ({ex.GetType().Name})"); return false; }
    }

    public void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write($"hotspot: no se pudo abrir la configuración ({ex.GetType().Name})"); }
    }
}

/// <summary>
/// Decides what to tell the user when a search ends without speakers. One notice per "no speakers" episode
/// (until a search finds something), and nothing is changed without the user pressing a button.
/// </summary>
public sealed class HotspotAdvisor(IHotspotController hotspot)
{
    private bool _asked;
    private int _generation;

    public IHotspotController Controller => hotspot;

    /// <summary>A search found speakers: the next empty search may notify again (and a check still in flight is dropped).</summary>
    public void Reset() { _asked = false; _generation++; }

    /// <summary>Call after an empty search. Null = nothing new to show.</summary>
    public async Task<HotspotPrompt?> CheckAfterEmptySearchAsync()
    {
        if (_asked) return null;
        _asked = true; // claimed before the await: two overlapping checks cannot both notify
        int generation = _generation;
        var state = await hotspot.GetStateAsync();
        if (generation != _generation) return null; // speakers showed up meanwhile; Reset already re-armed the notice
        if (state == HotspotState.Changing) { _asked = false; return null; } // coming up by itself (e.g. at sign-in): look again next time
        return state switch
        {
            HotspotState.Off => HotspotPrompt.Offer,
            HotspotState.On => HotspotPrompt.AlreadyOn,
            _ => HotspotPrompt.Unknown, // not knowing is not the same as "off"
        };
    }

    public async Task<HotspotPrompt> EnableAsync() => await hotspot.EnableAsync() ? HotspotPrompt.Enabled : HotspotPrompt.Failed;
}
