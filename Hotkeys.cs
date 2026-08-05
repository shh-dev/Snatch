using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Snatch;

/// <summary>Registers global hotkeys via a hidden message window and raises events on press.</summary>
public class Hotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    public int LastError { get; private set; }
    public IntPtr Handle => _hwnd;
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Window _host;
    private IntPtr _hwnd;
    private HwndSource? _src;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public Hotkeys(Window host)
    {
        _host = host;
        var helper = new WindowInteropHelper(host);
        _hwnd = helper.EnsureHandle();
        _src = HwndSource.FromHwnd(_hwnd);
        _src?.AddHook(WndProc);
    }

    /// <summary>vk = virtual-key code. Returns false if the combo is already taken by another app.</summary>
    public bool Register(uint vk, bool ctrl, bool shift, bool alt, Action onPress)
    {
        int id = _nextId++;
        uint mods = (ctrl ? MOD_CONTROL : 0) | (shift ? MOD_SHIFT : 0) | (alt ? MOD_ALT : 0);
        if (!RegisterHotKey(_hwnd, id, mods, vk)) { LastError = Marshal.GetLastWin32Error(); return false; }
        _actions[id] = onPress;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var act))
        {
            handled = true;
            try { act(); } catch { /* ignore */ }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _src?.RemoveHook(WndProc);
    }
}
