using NAudio.Dsp;

namespace SonosFlow.Core.Audio;

/// <summary>
/// Turns the PCM we actually stream into log-spaced spectrum bands (0..1) and stereo peak levels
/// for the UI. Runs on the capture thread; readers take the latest snapshot.
/// </summary>
public sealed class SpectrumAnalyzer
{
    private const int FftOrder = 11; // 2048 points
    private const int FftSize = 1 << FftOrder;
    private const int Hop = FftSize / 2;
    private const double FloorDb = -80, CeilingDb = -12;

    private readonly int _sampleRate;
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly (int From, int To)[] _bandBins;
    private readonly float[] _bands;
    private readonly object _lock = new();
    private int _writePos;
    private int _sinceLast;
    private float _peakL, _peakR;

    public int BandCount { get; }

    public SpectrumAnalyzer(int sampleRate, int bandCount = 48, double minHz = 40, double maxHz = 16000)
    {
        _sampleRate = sampleRate;
        BandCount = bandCount;
        _bands = new float[bandCount];
        for (int i = 0; i < FftSize; i++) _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);

        _bandBins = new (int, int)[bandCount];
        double binHz = (double)sampleRate / FftSize;
        for (int b = 0; b < bandCount; b++)
        {
            double lo = minHz * Math.Pow(maxHz / minHz, (double)b / bandCount);
            double hi = minHz * Math.Pow(maxHz / minHz, (double)(b + 1) / bandCount);
            int from = Math.Max(1, (int)(lo / binHz));
            int to = Math.Max(from, (int)(hi / binHz));
            _bandBins[b] = (from, Math.Min(to, FftSize / 2 - 1));
        }
    }

    /// <summary>Feed 16-bit little-endian stereo PCM.</summary>
    public void Process(ReadOnlySpan<byte> pcm)
    {
        int frames = pcm.Length / 4;
        float pl = 0, pr = 0;
        for (int f = 0; f < frames; f++)
        {
            float l = BitConverter.ToInt16(pcm.Slice(f * 4, 2)) / 32768f;
            float r = BitConverter.ToInt16(pcm.Slice(f * 4 + 2, 2)) / 32768f;
            pl = MathF.Max(pl, MathF.Abs(l));
            pr = MathF.Max(pr, MathF.Abs(r));
            _ring[_writePos] = (l + r) * 0.5f;
            _writePos = (_writePos + 1) % FftSize;
            if (++_sinceLast >= Hop)
            {
                _sinceLast = 0;
                Analyze();
            }
        }
        lock (_lock)
        {
            _peakL = MathF.Max(_peakL, pl);
            _peakR = MathF.Max(_peakR, pr);
        }
    }

    private void Analyze()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _ring[(_writePos + i) % FftSize] * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftOrder, _fft);

        lock (_lock)
        {
            for (int b = 0; b < BandCount; b++)
            {
                var (from, to) = _bandBins[b];
                double max = 0;
                for (int k = from; k <= to; k++)
                    max = Math.Max(max, _fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                // NAudio's forward FFT scales by 1/N; ×2 for the one-sided spectrum, ×2 for the Hann gain.
                double db = 10 * Math.Log10(max * 16 + 1e-12);
                _bands[b] = (float)Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);
            }
        }
    }

    /// <summary>Copies the latest bands and returns peaks since the previous call (then resets them).</summary>
    public (float PeakL, float PeakR) Snapshot(float[] bands)
    {
        lock (_lock)
        {
            Array.Copy(_bands, bands, Math.Min(bands.Length, _bands.Length));
            var peaks = (_peakL, _peakR);
            _peakL = _peakR = 0;
            return peaks;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_bands);
            Array.Clear(_ring);
            _peakL = _peakR = 0;
        }
    }
}
