using NAudio.CoreAudioApi;

namespace SonosFlow.Core.Audio;

public sealed record OutputDeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (predeterminado)" : Name;
}

/// <summary>Lists the Windows output devices whose mix can be captured.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<OutputDeviceInfo> ListOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch (System.Runtime.InteropServices.COMException) { }

        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new OutputDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId))
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name)
            .ToList();
    }

    // Virtual outputs that play nowhere (Steam's only sounds during a Remote Play session). Left out on purpose:
    // VB-Audio CABLE Input and Voicemeeter, which are commonly listened to or routed to the speakers.
    private static readonly string[] SilentOutputNames = ["Steam Streaming"];

    public static bool IsSilentOutputName(string name) =>
        SilentOutputNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An active output nobody hears (a known virtual device), never the system default; null when this PC has none.
    /// There is deliberately no "except the output the app is on": Windows keeps the per-application output per executable,
    /// so a browser that was just restarted is BORN on the silent output, and excluding that one left no destination at all
    /// (the redirect then fell back to muting the silent output itself: the 02/11/2026 incident).
    /// </summary>
    public static OutputDeviceInfo? FindSilentOutput() => PickSilentOutput(ListOutputs());

    internal static OutputDeviceInfo? PickSilentOutput(IEnumerable<OutputDeviceInfo> outputs) =>
        outputs.FirstOrDefault(d => !d.IsDefault && IsSilentOutputName(d.Name));

    /// <summary>The device with <paramref name="id"/>, or the current default when null/missing.</summary>
    public static MMDevice Resolve(string? id)
    {
        var enumerator = new MMDeviceEnumerator();
        if (id is not null)
        {
            try
            {
                var d = enumerator.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
}
