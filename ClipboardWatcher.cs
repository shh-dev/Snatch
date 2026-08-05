using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Snatch;

/// <summary>Payload of a clipboard change we care about.</summary>
public class ClipContent
{
    public BitmapSource? Image;   // non-null → image copied (thumbnail ok for display)
    public string? Text;          // non-null → text/file-list copied
    public string? ColorHex;      // non-null → picked a screen color (#RRGGBB)
    public string? ImagePath;     // if set, paste loads full-res image from this file (Image is just a thumb)
    public bool IsImage => Image != null;
    public bool IsColor => ColorHex != null;
}

/// <summary>
/// Listens for clipboard changes on a hidden message window and raises <see cref="Changed"/>
/// when the user copies an image or text. Ignores our own clipboard writes (call <see cref="Suppress"/>).
/// </summary>
public class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly IntPtr _hwnd;
    private HwndSource? _src;
    private int _suppressUntilTick;      // ignore our own writes for a short window
    private readonly DispatcherTimer _debounce;   // collapse the multi-format update burst into one
    private string? _lastSig; private int _lastSigTick;   // drop duplicate change events (clear+set apps)

    public event Action<ClipContent>? Changed;

    public ClipboardWatcher(IntPtr hostHandle)
    {
        _hwnd = hostHandle;
        _src = HwndSource.FromHwnd(_hwnd);
        _src?.AddHook(WndProc);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += OnDebounce;
        AddClipboardFormatListener(_hwnd);
    }

    /// <summary>Ignore clipboard updates for a short window (our own SetImage/SetText, which may fire several).</summary>
    public void Suppress() => _suppressUntilTick = Environment.TickCount + 500;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE) return IntPtr.Zero;
        handled = true;
        if (Environment.TickCount < _suppressUntilTick) return IntPtr.Zero;
        // one copy can post several updates (multiple formats) — debounce to a single read
        _debounce.Stop();
        _debounce.Start();
        return IntPtr.Zero;
    }

    private void OnDebounce(object? s, EventArgs e)
    {
        _debounce.Stop();
        if (Environment.TickCount < _suppressUntilTick) return;
        var content = Read();
        if (content == null) return;

        // 같은 내용이 1초 내 다시 들어오면 무시 (지웠다-세팅 앱의 중복 이벤트)
        string sig = content.IsColor ? "C:" + content.ColorHex
                   : content.Text != null ? "T:" + content.Text
                   : content.Image is { } im ? $"I:{im.PixelWidth}x{im.PixelHeight}"
                   : "?";
        int now = Environment.TickCount;
        if (sig == _lastSig && now - _lastSigTick < 1000) return;
        _lastSig = sig; _lastSigTick = now;

        try { Changed?.Invoke(content); } catch { }
    }

    /// <summary>Read image or text from the clipboard. Retries a few times (clipboard may be locked).</summary>
    private static ClipContent? Read()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var img = System.Windows.Clipboard.GetImage();
                    if (img != null) { img.Freeze(); return new ClipContent { Image = img }; }
                }
                if (System.Windows.Clipboard.ContainsText())
                {
                    var t = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(t)) return new ClipContent { Text = t };
                }
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var files = System.Windows.Clipboard.GetFileDropList();
                    if (files.Count > 0)
                    {
                        var names = new List<string>();
                        foreach (var f in files) if (f != null) names.Add(System.IO.Path.GetFileName(f));
                        return new ClipContent { Text = string.Join("\n", names) };
                    }
                }
                return null; // clipboard read OK but nothing we handle
            }
            catch
            {
                System.Threading.Thread.Sleep(30); // locked by another app, retry
            }
        }
        return null;
    }

    public void Dispose()
    {
        _debounce.Stop();
        try { RemoveClipboardFormatListener(_hwnd); } catch { }
        _src?.RemoveHook(WndProc);
        _src = null;
    }
}
