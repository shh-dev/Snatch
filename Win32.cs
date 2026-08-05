using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Snatch;

public static class Win32
{
    // ---- Foreground (active) window bounds ----
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>Active window rect in physical px, or null. Skips our own windows.</summary>
    public static (int x, int y, int w, int h)? ActiveWindowBounds()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return null; // 우리 창은 무시

        // DWM 실제 프레임(그림자 제외) 우선, 실패 시 GetWindowRect
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0 || r.Right <= r.Left)
            if (!GetWindowRect(hwnd, out r)) return null;

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 5 || h < 5) return null;
        return (r.Left, r.Top, w, h);
    }

    // ---- Enumerate visible top-level windows (z-order, top first) for window-pick mode ----
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    private const int DWMWA_CLOAKED = 14;

    public static List<(int x, int y, int w, int h)> VisibleTopWindows()
    {
        var list = new List<(int, int, int, int)>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cl, sizeof(int)) == 0 && cl != 0) return true; // 가상데스크탑 등 숨김

            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0 || r.Right <= r.Left)
                if (!GetWindowRect(h, out r)) return true;

            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 40 || ht < 40) return true; // 작은 도구/팝업 제외
            list.Add((r.Left, r.Top, w, ht));
            return true;
        }, IntPtr.Zero);
        return list; // EnumWindows = z-order 위→아래
    }

    // ---- Known Folder: Downloads ----
    private static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public static string GetDownloadsFolder()
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(FOLDERID_Downloads, 0, IntPtr.Zero, out p) == 0)
                return Marshal.PtrToStringUni(p) ?? Fallback();
        }
        catch { /* ignore */ }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
        return Fallback();

        static string Fallback() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // ---- File icon extraction ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SMALLICON = 0x1;

    public static BitmapSource? GetFileIcon(string path, bool large = true)
    {
        var shfi = new SHFILEINFO();
        IntPtr res = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON));
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { DestroyIcon(shfi.hIcon); }
    }

    public static string HumanSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {u[i]}" : $"{v:0.#} {u[i]}";
    }
}
