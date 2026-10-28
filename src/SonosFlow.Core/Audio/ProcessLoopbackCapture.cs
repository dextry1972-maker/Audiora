using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SonosFlow.Core.Audio;

/// <summary>
/// Captures only the audio rendered by one process and its child processes (Windows 10 2004+),
/// on whatever output device that process uses. Other applications are not captured and the
/// target keeps playing through its usual output. Implements NAudio's <see cref="IWaveIn"/>.
/// Uses ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    private const uint BufferFlagsSilent = 0x2;
    private const uint BufferFlagsDataDiscontinuity = 0x1;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    private readonly int _processId;
    private readonly bool _includeTree;
    private IAudioClient? _client;
    private IAudioCaptureClient? _captureClient;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private AutoResetEvent? _event;

    public WaveFormat WaveFormat { get; set; }
    public int ProcessId => _processId;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <param name="format">PCM format to receive; Windows converts the app's audio to it.</param>
    public ProcessLoopbackCapture(int processId, WaveFormat format, bool includeProcessTree = true)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("La captura por aplicación requiere Windows 10 versión 2004 o posterior.");
        _processId = processId;
        _includeTree = includeProcessTree;
        WaveFormat = format;
    }

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public void StartRecording()
    {
        if (_thread is not null) throw new InvalidOperationException("Ya se está capturando.");
        _client = Activate(_processId, _includeTree);

        var format = WaveFormat;
        var fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(new WaveFormatEx
            {
                wFormatTag = 1, // PCM
                nChannels = (ushort)format.Channels,
                nSamplesPerSec = (uint)format.SampleRate,
                nAvgBytesPerSec = (uint)format.AverageBytesPerSecond,
                nBlockAlign = (ushort)format.BlockAlign,
                wBitsPerSample = (ushort)format.BitsPerSample,
                cbSize = 0,
            }, fmtPtr, false);

            const long bufferDuration100ns = 1_000_000; // 100 ms
            Check(_client.Initialize(0 /* shared */,
                StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                bufferDuration100ns, 0, fmtPtr, IntPtr.Zero), "IAudioClient.Initialize");
        }
        finally
        {
            Marshal.FreeHGlobal(fmtPtr);
        }

        var iid = IidAudioCaptureClient;
        Check(_client.GetService(ref iid, out var captureObj), "IAudioClient.GetService");
        _captureClient = (IAudioCaptureClient)captureObj;

        _event = new AutoResetEvent(false);
        Check(_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
        Check(_client.Start(), "IAudioClient.Start");

        _stopRequested = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = $"ProcessLoopback-{_processId}" };
        _thread.Start();
    }

    public void StopRecording()
    {
        _stopRequested = true;
        _event?.Set();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(1000);
        _thread = null;
    }

    private long _discontinuities;

    /// <summary>Packets Windows flagged as not continuous with the previous one (a gap in the captured audio).</summary>
    public long Discontinuities => Interlocked.Read(ref _discontinuities);

    private void CaptureLoop()
    {
        Exception? error = null;
        var buffer = new byte[WaveFormat.AverageBytesPerSecond / 10];
        int blockAlign = WaveFormat.BlockAlign;
        // Real-time scheduling, as in Microsoft's ApplicationLoopback sample (best effort: works without it).
        int taskIndex = 0;
        var mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            while (!_stopRequested)
            {
                _event!.WaitOne(200);
                if (_stopRequested) break;
                while (true)
                {
                    Check(_captureClient!.GetNextPacketSize(out var packetFrames), "GetNextPacketSize");
                    if (packetFrames == 0) break;
                    Check(_captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _), "GetBuffer");
                    int bytes = (int)frames * blockAlign;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((flags & BufferFlagsDataDiscontinuity) != 0) Interlocked.Increment(ref _discontinuities);
                    if ((flags & BufferFlagsSilent) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    Check(_captureClient.ReleaseBuffer(frames), "ReleaseBuffer");
                    if (bytes > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            error = ex;
        }
        finally
        {
            try { _client?.Stop(); } catch (COMException) { }
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    private static IAudioClient Activate(int processId, bool includeTree)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = includeTree ? 0 : 1, // INCLUDE / EXCLUDE target process tree
        };
        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = 0x41 /* VT_BLOB */, cbSize = (uint)paramsSize, pBlobData = paramsPtr }, propPtr, false);

            var handler = new ActivationHandler();
            var iid = IidAudioClient;
            Check(ActivateAudioInterfaceAsync(VirtualProcessLoopbackDevice, ref iid, propPtr, handler, out _), "ActivateAudioInterfaceAsync");
            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Windows no respondió al activar la captura de la aplicación.");
            return handler.Completed.Result;
        }
        finally
        {
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} falló (0x{hr:X8})", hr);
    }

    public void Dispose()
    {
        StopRecording();
        if (_captureClient is not null) { Marshal.ReleaseComObject(_captureClient); _captureClient = null; }
        if (_client is not null) { Marshal.ReleaseComObject(_client); _client = null; }
        _event?.Dispose();
        _event = null;
    }

    // ── Interop ────────────────────────────────────────────────

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marker: the completion callback may arrive on any thread.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Completed => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out var hr, out var iface);
                if (hr < 0) _tcs.TrySetException(new COMException($"No se pudo activar la captura de la aplicación (0x{hr:X8})", hr));
                else _tcs.TrySetResult((IAudioClient)iface);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
