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

    /// <summary>
    /// An active output nobody hears (a known virtual device), never the system default nor
    /// <paramref name="excludeId"/>; null when this PC has none.
    /// </summary>
    public static OutputDeviceInfo? FindSilentOutput(string? excludeId) =>
        ListOutputs().FirstOrDefault(d => !d.IsDefault && d.Id != excludeId
            && SilentOutputNames.Any(n => d.Name.Contains(n, StringComparison.OrdinalIgnoreCase)));

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
