using System.Drawing;
using System.Runtime.InteropServices;

namespace Snatch;

public static class IconExtract
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon? Extract(string file, int index)
    {
        var large = new IntPtr[1];
        uint n = ExtractIconEx(file, index, large, null, 1);
        if (n == 0 || large[0] == IntPtr.Zero) return null;
        try
        {
            // clone so we can free the native handle
            var ico = (Icon)Icon.FromHandle(large[0]).Clone();
            return ico;
        }
        catch { return null; }
        finally { DestroyIcon(large[0]); }
    }
}
