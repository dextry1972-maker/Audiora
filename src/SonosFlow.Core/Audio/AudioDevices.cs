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
