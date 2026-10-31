using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonosFlow.Core.Audio;

/// <summary>
/// Captures everything an output device plays (WASAPI loopback) — or, with
/// <see cref="ForProcess"/>, only one application's audio — and emits it as
/// 16-bit stereo PCM at the capture's sample rate. WASAPI delivers no packets while nothing is
/// playing, so a pacer fills those gaps with real-time silence to keep the HTTP stream alive.
/// </summary>
public sealed class LoopbackSource : IDisposable
{
    private const int AllowanceMs = 120;
    private const int PacerIntervalMs = 10;
    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly IWaveIn _capture;
    private readonly object _emitLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _sourceChannels;
    private readonly bool _sourceIsFloat;
    private readonly int _sourceBytesPerSample;
    private Thread? _pacer;
    private volatile bool _running;
    private long _emittedFrames;
    private byte[] _convertBuffer = [];

    private long AllowanceFrames => OutputFormat.SampleRate * AllowanceMs / 1000;

    public WaveFormat OutputFormat { get; }
    /// <summary>Captured output device, or null when capturing a single application.</summary>
    public MMDevice? Device { get; }
    /// <summary>What is being captured: the device name or the application name.</summary>
    public string DeviceName { get; }
    public string SourceFormatDescription { get; }

    /// <summary>Raised with converted PCM (16-bit LE stereo). Called on the capture or pacer thread.</summary>
    public event Action<ReadOnlyMemory<byte>>? PcmAvailable;

    private static int _active;
    private bool _counted;

    /// <summary>Captures currently running in this process (diagnostics: must never exceed one per session).</summary>
    public static int ActiveCount => Volatile.Read(ref _active);

    /// <summary>Gaps Windows reported in the per-app capture (0 for whole-output capture).</summary>
    public long CaptureDiscontinuities =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && _capture is ProcessLoopbackCapture p ? p.Discontinuities : 0;

    /// <summary>Optional diagnostics (set by the session): capture timing and pacer fills. Measures, never changes anything.</summary>
    public SonosFlow.Core.Diagnostics.StreamDiagnostics? Diagnostics { get; set; }

    public long CapturedBytes { get; private set; }
    public long SilenceBytes { get; private set; }
    /// <summary>Peak of the last captured block, 0..1. Real measurement of what was captured.</summary>
    public float LastPeak { get; private set; }
    public Exception? CaptureError { get; private set; }

