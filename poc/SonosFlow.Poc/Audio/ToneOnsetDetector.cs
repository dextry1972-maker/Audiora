namespace SonosFlow.Poc.Audio;

/// <summary>
/// Detects the onset of a pure test tone with a Goertzel filter over short windows.
/// Tonality (tone power vs. total energy) keeps music and speech from triggering it.
/// </summary>
public sealed class ToneOnsetDetector(int sampleRate, double toneHz, double minTonality, double minLevel, double baselineFactor)
{
    private const double RefractorySeconds = 1.5;
    private readonly int _window = sampleRate / 200; // 5 ms
    private readonly double _coeff = 2 * Math.Cos(2 * Math.PI * toneHz / sampleRate);
    private readonly List<float> _pending = [];
    private double _pendingStartTime;
    private double _baseline = 1e-9;
    private double _lastOnset = double.NegativeInfinity;

    public List<double> Onsets { get; } = [];
    /// <summary>Diagnostics: strongest window seen (rms and tonality of that window).</summary>
    public double MaxRms { get; private set; }
    public double TonalityAtMaxRms { get; private set; }
    public double MaxTonalityAboveLevel { get; private set; }
    public long Windows { get; private set; }

    /// <param name="startTime">Time in seconds (shared clock) of the first sample in <paramref name="mono"/>.</param>
    public void Process(ReadOnlySpan<float> mono, double startTime)
    {
        if (_pending.Count == 0) _pendingStartTime = startTime;
        foreach (var s in mono) _pending.Add(s);

        int offset = 0;
        while (_pending.Count - offset >= _window)
        {
            double s1 = 0, s2 = 0, energy = 0;
            for (int i = 0; i < _window; i++)
            {
                double x = _pending[offset + i];
                energy += x * x;
                double s0 = x + _coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - _coeff * s1 * s2;
            double tonality = energy > 0 ? power / (energy * _window / 2.0) : 0;
            double rms = Math.Sqrt(energy / _window);
            double t = _pendingStartTime + (double)offset / sampleRate;
            Windows++;
            if (rms > MaxRms) { MaxRms = rms; TonalityAtMaxRms = tonality; }
            if (rms >= minLevel && tonality > MaxTonalityAboveLevel) MaxTonalityAboveLevel = tonality;

            bool hit = tonality >= minTonality && rms >= minLevel && power >= _baseline * baselineFactor
                       && t - _lastOnset >= RefractorySeconds;
            if (hit)
            {
                Onsets.Add(t);
                _lastOnset = t;
            }
            else if (t - _lastOnset > 0.2)
            {
                _baseline = _baseline * 0.98 + power * 0.02; // slow floor of tone-band power
            }
            offset += _window;
        }
        _pending.RemoveRange(0, offset);
        _pendingStartTime += (double)offset / sampleRate;
    }
}
