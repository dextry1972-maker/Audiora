using NAudio.Wave;

namespace SonosFlow.Poc.Audio;

/// <summary>Silence with an on-demand faded 2 kHz beep.</summary>
public sealed class BeepProvider(int sampleRate, double toneHz = 2000, double beepSeconds = 0.03, float amplitude = 0.3f) : ISampleProvider
{
    private readonly int _beepFrames = (int)(sampleRate * beepSeconds);
    private int _remaining = -1;
    private int _pos;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

    public void Trigger() { _pos = 0; Volatile.Write(ref _remaining, _beepFrames); }

    public long Reads;
    public long BeepFramesPlayed;

    public int Read(float[] buffer, int offset, int count)
    {
        Reads++;
        for (int i = 0; i < count; i += 2)
        {
            float s = 0;
            if (_remaining > 0)
            {
                double fade = Math.Min(1.0, Math.Min(_pos, _remaining) / (sampleRate * 0.003));
                s = (float)(amplitude * fade * Math.Sin(2 * Math.PI * toneHz * _pos / sampleRate));
                _pos++;
                _remaining--;
                BeepFramesPlayed++;
            }
            buffer[offset + i] = s;
            buffer[offset + i + 1] = s;
        }
        return count;
    }
}