    /// <param name="device">Render endpoint to capture; null = the current default output.</param>
    public LoopbackSource(MMDevice? device = null)
    {
        if (device is null)
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        Device = device;
        DeviceName = device.FriendlyName;
        _capture = new WasapiLoopbackCapture(device);
        (_sourceChannels, _sourceBytesPerSample, _sourceIsFloat, SourceFormatDescription, OutputFormat) = Describe(_capture.WaveFormat);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    internal LoopbackSource(IWaveIn capture, string name)
    {
        DeviceName = name;
        _capture = capture;
        (_sourceChannels, _sourceBytesPerSample, _sourceIsFloat, SourceFormatDescription, OutputFormat) = Describe(_capture.WaveFormat);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>
    /// Only the audio of <paramref name="processId"/> and its child processes, delivered at
    /// <paramref name="sampleRate"/> (Windows resamples), so it can replace a device capture mid-stream.
    /// </summary>
    public static LoopbackSource ForProcess(int processId, string appName, int sampleRate) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? new(new ProcessLoopbackCapture(processId, new WaveFormat(sampleRate, 16, 2)), appName)
            : throw new PlatformNotSupportedException("La captura por aplicación requiere Windows 10 versión 2004 o posterior.");

    private static (int Channels, int BytesPerSample, bool IsFloat, string Description, WaveFormat Output) Describe(WaveFormat fmt)
    {
        int bytesPerSample = fmt.BitsPerSample / 8;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                       || (fmt is WaveFormatExtensible ext && ext.SubFormat == KsDataFormatSubtypeIeeeFloat);
        var description = $"{fmt.SampleRate} Hz, {fmt.BitsPerSample} bit {(isFloat ? "float" : "PCM")}, {fmt.Channels} ch";
        if (!isFloat && bytesPerSample != 2)
            throw new NotSupportedException($"Formato de captura no soportado: {description}");
        return (fmt.Channels, bytesPerSample, isFloat, description, new WaveFormat(fmt.SampleRate, 16, 2));
    }

    public void Start()
    {
        _running = true;
        _clock.Restart();
        _emittedFrames = 0;
        _capture.StartRecording();
        if (!_counted) { _counted = true; Interlocked.Increment(ref _active); }
        _pacer = new Thread(PacerLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "SilencePacer" };
        _pacer.Start();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_running) CaptureError ??= e.Exception ?? new IOException("La captura terminó inesperadamente.");
    }

    public void Stop()
    {
        _running = false;
        _capture.StopRecording();
        _pacer?.Join(500);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        int frames = e.BytesRecorded / (_sourceBytesPerSample * _sourceChannels);
        int outBytes = frames * 4;
        if (_convertBuffer.Length < outBytes) _convertBuffer = new byte[outBytes];

        float peak = 0f;
        var src = e.Buffer.AsSpan(0, e.BytesRecorded);
        var dst = _convertBuffer.AsSpan(0, outBytes);
        for (int f = 0; f < frames; f++)
        {
            float l = ReadSample(src, f, 0);
            float r = _sourceChannels > 1 ? ReadSample(src, f, 1) : l;
            peak = MathF.Max(peak, MathF.Max(MathF.Abs(l), MathF.Abs(r)));
            WriteInt16(dst, f * 4, l);
            WriteInt16(dst, f * 4 + 2, r);
        }

        Diagnostics?.CaptureBlock(frames, peak > 0.0003f); // above ≈ −70 dBFS counts as audio

        lock (_emitLock)
        {
            LastPeak = peak;
            CapturedBytes += outBytes;
            // Keep the frame accounting within ±allowance of wall time so device/QPC clock drift
            // never turns into inserted silence while audio is flowing.
            long wall = WallFrames();
            _emittedFrames = Math.Clamp(_emittedFrames + frames, wall - AllowanceFrames, wall + AllowanceFrames);
            PcmAvailable?.Invoke(_convertBuffer.AsMemory(0, outBytes));
        }
    }

    private float ReadSample(ReadOnlySpan<byte> src, int frame, int channel)
    {
        int offset = (frame * _sourceChannels + channel) * _sourceBytesPerSample;
        return _sourceIsFloat
            ? BitConverter.ToSingle(src.Slice(offset, 4))
            : BitConverter.ToInt16(src.Slice(offset, 2)) / 32768f;
    }

    private static void WriteInt16(Span<byte> dst, int offset, float sample)
    {
        short s = (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);
        BitConverter.TryWriteBytes(dst.Slice(offset, 2), s);
    }

    private long WallFrames() => _clock.ElapsedTicks * OutputFormat.SampleRate / Stopwatch.Frequency;

    private void PacerLoop()
    {
        var silence = new byte[OutputFormat.AverageBytesPerSecond]; // up to 1 s of zeros
        while (_running)
        {
            Thread.Sleep(PacerIntervalMs);
            lock (_emitLock)
            {
                // Only fill a real deficit: capture bursts arrive every ~50 ms, so a deficit
                // smaller than the allowance just means the next burst hasn't landed yet.
                long deficit = WallFrames() - AllowanceFrames - _emittedFrames;
                if (deficit <= 0) continue;

                int frames = (int)Math.Min(deficit, OutputFormat.SampleRate);
                int bytes = frames * OutputFormat.BlockAlign;
                LastPeak = 0f;
                SilenceBytes += bytes;
                Diagnostics?.PacerFill(bytes);
                _emittedFrames += frames;
                PcmAvailable?.Invoke(silence.AsMemory(0, bytes));
            }
        }
    }

    public void Dispose()
    {
        if (_running) Stop();
        _capture.Dispose();
        if (_counted) { _counted = false; Interlocked.Decrement(ref _active); }
    }
}
