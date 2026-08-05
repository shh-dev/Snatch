using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Snatch;

public static class Capture
{
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);

    // ---- draw the live mouse cursor into a captured bitmap (CopyFromScreen omits it) ----
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int istep, IntPtr brush, int flags);
    private const int CURSOR_SHOWING = 1, DI_NORMAL = 3;

    /// <summary>Draw the current mouse cursor onto <paramref name="g"/>, with the bitmap's screen origin at (originX, originY) physical px.</summary>
    public static void DrawCursor(Graphics g, int originX, int originY)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero) return;

            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            int x = ci.ptScreenPos.X - originX - hx;
            int y = ci.ptScreenPos.Y - originY - hy;
            IntPtr hdc = g.GetHdc();
            try { DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
            finally { g.ReleaseHdc(); }
        }
        catch { /* cursor draw is best-effort */ }
    }

    /// <summary>Snapshot the entire virtual desktop (all monitors) in physical pixels.</summary>
    public static Bitmap GrabVirtualScreen(out int originX, out int originY)
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        originX = vs.X; originY = vs.Y;
        var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(vs.X, vs.Y, 0, 0, new Size(vs.Width, vs.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>Snapshot a single physical-pixel rectangle (e.g. one monitor).</summary>
    public static Bitmap GrabRegion(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var h = bmp.GetHbitmap();
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }

    /// <summary>Crop a region (absolute physical px) out of an already-grabbed full snapshot, save PNG.</summary>
    public static string? SaveCrop(Bitmap full, int originX, int originY, int absX, int absY, int w, int h, Settings s, string stamp)
    {
        if (w < 3 || h < 3) return null;
        try
        {
            int sx = Math.Max(0, absX - originX);
            int sy = Math.Max(0, absY - originY);
            w = Math.Min(w, full.Width - sx);
            h = Math.Min(h, full.Height - sy);
            if (w < 3 || h < 3) return null;

            var dir = ScreenshotFolder(s);
            Directory.CreateDirectory(dir);
            using var crop = full.Clone(new Rectangle(sx, sy, w, h), full.PixelFormat);
            var path = Unique(Path.Combine(dir, $"스크린샷 {stamp}.png"));
            crop.Save(path, ImageFormat.Png);
            return path;
        }
        catch { return null; }
    }

    public static string ScreenshotFolder(Settings s)
    {
        if (!string.IsNullOrWhiteSpace(s.ScreenshotFolder)) return s.ScreenshotFolder!;
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pics, "Screenshots");
    }

    /// <summary>Capture a physical-pixel rectangle of the screen, save PNG, return path (or null).</summary>
    public static string? SaveRegion(int x, int y, int w, int h, Settings s, string stamp)
    {
        if (w < 3 || h < 3) return null;
        try
        {
            var dir = ScreenshotFolder(s);
            Directory.CreateDirectory(dir);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                if (s.CaptureCursor) DrawCursor(g, x, y);
            }

            var path = Path.Combine(dir, $"스크린샷 {stamp}.png");
            path = Unique(path);
            bmp.Save(path, ImageFormat.Png);
            return path;
        }
        catch { return null; }
    }

    private static string Unique(string dest)
    {
        if (!File.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest)!;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 1; ; i++)
        {
            var cand = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(cand)) return cand;
        }
    }
}
