using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Snatch;

/// <summary>스크롤 캡처 중 표시 배지: 캡처 영역 바로 위(공간 없으면 아래)에 뜸. 캡처에 안 찍히고 마우스도 통과.</summary>
public class ScrollBadge : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;

    private readonly TextBlock _text;

    public ScrollBadge(int rx, int ry, int rw, int rh)   // 영역: 물리 px
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000;
        _text = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot); row.Children.Add(_text);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1F)), CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 14, 6), Child = row,
        };
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        SetProgress(0, 0);
        Win32.ExcludeFromCapture(this);   // 결과 이미지에 안 찍히게

        Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // 클릭·휠이 배지를 지나 아래 창으로 가게 (스크롤 대상 판단을 방해하지 않게)
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            double k = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            int bw = (int)(ActualWidth * k), bh = (int)(ActualHeight * k);
            var mon = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(rx + rw / 2, ry + rh / 2)).Bounds;
            int x = Math.Clamp(rx + (rw - bw) / 2, mon.Left, mon.Right - bw);
            int y = ry - bh - 8;                                  // 영역 위
            if (y < mon.Top) y = ry + rh + 8;                     // 공간 없으면 아래
            if (y + bh > mon.Bottom) y = ry + 8;                  // 그래도 없으면 영역 안쪽 위 (가운데 커서와는 멀리)
            SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x1 | 0x10);   // TOPMOST, NOSIZE|NOACTIVATE
        };
    }

    /// <summary>드래그한 영역 테두리. 안쪽은 완전 투명 + 클릭 통과라 휠·캡처를 방해하지 않음.</summary>
    public static Window ShowFrame(int rx, int ry, int rw, int rh)
    {
        const int pad = 3;   // 테두리를 영역 바깥에 그려서 캡처 이미지 가장자리에 안 걸리게
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Width = 10, Height = 10,
            Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), BorderThickness = new Thickness(2) },
        };
        Win32.ExcludeFromCapture(w);
        w.Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowPos(h, new IntPtr(-1), rx - pad, ry - pad, rw + pad * 2, rh + pad * 2, 0x10);   // 물리 px로 위치·크기, NOACTIVATE
        };
        w.Show();
        return w;
    }

    public void SetProgress(int frames, int height) =>
        _text.Text = frames == 0
            ? "스크롤 캡처 중 · 마우스는 잠깐 놓아 주세요 · Esc = 거기까지 저장"
            : $"스크롤 캡처 중 · {frames}장 · {height}px · Esc = 거기까지 저장";
}
