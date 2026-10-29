using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Snatch;

/// <summary>
/// 화면에 고정(핀): 이미지를 항상 위에 떠 있는 작은 맥 스타일 창으로.
/// 끌기 = 이동, 휠 = 확대/축소(커서 기준), Ctrl+휠 = 투명도, Esc·더블클릭 = 닫기, 우클릭 = 메뉴.
/// </summary>
public class PinWindow : Window
{
    private static readonly List<PinWindow> _all = new();
    public static int Count => _all.Count;
    public static void CloseAll() { foreach (var p in _all.ToArray()) p.Close(); }
    public static void ClearClickThroughAll() { foreach (var p in _all) p.SetClickThrough(false); }

    private readonly BitmapSource _img;
    private readonly string? _path;
    private readonly Settings _settings;
    private readonly Action<string>? _onEdit;
    private readonly Border _card;
    private readonly Border _shadow;
    private readonly Border _flash;
    private readonly TextBlock _flashText;
    private double _baseW, _baseH;      // 100%일 때 크기 (DIP)
    private int _zoomPct = 100;
    private bool _clickThrough;
    private const double Pad = 14;      // 그림자 자리

    public PinWindow(BitmapSource img, string? path, Settings settings, Action<string>? onEdit)
    {
        _img = img; _path = path; _settings = settings; _onEdit = onEdit;
        Title = "Snatch 핀";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _shadow = new Border
        {
            CornerRadius = new CornerRadius(10), Background = Brushes.Black,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5 },
        };
        _card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(img) { Stretch = Stretch.Fill },
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
        };
        _flashText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
        _flash = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1F)), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0), Opacity = 0, IsHitTestVisible = false, Child = _flashText,
        };
        var root = new Grid { Margin = new Thickness(Pad) };
        root.Children.Add(_shadow); root.Children.Add(_card); root.Children.Add(_flash);
        Content = root;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");

        SourceInitialized += (_, _) =>
        {
            // 원본 픽셀 그대로(100%) 보이게: 물리 px → DIP
            double sx = 1, sy = 1;
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct) { sx = ct.TransformFromDevice.M11; sy = ct.TransformFromDevice.M22; }
            _baseW = Math.Max(24, img.PixelWidth * sx); _baseH = Math.Max(24, img.PixelHeight * sy);
            // 너무 크면 화면 60% 안으로
            var wa = SystemParameters.WorkArea;
            double fit = Math.Min(1.0, Math.Min(wa.Width * 0.6 / _baseW, wa.Height * 0.6 / _baseH));
            _zoomPct = (int)Math.Round(fit * 100);
            ApplySize();
            // 커서 근처에 띄움
            var c = System.Windows.Forms.Cursor.Position;
            Left = Math.Clamp(c.X * sx - _baseW * fit / 2, wa.Left, Math.Max(wa.Left, wa.Right - _baseW * fit - 2 * Pad));
            Top = Math.Clamp(c.Y * sy - _baseH * fit / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - _baseH * fit - 2 * Pad));
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            try { DragMove(); } catch { }
        };
        MouseWheel += OnWheel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) CopyImage();
        };
        ContextMenu = BuildMenu();
        Loaded += (_, _) => _all.Add(this);
        Closed += (_, _) => _all.Remove(this);
    }

    private void ApplySize()
    {
        double z = _zoomPct / 100.0;
        _card.Width = _shadow.Width = Math.Max(16, _baseW * z);
        _card.Height = _shadow.Height = Math.Max(16, _baseH * z);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        int dir = Math.Sign(e.Delta);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Opacity = Math.Clamp(Math.Round(Opacity * 10 + dir) / 10.0, 0.2, 1.0);   // 10% 단위
            Flash($"투명도 {Math.Round(Opacity * 100)}%");
            return;
        }
        // 커서 아래 지점이 그대로 있게 확대/축소
        var p = e.GetPosition(_card);
        double oldW = _card.Width, oldH = _card.Height;
        int step = _zoomPct >= 100 ? 10 : 5;
        _zoomPct = Math.Clamp(_zoomPct + dir * step, 10, 400);
        ApplySize();
        double kx = _card.Width / oldW, ky = _card.Height / oldH;
        Left -= p.X * (kx - 1);
        Top -= p.Y * (ky - 1);
        Flash($"{_zoomPct}%");
    }

    private System.Windows.Threading.DispatcherTimer? _flashTimer;
    private void Flash(string text)
    {
        _flashText.Text = text; _flash.Opacity = 1;
        _flashTimer?.Stop();
        _flashTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _flashTimer.Tick += (_, _) => { _flashTimer!.Stop(); _flash.Opacity = 0; };
        _flashTimer.Start();
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); return i; }
        m.Items.Add(Item("복사  (Ctrl+C)", CopyImage));
        m.Items.Add(Item("저장", SaveImage));
        m.Items.Add(Item("편집", Edit));
        var op = new MenuItem { Header = "투명도" };
        foreach (var v in new[] { 100, 80, 60, 40 }) op.Items.Add(Item($"{v}%", () => Opacity = v / 100.0));
        m.Items.Add(op);
        m.Items.Add(Item("100% 크기", () => { _zoomPct = 100; ApplySize(); }));
        m.Items.Add(Item("클릭 통과 (트레이 메뉴에서 해제)", () => SetClickThrough(true)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("닫기  (Esc)", Close));
        return m;
    }

    private void CopyImage()
    {
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetImage(_img); Flash("복사됨"); return; }
            catch { System.Threading.Thread.Sleep(60); }
        }
        Flash("복사 실패");
    }

    /// <summary>핀 이미지를 파일로 (원본 파일이 있으면 그대로, 없으면 새로 저장).</summary>
    private string? EnsureFile()
    {
        if (_path != null && File.Exists(_path)) return _path;
        try
        {
            var dir = Capture.ScreenshotFolder(_settings);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"스크린샷 {DateTime.Now:yyyy-MM-dd HHmmss}_핀.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_img));
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
            enc.Save(fs);
            return dest;
        }
        catch { return null; }
    }

    private void SaveImage()
    {
        var f = EnsureFile();
        if (f == null) { Flash("저장 실패"); return; }
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{f}\""); } catch { }
        Flash("저장됨");
    }

    private void Edit()
    {
        var f = EnsureFile();
        if (f != null) _onEdit?.Invoke(f);
    }

    // ---- 클릭 통과: 마우스가 핀을 지나 뒤 창으로 감 ----
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int v);
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    public void SetClickThrough(bool on)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        ex = on ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, ex);
        _clickThrough = on;
        if (on) { if (Opacity > 0.85) Opacity = 0.7; Flash("클릭 통과 켜짐 — 해제는 트레이 메뉴"); }
    }

    public bool ClickThrough => _clickThrough;
}
