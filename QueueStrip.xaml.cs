using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Snatch;

/// <summary>Persistent tiny chip stack next to the cursor showing the clipboard queue.
/// Click-through, never activates, stays as long as the queue has items (no timeout).</summary>
public partial class QueueStrip : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;
    private const int Tile = 30;   // chip size — small like a color chip
    private const int VK_CONTROL = 0x11, VK_V = 0x56, VK_K = 0x4B;
    private bool _kDown;

    /// <summary>Ctrl+K(칩 떠있을 때) → 편집 열기.</summary>
    public Action? OnEdit { get; set; }

    private readonly DispatcherTimer _previewTimer;   // auto-hides the newest-item preview box (short)
    private readonly DispatcherTimer _chipTimer;      // auto-hides the whole strip (longer)
    private readonly DispatcherTimer _maxLifeTimer;   // hard cap — always closes, even in keep-until-paste
    private const int PreviewMs = 2500, ChipMs = 8000, MaxLifeMs = 10000;
    private IntPtr _hwnd;
    private double _dpiX = 1, _dpiY = 1;
    private int _lastX = int.MinValue, _lastY = int.MinValue;

    public QueueStrip()
    {
        InitializeComponent();
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewMs) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            PreviewBox.Visibility = Visibility.Collapsed;
            PreviewBox.Child = null;
            if (ChipBox.Visibility != Visibility.Visible) CloseStrip();
        };
        _chipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ChipMs) };
        _chipTimer.Tick += (_, _) =>
        {
            _chipTimer.Stop();
            ChipBox.Visibility = Visibility.Collapsed;
            if (PreviewBox.Visibility != Visibility.Visible) CloseStrip();
        };
        _maxLifeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MaxLifeMs) };
        _maxLifeTimer.Tick += (_, _) => { _maxLifeTimer.Stop(); CloseStrip(); };
        Loaded += OnLoaded;
    }

    /// <summary>Which boxes this strip may show — set before <see cref="SetItems"/>.</summary>
    public bool ShowChips { get; set; } = true;
    public bool ShowPreview { get; set; } = true;
    /// <summary>true = never auto-hide; the app closes the strip when a paste happens.</summary>
    public bool NoTimeout { get; set; }

    /// <summary>Rebuild the chips + newest-item preview from the queue (newest = leftmost).</summary>
    public void SetItems(IReadOnlyList<ClipContent> items)
    {
        Tiles.Children.Clear();
        for (int i = 0; i < items.Count; i++)
            Tiles.Children.Add(MakeTile(items[i]));

        bool has = items.Count > 0;

        // 하드 캡 — 유지 모드든 아니든 10초 넘으면 무조건 닫힘
        _maxLifeTimer.Stop();
        if (has) _maxLifeTimer.Start();

        // 칩 박스 (미니 썸네일) — 길게 유지
        if (has && ShowChips)
        {
            ChipBox.Visibility = Visibility.Visible;
            _chipTimer.Stop();
            if (!NoTimeout) _chipTimer.Start();
        }
        else
        {
            ChipBox.Visibility = Visibility.Collapsed;
            _chipTimer.Stop();
        }

        // 미리보기 박스 (최신 내용) — 짧게 유지. 칩과 독립
        if (has && ShowPreview)
        {
            PreviewBox.Child = MakePreview(items[0]);
            PreviewBox.Visibility = Visibility.Visible;
            _previewTimer.Stop();
            if (!NoTimeout) _previewTimer.Start();
        }
        else
        {
            PreviewBox.Child = null;
            PreviewBox.Visibility = Visibility.Collapsed;
            _previewTimer.Stop();
        }
    }

    private static Brush HexBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return Brushes.Gray; }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Larger preview of the newest item, shown under the chip row.</summary>
    private UIElement MakePreview(ClipContent c)
    {
        if (c.IsColor && c.ColorHex is { } hex)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = HexBrush(hex) });
            row.Children.Add(new TextBlock
            {
                Text = hex, Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            });
            return row;
        }
        if (c.IsImage && c.Image is { } img)
        {
            var im = new Image { Source = img, Stretch = Stretch.Uniform, MaxWidth = 200, MaxHeight = 150 };
            RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
            return new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = im };
        }
        return new TextBlock
        {
            Text = (c.Text ?? "").Trim(),
            Foreground = Brushes.White,
            FontSize = 12,
            MaxWidth = 220,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 90,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    private Border MakeTile(ClipContent c)
    {
        var content = new Grid();

        if (c.IsColor && c.ColorHex is { } hex)
        {
            content.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = HexBrush(hex) });
        }
        else if (c.IsImage && c.Image is { } img)
        {
            var im = new Image { Source = img, Stretch = Stretch.UniformToFill, Width = Tile - 2, Height = Tile - 2 };
            RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
            content.Children.Add(im);
        }
        else
        {
            var t = (c.Text ?? "").TrimStart();
            content.Children.Add(new TextBlock
            {
                Text = t.Length > 0 ? t[..Math.Min(2, t.Length)] : "T",
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return new Border
        {
            Width = Tile,
            Height = Tile,
            Margin = new Thickness(0, 0, 3, 0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = content,
        };
    }

    private void OnLoaded(object? s, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        // 보기 전용 — 클릭 통과, 활성화 안 됨
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        { _dpiX = ct.TransformToDevice.M11; _dpiY = ct.TransformToDevice.M22; }

        MoveToCursor();
        CompositionTarget.Rendering += OnFrame;   // 모니터 refresh rate로 커서 추종(고주사율)
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    private void OnFrame(object? sender, EventArgs ev)
    {
        bool ctrl = IsDown(VK_CONTROL);

        // 붙여넣기(Ctrl+V) 하면 사라짐
        if (ctrl && IsDown(VK_V)) { CloseStrip(); return; }

        // Ctrl+K → 편집 열기
        bool k = IsDown(VK_K);
        if (k && ctrl && !_kDown) { _kDown = true; OnEdit?.Invoke(); return; }
        if (!k) _kDown = false;

        MoveToCursor();
    }

    private void MoveToCursor()
    {
        if (!GetCursorPos(out var p)) return;

        if (p.X == _lastX && p.Y == _lastY) return;   // 안 움직였으면 재배치 불필요
        _lastX = p.X; _lastY = p.Y;

        int wpx = (int)(ActualWidth * _dpiX), hpx = (int)(ActualHeight * _dpiY);
        int offX = (int)(18 * _dpiX), offY = (int)(18 * _dpiY);
        int x = p.X + offX, y = p.Y + offY;

        var mon = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).Bounds;
        if (x + wpx > mon.Right) x = p.X - offX - wpx;
        if (y + hpx > mon.Bottom) y = p.Y - offY - hpx;
        if (x < mon.Left) x = mon.Left;
        if (y < mon.Top) y = mon.Top;

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    }

    private bool _closed;
    public void CloseStrip()
    {
        if (!_closed) { CompositionTarget.Rendering -= OnFrame; _closed = true; }
        _previewTimer.Stop();
        _chipTimer.Stop();
        _maxLifeTimer.Stop();
        try { Close(); } catch { }
    }
}
