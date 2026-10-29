using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Snatch;

/// <summary>
/// 캡처 직후 미니 창 (CleanShot Quick Access 느낌): 모니터 오른쪽 아래에 썸네일 카드.
/// 올리면 편집·복사·폴더·핀·닫기 버튼, 썸네일을 끌면 진짜 파일 드래그(카톡·슬랙·탐색기·브라우저에 놓기).
/// 6초 뒤 자동으로 사라짐(올려 두면 유지), 연속 캡처는 3장까지 위로 쌓임.
/// </summary>
public class MiniCardWindow : Window
{
    private static readonly List<MiniCardWindow> _open = new();
    private const int MaxCards = 3;
    private const double CardW = 230;

    private readonly string _path;
    private readonly Action<string> _onEdit, _onPin;
    private readonly Border _actions;
    private readonly System.Windows.Threading.DispatcherTimer _hide;
    private Point _downAt; private bool _down;

    public static void Present(string path, Action<string> onEdit, Action<string> onPin)
    {
        try
        {
            var w = new MiniCardWindow(path, onEdit, onPin);
            w.Show();
        }
        catch { }
    }

    private MiniCardWindow(string path, Action<string> onEdit, Action<string> onPin)
    {
        _path = path; _onEdit = onEdit; _onPin = onPin;
        Title = "Snatch 미니"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        Win32.ExcludeFromCapture(this);   // 다음 캡처에 이 카드가 찍히지 않게

        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 460; bmp.UriSource = new Uri(path); bmp.EndInit(); bmp.Freeze();
        double h = Math.Clamp(CardW * bmp.PixelHeight / Math.Max(1, bmp.PixelWidth), 60, 260);

        var thumb = new Border
        {
            Width = CardW, Height = h, CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top },
            Cursor = Cursors.Hand, ToolTip = "끌어서 다른 앱에 놓기 · 클릭 = 편집",
        };

        // 올리면 나타나는 버튼들
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(Btn("편집", () => { _onEdit(_path); Close(); }));
        row.Children.Add(Btn("복사", CopyImage));
        row.Children.Add(Btn("폴더", () => { try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_path}\""); } catch { } }));
        row.Children.Add(Btn("핀", () => { _onPin(_path); Close(); }));
        var closeX = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1F)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "닫기",
        };
        closeX.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        // 어두운 막은 클릭을 막지 않게(썸네일 클릭·끌기가 그대로 되도록) 버튼만 받게 함
        var overlay = new Grid();
        overlay.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), IsHitTestVisible = false });
        overlay.Children.Add(row); overlay.Children.Add(closeX);
        _actions = new Border { CornerRadius = new CornerRadius(10), Child = overlay, Opacity = 0, IsHitTestVisible = false, Width = CardW, Height = h };
        // 둥근 모서리로 오버레이 자르기
        _actions.Clip = new RectangleGeometry(new Rect(0, 0, CardW, h), 10, 10);

        var shadow = new Border
        {
            CornerRadius = new CornerRadius(10), Background = Brushes.Black, Width = CardW, Height = h,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5 },
        };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), IsHitTestVisible = false,
        };
        var root = new Grid { Margin = new Thickness(14) };
        root.Children.Add(shadow); root.Children.Add(thumb); root.Children.Add(_actions); root.Children.Add(frame);
        Content = root;

        // 썸네일: 클릭 = 편집, 끌기 = 파일 드래그
        thumb.MouseLeftButtonDown += (_, e) => { _down = true; _downAt = e.GetPosition(this); thumb.CaptureMouse(); };
        thumb.MouseMove += (_, e) =>
        {
            if (!_down || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _downAt.X) < 6 && Math.Abs(p.Y - _downAt.Y) < 6) return;
            _down = false; thumb.ReleaseMouseCapture();
            StartDrag(thumb);
        };
        thumb.MouseLeftButtonUp += (_, _) =>
        {
            if (!_down) return;
            _down = false; thumb.ReleaseMouseCapture();
            _onEdit(_path); Close();
        };

        _hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hide.Tick += (_, _) => { _hide.Stop(); if (!IsMouseOver) Close(); };
        MouseEnter += (_, _) => { _hide.Stop(); ShowActions(true); };
        MouseLeave += (_, _) => { ShowActions(false); _hide.Stop(); _hide.Interval = TimeSpan.FromSeconds(3); _hide.Start(); };

        Loaded += (_, _) =>
        {
            _open.Add(this);
            while (_open.Count > MaxCards) _open[0].Close();
            Reflow();
            _hide.Start();
        };
        Closed += (_, _) => { _hide.Stop(); _open.Remove(this); Reflow(); };
    }

    private void ShowActions(bool on)
    {
        _actions.Opacity = on ? 1 : 0;
        _actions.IsHitTestVisible = on;
    }

    private Border Btn(string label, Action onClick)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2E)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold },
        };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        b.MouseLeave += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2E));
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private void CopyImage()
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(_path); bi.EndInit(); bi.Freeze();
            for (int i = 0; i < 10; i++)
            {
                try { Clipboard.SetImage(bi); Close(); return; }
                catch { System.Threading.Thread.Sleep(60); }
            }
        }
        catch { }
    }

    /// <summary>진짜 파일 드래그: 파일 경로(FileDrop) + 이미지 둘 다 실어서 어느 앱이든 받게.</summary>
    private void StartDrag(UIElement src)
    {
        if (!File.Exists(_path)) return;
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { _path });
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(_path); bi.EndInit(); bi.Freeze();
                data.SetImage(bi);
            }
            catch { }
            _hide.Stop();
            Opacity = 0.6;
            var result = DragDrop.DoDragDrop(src, data, DragDropEffects.Copy | DragDropEffects.Move);
            Opacity = 1;
            if (result != DragDropEffects.None) Close();
            else { _hide.Interval = TimeSpan.FromSeconds(3); _hide.Start(); }
        }
        catch { Opacity = 1; }
    }

    /// <summary>커서가 있는 모니터 오른쪽 아래부터 위로 쌓기 (최신이 맨 아래).</summary>
    private static void Reflow()
    {
        if (_open.Count == 0) return;
        var any = _open[^1];
        var c = System.Windows.Forms.Cursor.Position;
        var wp = System.Windows.Forms.Screen.FromPoint(c).WorkingArea;
        var ct = PresentationSource.FromVisual(any)?.CompositionTarget;
        double fx = ct?.TransformFromDevice.M11 ?? 1.0, fy = ct?.TransformFromDevice.M22 ?? 1.0;
        double right = wp.Right * fx, bottom = wp.Bottom * fy, top0 = wp.Top * fy;
        double y = bottom - 6;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            var w = _open[i];
            w.UpdateLayout();
            double h = w.ActualHeight, wd = w.ActualWidth;
            w.Left = right - wd - 6;
            w.Top = Math.Max(top0, y - h);
            y -= h - 10;   // 그림자 여백만큼 살짝 겹치게
        }
    }
}
