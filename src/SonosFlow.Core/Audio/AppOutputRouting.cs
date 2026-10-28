using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SonosFlow.Core.Audio;

/// <summary>
/// The per-application output device of Windows (Settings → Sound → Volume mixer → app → Output device), read and
/// written through Windows.Media.Internal.AudioPolicyConfig — the undocumented factory behind that page, also used by
/// EarTrumpet. Windows persists the choice per application (not per process), so every change must be undone:
/// see <see cref="AppOutputRedirect"/>. Only the 21H2+ interface layout is supported; older builds report unsupported.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppOutputRouting
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private static readonly Guid Iid = new("ab3d4648-e242-459f-b02f-541c70306324"); // IAudioPolicyConfigFactory, build 21390+

    // vtable: IUnknown (3) + IInspectable (3) + 19 methods we don't use, then:
    private const int SlotSetPersisted = 25, SlotGetPersisted = 26;
    private const int ERender = 0;
    private const int ErrorNotFound = unchecked((int)0x80070490); // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
    private static readonly int[] Roles = [0 /* eConsole */, 1 /* eMultimedia */];

    // Device interface path of an MMDevice endpoint, the form the policy store uses.
    private const string Prefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 21390);

    /// <summary>The app's chosen output (MMDevice id), or null when it follows the system default.</summary>
    public static string? Get(int processId)
    {
        using var factory = new Factory();
        var get = factory.Method<GetFn>(SlotGetPersisted);
        int hr = get(factory.Ptr, (uint)processId, ERender, Roles[0], out var h);
        if (hr == ErrorNotFound) return null; // no per-app choice: follows the system default
        Check(hr);
        return FromPolicyId(TakeString(h));
    }

    /// <summary>Sends the app to <paramref name="deviceId"/> (an MMDevice id); null = back to the system default.</summary>
    public static void Set(int processId, string? deviceId)
    {
        using var factory = new Factory();
        var set = factory.Method<SetFn>(SlotSetPersisted);
        var h = IntPtr.Zero;
        if (deviceId is not null)
        {
            var policyId = Prefix + deviceId + RenderSuffix;
            Check(WindowsCreateString(policyId, policyId.Length, out h));
        }
        try
        {
            foreach (var role in Roles) Check(set(factory.Ptr, (uint)processId, ERender, role, h));
        }
        finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }

    /// <summary>MMDevice id inside a policy-store device id (<c>\\?\SWD#MMDEVAPI#{id}#{class}</c>); null when empty.</summary>
    public static string? FromPolicyId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) id = id[Prefix.Length..];
        int hash = id.IndexOf('#');
        return hash >= 0 ? id[..hash] : id;
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    private static string? TakeString(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out var length), (int)length); }
        finally { WindowsDeleteString(h); }
    }

    private sealed class Factory : IDisposable
    {
        public IntPtr Ptr { get; }

        public Factory()
        {
            Check(WindowsCreateString(ClassName, ClassName.Length, out var name));
            try
            {
                var iid = Iid;
                Check(RoGetActivationFactory(name, ref iid, out var ptr));
                Ptr = ptr;
            }
            finally { WindowsDeleteString(name); }
        }

        public T Method<T>(int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Ptr), slot * IntPtr.Size));

        public void Dispose() => Marshal.Release(Ptr);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFn(IntPtr self, uint processId, int flow, int role, out IntPtr deviceId);

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
