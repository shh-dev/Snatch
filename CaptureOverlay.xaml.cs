using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace Snatch;

public partial class CaptureOverlay : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private Point _start;
    private bool _dragging;
    private double _scaleX = 1.0, _scaleY = 1.0;

    private bool _winMode;
    private List<(int x, int y, int w, int h)> _windows = new();
    private Int32Rect? _hoverWin; // physical px of highlighted window

    private BitmapSource? _frozen;   // frozen snapshot (physical px) for the loupe
    private const int LoupeHalf = 8; // half-window in source px → 16px sampled, 8× zoom into 128px box
    private int _lastFx = int.MinValue, _lastFy = int.MinValue; // skip idle rebuilds

    private bool _hasTarget;                              // overlay covers one monitor, not whole desktop
    private int _tx, _ty, _tw, _th;                      // target monitor bounds (physical px)

    /// <summary>Selected rectangle in physical screen pixels, set when user completes a drag.</summary>
    public Int32Rect? Result { get; private set; }

    /// <summary>Set when the user just clicks (no drag) = pick the color under cursor (#RRGGBB).</summary>
    public string? PickedColor { get; private set; }

    /// <summary>Set the frozen full-screen snapshot used as the overlay background.</summary>
    public void SetFrozen(System.Windows.Media.Imaging.BitmapSource src) { Frozen.Source = src; _frozen = src; }

    /// <summary>Window rects (physical px, z-order top-first) for Space window-pick mode.</summary>
    public void SetWindows(List<(int x, int y, int w, int h)> wins) => _windows = wins;

    public CaptureOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Activate(); Focus();
            // 모니터 refresh rate 프레임마다 커서 추적 (고주사율 대응) + 돋보기 갱신
            CompositionTarget.Rendering += OnFrame;
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Cover just this monitor (physical px). Call before ShowDialog. If unset, spans whole desktop.</summary>
    public void SetTargetMonitor(int x, int y, int w, int h)
    {
        _hasTarget = true; _tx = x; _ty = y; _tw = w; _th = h;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = PresentationSource.FromVisual(this);
        _scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        _scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        if (_hasTarget)
        {
            Left = _tx / _scaleX; Top = _ty / _scaleY;
            Width = _tw / _scaleX; Height = _th / _scaleY;
        }
        else
        {
            Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        }
    }

    /// <summary>Per-composed-frame update: samples the live cursor so selection + loupe
    /// track at the display's refresh rate (smooth on 144/240Hz), not just on mouse events.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (_winMode) { Loupe.Visibility = Visibility.Collapsed; return; }
        if (!GetCursorPos(out var p)) return;

        int fx = p.X, fy = p.Y;
        if (!_dragging && fx == _lastFx && fy == _lastFy) return; // cursor idle → nothing to redo
        _lastFx = fx; _lastFy = fy;

        double diuX = p.X / _scaleX - Left;   // OS physical cursor → Root DIU
        double diuY = p.Y / _scaleY - Top;

        if (_dragging) Place(_start, new Point(diuX, diuY));
        UpdateLoupe(diuX, diuY);
    }

    private void UpdateLoupe(double diuX, double diuY)
    {
        if (_frozen == null) return;

        // cursor → frozen bitmap pixel (frozen spans exactly this window)
        int fx = (int)Math.Round(diuX * _scaleX);
        int fy = (int)Math.Round(diuY * _scaleY);

        int size = LoupeHalf * 2;
        int x0 = Math.Clamp(fx - LoupeHalf, 0, Math.Max(0, _frozen.PixelWidth - size));
        int y0 = Math.Clamp(fy - LoupeHalf, 0, Math.Max(0, _frozen.PixelHeight - size));
        if (_frozen.PixelWidth < size || _frozen.PixelHeight < size) return;

        try
        {
            var crop = new CroppedBitmap(_frozen, new Int32Rect(x0, y0, size, size));
            crop.Freeze();
            LoupeImg.Source = crop;
        }
        catch { return; }

        if (_dragging)
        {
            int pw = (int)Math.Round(Math.Abs(diuX - _start.X) * _scaleX);
            int ph = (int)Math.Round(Math.Abs(diuY - _start.Y) * _scaleY);
            LoupeInfo.Text = $"{pw} × {ph}";
        }
        else
        {
            LoupeInfo.Text = $"{fx}, {fy}   {SamplePixelHex(fx, fy)}";
        }

        // 커서 우하단에 붙이되 화면 끝이면 반대쪽으로
        const double lw = 140, lh = 165, off = 24;
        double lx = diuX + off, ly = diuY + off;
        if (lx + lw > ActualWidth) lx = diuX - off - lw;
        if (ly + lh > ActualHeight) ly = diuY - off - lh;
        if (lx < 0) lx = 0;
        if (ly < 0) ly = 0;
        Canvas.SetLeft(Loupe, lx);
        Canvas.SetTop(Loupe, ly);
        Loupe.Visibility = Visibility.Visible;
    }

    private string SamplePixelHex(int fx, int fy)
    {
        try
        {
            if (_frozen == null || fx < 0 || fy < 0 || fx >= _frozen.PixelWidth || fy >= _frozen.PixelHeight)
                return "";
            var one = new CroppedBitmap(_frozen, new Int32Rect(fx, fy, 1, 1));
            var buf = new byte[4];
            one.CopyPixels(buf, 4, 0); // Bgra32/Pbgra32
            return $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}";
        }
        catch { return ""; }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_winMode)
        {
            if (_hoverWin is { } w) { Result = w; DialogResult = true; }
            return;
        }
        _start = e.GetPosition(Root);
        _dragging = true;
        Sel.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        Place(_start, _start);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        // 드래그 중 선택영역은 OnFrame(고주사율)에서 갱신. 여기선 창 모드 하이라이트만.
        if (_winMode) UpdateHoverWindow(e.GetPosition(Root));
    }

    private void ToggleWinMode()
    {
        _winMode = !_winMode;
        _dragging = false;
        if (_winMode)
        {
            UpdateHoverWindow(Mouse.GetPosition(Root));
        }
        else
        {
            _hoverWin = null;
            Sel.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateHoverWindow(Point diu)
    {
        int px = (int)Math.Round((Left + diu.X) * _scaleX);
        int py = (int)Math.Round((Top + diu.Y) * _scaleY);
        foreach (var w in _windows)
        {
            if (px >= w.x && px < w.x + w.w && py >= w.y && py < w.y + w.h)
            {
                _hoverWin = new Int32Rect(w.x, w.y, w.w, w.h);
                // 물리 → DIU 로 변환해 하이라이트
                double dx = w.x / _scaleX - Left, dy = w.y / _scaleY - Top;
                double dw = w.w / _scaleX, dh = w.h / _scaleY;
                Canvas.SetLeft(Sel, dx); Canvas.SetTop(Sel, dy);
                Sel.Width = dw; Sel.Height = dh;
                Sel.Visibility = Visibility.Visible;
                SizeLabel.Text = $"{w.w} × {w.h} px";
                double by = dy - 28; if (by < 4) by = dy + 6;
                Canvas.SetLeft(SizeBadge, dx); Canvas.SetTop(SizeBadge, by);
                SizeBadge.Visibility = Visibility.Visible;
                return;
            }
        }
        _hoverWin = null;
        Sel.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var end = e.GetPosition(Root);

        double x = Math.Min(_start.X, end.X);
        double y = Math.Min(_start.Y, end.Y);
        double w = Math.Abs(end.X - _start.X);
        double h = Math.Abs(end.Y - _start.Y);

        // 단순 클릭(드래그 아님) = 커서 아래 픽셀 색상 뽑기 → HEX 반환하고 닫기
        if (w < 6 || h < 6)
        {
            int fx = (int)Math.Round(_start.X * _scaleX);
            int fy = (int)Math.Round(_start.Y * _scaleY);
            var hex = SamplePixelHex(fx, fy);
            if (!string.IsNullOrEmpty(hex)) { PickedColor = hex; DialogResult = true; }
            else { Sel.Visibility = Visibility.Collapsed; SizeBadge.Visibility = Visibility.Collapsed; }
            return;
        }

        // DIU (window/root coords) -> physical screen pixels
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double screenDiuX = Left + x;
        double screenDiuY = Top + y;

        Result = new Int32Rect(
            (int)Math.Round(screenDiuX * sx),
            (int)Math.Round(screenDiuY * sy),
            (int)Math.Round(w * sx),
            (int)Math.Round(h * sy));

        DialogResult = true; // closes
    }

    private void Place(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        Canvas.SetLeft(Sel, x);
        Canvas.SetTop(Sel, y);
        Sel.Width = w;
        Sel.Height = h;

        // 저장될 실제 픽셀(물리) 표시
        int pw = (int)Math.Round(w * _scaleX);
        int ph = (int)Math.Round(h * _scaleY);
        SizeLabel.Text = $"{pw} × {ph} px";
        double bx = x;
        double by = y - 28;            // 선택영역 위에 배지
        if (by < 4) by = y + 6;        // 위 공간 없으면 안쪽
        Canvas.SetLeft(SizeBadge, bx);
        Canvas.SetTop(SizeBadge, by);
    }

    private void OnCancel(object sender, MouseButtonEventArgs e) { Result = null; DialogResult = false; }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Result = null; DialogResult = false; }
        else if (e.Key == Key.Space) { ToggleWinMode(); e.Handled = true; }
    }
}
