using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonosFlow.Poc.Audio;

public sealed record LatencyResult(string MicName, IReadOnlyList<double> SamplesSeconds, int ClicksSent, int ClicksSeenLoopback)
{
    public string Diagnostics { get; init; } = "";
}

/// <summary>
/// Acoustic end-to-end latency: plays short 2 kHz beeps on the PC output, timestamps them in the
/// loopback capture (what we send) and in the microphone (what the Sonos actually plays), on one clock.
/// </summary>
public sealed class LatencyTest
{
    private const double ToneHz = 2000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LoopbackSource _source;
    private readonly ToneOnsetDetector _loopbackDetector;
    private ToneOnsetDetector? _micDetector;

    public LatencyTest(LoopbackSource source)
    {
        _source = source;
        _loopbackDetector = new ToneOnsetDetector(source.OutputFormat.SampleRate, ToneHz, minTonality: 0.6, minLevel: 0.003, baselineFactor: 8);
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    public async Task<LatencyResult> RunAsync(int clicks, TimeSpan spacing, CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        using var micCapture = new WasapiCapture(mic, true, 20);
        var micFmt = micCapture.WaveFormat;
        _micDetector = new ToneOnsetDetector(micFmt.SampleRate, ToneHz, minTonality: 0.3, minLevel: 0.0002, baselineFactor: 12);
        micCapture.DataAvailable += (_, e) => OnMic(e, micFmt);

        _source.PcmAvailable += OnLoopback;
        var beeper = new BeepProvider(_source.OutputFormat.SampleRate);
        using var output = new WasapiOut(_source.Device, AudioClientShareMode.Shared, true, 30);
        output.Init(beeper);
        output.Play();
        micCapture.StartRecording();
        try
        {
            // Coarse pass: one beep, wait for it to come back so later beeps can be paired unambiguously.
            beeper.Trigger();
            double coarse = await WaitForFirstPairAsync(TimeSpan.FromSeconds(25), ct);
            if (double.IsNaN(coarse))
                return new LatencyResult(mic.FriendlyName, [], 1, _loopbackDetector.Onsets.Count) { Diagnostics = Describe(beeper, micFmt) };

            for (int i = 0; i < clicks; i++)
            {
                await Task.Delay(spacing, ct);
                beeper.Trigger();
            }
            await Task.Delay(TimeSpan.FromSeconds(coarse + 3), ct);

            var samples = Pair(coarse, tolerance: Math.Min(1.0, spacing.TotalSeconds / 2 - 0.1));
            return new LatencyResult(mic.FriendlyName, samples, clicks + 1, _loopbackDetector.Onsets.Count) { Diagnostics = Describe(beeper, micFmt) };
        }
        finally
        {
            _source.PcmAvailable -= OnLoopback;
            micCapture.StopRecording();
            output.Stop();
        }
    }

    private string Describe(BeepProvider beeper, WaveFormat micFmt)
    {
        lock (this)
        {
            string D(ToneOnsetDetector d) =>
                $"ventanas={d.Windows} rmsMáx={d.MaxRms:F4} (tonalidad {d.TonalityAtMaxRms:F2}) tonalidadMáx≥nivel={d.MaxTonalityAboveLevel:F2} detecciones={d.Onsets.Count}";
            return $"beeper: lecturas={beeper.Reads} muestrasPitido={beeper.BeepFramesPlayed}\n" +
                   $"loopback: {D(_loopbackDetector)}\n" +
                   $"mic ({micFmt.SampleRate} Hz, {micFmt.BitsPerSample} bit, {micFmt.Encoding}, {micFmt.Channels} can.): {D(_micDetector!)}";
        }
    }

    private async Task<double> WaitForFirstPairAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Now + timeout.TotalSeconds;
        while (Now < deadline)
        {
            await Task.Delay(100, ct);
            lock (this)
            {
                if (_loopbackDetector.Onsets.Count == 0) continue;
                double tL = _loopbackDetector.Onsets[0];
                var tM = _micDetector!.Onsets.FirstOrDefault(t => t > tL + 0.02, double.NaN);
                if (!double.IsNaN(tM)) return tM - tL;
            }
        }
        return double.NaN;
    }

    private List<double> Pair(double expected, double tolerance)
    {
        lock (this)
        {
            var result = new List<double>();
            foreach (var tL in _loopbackDetector.Onsets)
            {
                var best = _micDetector!.Onsets
                    .Select(tM => tM - tL)
                    .Where(d => Math.Abs(d - expected) <= tolerance)
                    .OrderBy(d => Math.Abs(d - expected))
                    .Cast<double?>()
                    .FirstOrDefault();
                if (best is double d) result.Add(d);
            }
            return result;
        }
    }

    private void OnLoopback(ReadOnlyMemory<byte> pcm)
    {
        // Callback time ≈ end of this block; the block's first sample is that much earlier.
        double end = Now;
        int frames = pcm.Length / 4;
        var mono = new float[frames];
        var span = pcm.Span;
        for (int f = 0; f < frames; f++)
            mono[f] = (BitConverter.ToInt16(span.Slice(f * 4, 2)) + BitConverter.ToInt16(span.Slice(f * 4 + 2, 2))) / 65536f;
        lock (this) _loopbackDetector.Process(mono, end - (double)frames / _source.OutputFormat.SampleRate);
    }

    private void OnMic(WaveInEventArgs e, WaveFormat fmt)
    {
        double end = Now;
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * fmt.Channels);
        bool isFloat = fmt.BitsPerSample == 32; // WASAPI shared-mode capture is float32 or PCM16 in practice
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            int o = f * fmt.Channels * bytesPerSample;
            mono[f] = isFloat ? BitConverter.ToSingle(e.Buffer, o) : BitConverter.ToInt16(e.Buffer, o) / 32768f;
        }
        lock (this) _micDetector!.Process(mono, end - (double)frames / fmt.SampleRate);
    }
}
