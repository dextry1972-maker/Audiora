using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonosFlow.Poc.Audio;

/// <summary>
/// Captures everything the default render device plays (WASAPI loopback) and emits it as
/// 16-bit stereo PCM at the device's sample rate. WASAPI delivers no packets while nothing is
/// playing, so a pacer fills those gaps with real-time silence to keep the HTTP stream alive.
/// </summary>
public sealed class LoopbackSource : IDisposable
{
    private const int AllowanceMs = 120;
    private const int PacerIntervalMs = 10;
    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WasapiLoopbackCapture _capture;
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
    public MMDevice Device { get; }
    public string DeviceName { get; }
    public string SourceFormatDescription { get; }

    /// <summary>Raised with converted PCM (16-bit LE stereo). Called on the capture or pacer thread.</summary>
    public event Action<ReadOnlyMemory<byte>>? PcmAvailable;

    public long CapturedBytes { get; private set; }
    public long SilenceBytes { get; private set; }
    /// <summary>Peak of the last captured block, 0..1. Real measurement of what was captured.</summary>
    public float LastPeak { get; private set; }
    public Exception? CaptureError { get; private set; }

    public LoopbackSource()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Device = device;
        DeviceName = device.FriendlyName;
        _capture = new WasapiLoopbackCapture(device);

        var fmt = _capture.WaveFormat;
        _sourceChannels = fmt.Channels;
        _sourceBytesPerSample = fmt.BitsPerSample / 8;
        _sourceIsFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                         || (fmt is WaveFormatExtensible ext && ext.SubFormat == KsDataFormatSubtypeIeeeFloat);
        SourceFormatDescription = $"{fmt.SampleRate} Hz, {fmt.BitsPerSample} bit {(_sourceIsFloat ? "float" : "PCM")}, {fmt.Channels} canales";

        if (!_sourceIsFloat && _sourceBytesPerSample != 2)
            throw new NotSupportedException($"Formato de captura no soportado: {SourceFormatDescription}");

        OutputFormat = new WaveFormat(fmt.SampleRate, 16, 2);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) => CaptureError ??= e.Exception;
    }

    public void Start()
    {
        _running = true;
        _clock.Restart();
        _emittedFrames = 0;
        _capture.StartRecording();
        _pacer = new Thread(PacerLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "SilencePacer" };
        _pacer.Start();
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
                _emittedFrames += frames;
                PcmAvailable?.Invoke(silence.AsMemory(0, bytes));
            }
        }
    }

    public void Dispose()
    {
        if (_running) Stop();
        _capture.Dispose();
    }
}
