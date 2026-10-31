using NAudio.Wave;
using SonosFlow.Core.Audio;

namespace SonosFlow.Core.Tests;

public sealed class LoopbackSourceTests
{
    private sealed class Capture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(48000, 16, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() => End(null);
        public void End(Exception? error) => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public void Dispose() { }
    }

    [Fact]
    public void Unexpected_capture_end_without_exception_is_detected_instead_of_hidden_by_silence()
    {
        using var capture = new Capture();
        using var source = new LoopbackSource(capture, "simulated");
        source.Start();
        capture.End(null);
        Assert.IsType<IOException>(source.CaptureError);
    }

    [Fact]
    public void Capture_failure_preserves_the_original_error()
    {
        using var capture = new Capture();
        using var source = new LoopbackSource(capture, "simulated");
        source.Start();
        var failure = new InvalidOperationException("simulated device loss");
        capture.End(failure);
        capture.End(null);
        Assert.Same(failure, source.CaptureError);
    }

    [Fact]
    public void Planned_stop_is_not_reported_as_capture_failure()
    {
        using var capture = new Capture();
        using var source = new LoopbackSource(capture, "simulated");
        source.Start();
        source.Stop();
        Assert.Null(source.CaptureError);
    }
}
