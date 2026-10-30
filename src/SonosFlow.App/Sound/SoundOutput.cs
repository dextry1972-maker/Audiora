using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.Sound;

public interface ISoundOutput : IDisposable
{
    /// <summary>Sounds still playing.</summary>
    int ActiveVoices { get; }
    /// <summary>
    /// Plays a mono 48 kHz buffer once at <paramref name="gain"/>; never throws. <paramref name="veto"/> is asked with the
    /// device that will really be used (the chosen one, or the default if it is gone) just before the sound starts.
    /// </summary>
    void Play(float[] mono, float gain, string? deviceId, Func<string, bool>? veto = null);
    /// <summary>Drops every sound still playing (a stream is starting and would capture them).</summary>
    void CancelAll();
}

/// <summary>Soft ceiling for the mix of overlapping sounds: never reaches full scale, so the engine never clips.</summary>
public static class SoundLimiter
{
    public static float Limit(float x)
    {
        float a = MathF.Abs(x);
        return a <= 0.8f ? x : MathF.CopySign(0.8f + 0.19f * MathF.Tanh((a - 0.8f) / 0.19f), x);
    }
}

/// <summary>
/// Plays the effects on a local render endpoint with one shared-mode WASAPI client and a mixer, so overlapping
/// sounds are cheap. Opened on the first sound, closed after a few idle seconds (nothing runs between sounds),
/// reopened if the chosen device changes, and parked for a while if the device is gone or fails.
/// It is a separate render client in Audiora's own process: it never touches the capture, the buffers or the
/// redirection of the streaming engine.
/// </summary>
public sealed class WasapiEffectOutput : ISoundOutput
{
    private const int IdleCloseMs = 3000, FailureBackoffMs = 10000, LatencyMs = 80;
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(EnchantedSynth.SampleRate, 2);

    private readonly object _gate = new();
    private readonly Timer _idle;
    private MixingSampleProvider? _mixer;
    private WasapiOut? _out;
    private string? _openId;
    private MMDevice? _device;
    private long _lastActivity, _failedUntil;
    private int _voices;
    private bool _disposed, _failureLogged;

    public WasapiEffectOutput() => _idle = new Timer(_ => CloseIfIdle(), null, 1000, 1000);

    public int ActiveVoices => Volatile.Read(ref _voices);

    public void Play(float[] mono, float gain, string? deviceId, Func<string, bool>? veto = null)
    {
        lock (_gate)
        {
            if (_disposed || Environment.TickCount64 < _failedUntil) return;
            try
            {
                var device = AudioDevices.Resolve(deviceId); // the default when the chosen device is gone
                if (veto?.Invoke(device.ID) == true) { device.Dispose(); return; } // it would be captured: not even a moment of it
                if (_out is not null && _openId != device.ID) CloseLocked();
                if (_out is null) OpenLocked(device); else device.Dispose();
                Interlocked.Increment(ref _voices);
                _mixer!.AddMixerInput(new Voice(mono, gain));
                _lastActivity = Environment.TickCount64;
                _failureLogged = false;
            }
            catch (Exception ex) // COM/driver errors of any kind: a missing sound must never reach the user
            {
                FailLocked(ex);
            }
        }
    }

    private void OpenLocked(MMDevice device)
    {
        WasapiOut? output = null;
        try
        {
            var mixer = new MixingSampleProvider(Format) { ReadFully = true };
            mixer.MixerInputEnded += (s, _) => { if (ReferenceEquals(s, _mixer)) Interlocked.Decrement(ref _voices); };
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
            output.PlaybackStopped += OnStopped;
            output.Init(new LimitedMix(mixer).ToWaveProvider());
            output.Play();
            _mixer = mixer;
            _out = output;
            _device = device;
            _openId = device.ID;
            Volatile.Write(ref _voices, 0);
        }
        catch
        {
            if (output is not null) { output.PlaybackStopped -= OnStopped; try { output.Dispose(); } catch (Exception) { /* nothing to free */ } }
            device.Dispose();
            throw;
        }
    }

    // On the pool, never while the playback thread holds our lock: Stop/Dispose may wait for that thread.
    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(sender, _out)) return;
                FailLocked(e.Exception); // device unplugged or driver reset
            }
        });
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            _mixer?.RemoveAllMixerInputs();
            Volatile.Write(ref _voices, 0);
        }
    }

    private void FailLocked(Exception ex)
    {
        CloseLocked();
        _failedUntil = Environment.TickCount64 + FailureBackoffMs;
        if (_failureLogged) return;
        _failureLogged = true; // one line per outage, not one per sound
        AppLog.Write($"sonidos: salida local no disponible ({ex.GetType().Name}); se reintenta en {FailureBackoffMs / 1000} s");
    }

    private void CloseIfIdle()
    {
        lock (_gate)
            if (_out is not null && _voices <= 0 && Environment.TickCount64 - _lastActivity > IdleCloseMs) CloseLocked();
    }

    private void CloseLocked()
    {
        var output = _out;
        var device = _device;
        _out = null; _mixer = null; _openId = null; _device = null;
        Volatile.Write(ref _voices, 0);
        if (output is not null)
        {
            output.PlaybackStopped -= OnStopped;
            try { output.Stop(); } catch (Exception) { /* already gone */ }
            try { output.Dispose(); } catch (Exception) { /* already gone */ }
        }
        device?.Dispose();
    }

    private sealed class LimitedMix(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            for (int i = 0; i < n; i++) buffer[offset + i] = SoundLimiter.Limit(buffer[offset + i]);
            return n;
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        lock (_gate) { _disposed = true; CloseLocked(); }
    }

    private sealed class Voice(float[] mono, float gain) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat => Format;

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = Math.Min(count / 2, mono.Length - _pos);
            for (int i = 0; i < frames; i++)
            {
                float s = mono[_pos + i] * gain;
                buffer[offset + 2 * i] = s;
                buffer[offset + 2 * i + 1] = s;
            }
            _pos += frames;
            return frames * 2; // 0 when finished: the mixer drops it
        }
    }
}
