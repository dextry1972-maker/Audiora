using NAudio.Wave;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Diagnostics;

namespace SonosFlow.Core.Tests;

/// <summary>
/// The capture timing can be perfect while the CONTENT has a hole (the 02/11 incident: micro-cuts with every timing metric
/// clean). These tests feed known audio through the real conversion path and check the "zeros inside audio" measurement.
/// </summary>
public sealed class DropoutDetectorTests
{
    private const int Rate = 48000;

    private sealed class FakeCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(Rate, 16, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
        public void Push(short[] frames)
        {
            var bytes = new byte[frames.Length * 4];
            for (int i = 0; i < frames.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), frames[i]);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 4 + 2), frames[i]);
            }
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        }
    }

    /// <summary>A 440 Hz tone that never touches zero (offset) so ordinary zero crossings are not mistaken for holes.</summary>
    private static short[] Tone(int frames, int from = 0)
    {
        var s = new short[frames];
        for (int i = 0; i < frames; i++) s[i] = (short)(3000 + 6000 * Math.Sin(2 * Math.PI * 440 * (from + i) / Rate));
        return s;
    }

    private static (int Count, double MaxMs) Measure(Action<FakeCapture> feed)
    {
        using var diag = new StreamDiagnostics(null, Rate, "test");
        var capture = new FakeCapture();
        using var source = new LoopbackSource(capture, "test") { Diagnostics = diag };
        source.PcmAvailable += _ => { };
        feed(capture);
        var second = diag.Tick(0, null, 192);
        return ((int)second.Dropouts, second.DropoutMaxMs);
    }

    [Fact]
    public void A_hole_of_zeros_inside_audio_is_counted_with_its_length()
    {
        var (count, max) = Measure(c =>
        {
            c.Push(Tone(4800));                      // 100 ms of audio
            c.Push(new short[240]);                  // 5 ms of exact zeros
            c.Push(Tone(4800, 4800 + 240));          // audio again
        });
        Assert.Equal(1, count);
        Assert.InRange(max, 4.9, 5.1);
    }

    [Fact]
    public void A_hole_that_spans_two_blocks_is_still_one_hole()
    {
        var (count, max) = Measure(c =>
        {
            c.Push(Tone(2400));
            c.Push(new short[240]);
            c.Push(new short[240]);                  // 10 ms of zeros split over two packets
            c.Push(Tone(2400));
        });
        Assert.Equal(1, count);
        Assert.InRange(max, 9.9, 10.1);
    }

    [Fact]
    public void Long_silence_leading_silence_and_trailing_silence_are_not_holes()
    {
        var (count, _) = Measure(c =>
        {
            c.Push(new short[4800]);                 // starts silent (nothing played yet)
            c.Push(Tone(4800));
            c.Push(new short[48000]);                // a full second of silence: a real pause
            c.Push(Tone(4800));
            c.Push(new short[2400]);                 // ends in silence: nothing followed
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public void Ordinary_audio_with_zero_crossings_and_one_off_zero_samples_reports_nothing()
    {
        var (count, _) = Measure(c =>
        {
            var wave = new short[9600];
            for (int i = 0; i < wave.Length; i++) wave[i] = (short)(8000 * Math.Sin(2 * Math.PI * 1000 * i / Rate)); // crosses zero every 24 frames
            c.Push(wave);
            var spiky = Tone(2400);
            spiky[1000] = 0; spiky[1500] = 0;        // isolated zero samples
            c.Push(spiky);
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public void A_flaw_shorter_than_the_threshold_is_ignored()
    {
        var (count, _) = Measure(c =>
        {
            c.Push(Tone(4800));
            c.Push(new short[48]);                   // 1 ms
            c.Push(Tone(4800, 4848));
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public void The_measurement_never_changes_the_audio_that_is_sent()
    {
        using var diag = new StreamDiagnostics(null, Rate, "test");
        var capture = new FakeCapture();
        using var source = new LoopbackSource(capture, "test") { Diagnostics = diag };
        var sent = new List<byte>();
        source.PcmAvailable += pcm => sent.AddRange(pcm.ToArray());
        var input = new List<short>(Tone(2400));
        input.AddRange(new short[240]);
        input.AddRange(Tone(2400));
        capture.Push([.. input]);
        Assert.Equal(input.Count * 4, sent.Count);
        for (int i = 0; i < input.Count; i++) Assert.Equal(input[i], BitConverter.ToInt16(sent.ToArray(), i * 4));
        Assert.Equal(1, diag.Tick(0, null, 192).Dropouts);
    }
}
