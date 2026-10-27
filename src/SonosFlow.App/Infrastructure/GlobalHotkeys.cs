using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SonosFlow.App.Infrastructure;

public enum HotkeyAction { VolumeUp, VolumeDown, PauseResume, ToggleMini }

/// <summary>
/// System-wide shortcuts (Ctrl+Alt+…) through RegisterHotKey on a message-only window: they work while a game
/// or another app has the focus and never take the focus themselves. The media keys are left alone so they keep
/// controlling the player that is actually playing.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    public static readonly IReadOnlyList<(HotkeyAction Action, uint Key, string Label, bool Repeat)> All =
    [
        (HotkeyAction.VolumeUp, 0x26, "Ctrl+Alt+↑", true),
        (HotkeyAction.VolumeDown, 0x28, "Ctrl+Alt+↓", true),
        (HotkeyAction.PauseResume, 0x50, "Ctrl+Alt+P", false),
        (HotkeyAction.ToggleMini, 0x4F, "Ctrl+Alt+O", false), // Ctrl+Alt+M is commonly taken by other apps
    ];

    private readonly HwndSource _window;
    private readonly Action<HotkeyAction> _handler;
    private readonly List<int> _registered = [];

    public GlobalHotkeys(Action<HotkeyAction> handler)
    {
        _handler = handler;
        _window = new HwndSource(new HwndSourceParameters("AudioraHotkeys") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        _window.AddHook(WndProc);
    }

    /// <summary>Registers every shortcut; returns the labels of those another program already owns.</summary>
    public IReadOnlyList<string> Enable()
    {
        Disable();
        var taken = new List<string>();
        for (int id = 0; id < All.Count; id++)
        {
            var h = All[id];
            if (RegisterHotKey(_window.Handle, id, MOD_CONTROL | MOD_ALT | (h.Repeat ? 0 : MOD_NOREPEAT), h.Key)) _registered.Add(id);
            else taken.Add(h.Label);
        }
        return taken;
    }

    public void Disable()
    {
        foreach (var id in _registered) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() is var id && id >= 0 && id < All.Count)
        {
            handled = true;
            _handler(All[id].Action);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Disable();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
