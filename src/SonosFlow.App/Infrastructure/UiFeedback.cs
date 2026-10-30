using System.Diagnostics;
using System.Media;

namespace SonosFlow.App.Infrastructure;

/// <summary>A quiet generated confirmation, never a recording or a system-volume adjustment.</summary>
public sealed class UiFeedback : IDisposable
{
    private readonly MemoryStream _wave = new(CreateWave(), writable: false);
    private SoundPlayer? _player;
    private long _lastPlayed = long.MinValue;

    public static bool Allowed(bool enabled, bool streaming, bool otherInstance = false) => enabled && !streaming && !otherInstance;

    public void Confirm(bool enabled, bool streaming)
    {
        if (!Allowed(enabled, streaming, !AppSettings.PersistenceEnabled && OtherInstanceExists())) { Stop(); return; }
        long now = Environment.TickCount64;
        if (_lastPlayed != long.MinValue && now - _lastPlayed < 140) return;
        _lastPlayed = now;
        try
        {
            if (_player is null) { _player = new SoundPlayer(_wave); _player.Load(); }
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { Stop(); } // Feedback must not interfere with a setting or show a modal error.
    }

    private static bool OtherInstanceExists()
    {
        try
        {
            var processes = Process.GetProcessesByName("SonosFlow");
            try { return processes.Any(p => p.Id != Environment.ProcessId); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
    }
    public void Stop() { try { _player?.Stop(); } catch (InvalidOperationException) { } }
    public void Dispose() { Stop(); _player?.Dispose(); _wave.Dispose(); }

    public static byte[] CreateWave()
    {
        const int rate = 48000, frames = 2304;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 2);
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)rate;
            double envelope = Math.Min(1, t / 0.002) * Math.Exp(-t / 0.009) * Math.Pow(1 - i / (double)(frames - 1), 2);
            double sample = 0.035 * envelope * (0.8 * Math.Sin(2 * Math.PI * 1200 * t) + 0.2 * Math.Sin(2 * Math.PI * 1800 * t));
            writer.Write((short)Math.Round(sample * short.MaxValue));
        }
        return stream.ToArray();
    }
}
