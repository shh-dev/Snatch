using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using Path = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace Snatch;

/// <summary>Simple screenshot annotator: spotlight dim, arrows, boxes, numbered markers. Save / copy result.</summary>
public partial class AnnotationWindow : Window
{
    private readonly string _srcPath;
    private readonly Settings _settings;
    private readonly int _origW, _origH;
    private readonly double _dispW, _dispH;

    private string _tool = "arrow";
    private SolidColorBrush _color = new(Color.FromRgb(0xFF, 0x33, 0x33));   // 주석 색 (팔레트로 변경)
    private double _strokeW = 3;                                             // 선 굵기
    private Brush Accent => _color;
    private double Stroke => _strokeW;
    private static readonly Brush HlBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0x00));  // 불투명 — 멀티플라이 셰이더가 합성
    private const double HlWidth = 16;
    private Canvas? _hlLayer;   // highlighter strokes, multiply-blended over the image
    private BitmapSource _srcBmp = null!;   // full-res source for blur/mosaic
    private const double Margin = 2000;     // 이미지 둘레 여백(마크업 여유) — 넉넉한 가상 캔버스: 어느 방향으로든 이미지 밖에 그려도 안 잘림
    private Rectangle _bg = null!;          // 여백 배경 (성장 시 리사이즈)

    /// <summary>내용이 오른쪽/아래로 넘치면 캔버스 확장.</summary>
    private void GrowRightBottom(double x, double y)
    {
        const double pad = 40;
        bool grew = false;
        if (x + pad > Surface.Width) { Surface.Width = x + pad; grew = true; }
        if (y + pad > Surface.Height) { Surface.Height = y + pad; grew = true; }
        if (grew)
        {
            _bg.Width = Surface.Width; _bg.Height = Surface.Height;
            if (_spotlight != null) RebuildSpotlight();
        }
    }

    private Point _start;
    private bool _dragging;
    private Shape? _live;              // shape being dragged
    private Image? _blurLive;          // mosaic being dragged
    private bool _hatch;               // 사각형/원 빗금 채우기
    private double _fontSize = 12;     // 텍스트/콜아웃 폰트
    private bool _escArmed;            // ESC 두 번 확인
    private TextAlignment _align = TextAlignment.Left;
    private ShapePath? _spotlight;         // single dim overlay, multiple bright holes
    private readonly List<Rect> _holes = new();
    private int _dimTransPct = 15;         // 딤 투명도 % (휠 5%단위, 10~100)
    private byte DimAlpha => (byte)Math.Round(255 * (100 - _dimTransPct) / 100.0);
    private int _num;                  // number badge counter
    // ---- 실행취소/다시실행: 모든 편집(추가·이동·크기·삭제·색·자동도형·자르기)을 명령으로 쌓는다 ----
    private sealed record EditCmd(Action Undo, Action Redo);
    private readonly Stack<EditCmd> _undo = new(), _redo = new();
    private void Push(EditCmd c) { _undo.Push(c); _redo.Clear(); }

    /// <summary>이미 parent에 들어간 요소들을 "추가" 명령으로 기록.</summary>
    private void PushAdded(Panel parent, params UIElement[] els)
    {
        Push(new EditCmd(
            () => { foreach (var el in els) parent.Children.Remove(el); Deselect(); },
            () => { foreach (var el in els) if (!parent.Children.Contains(el)) parent.Children.Add(el); }));
    }

    // 여러 조각으로 된 주석(화살표 선+촉, 콜아웃 선+점+칩)은 선택할 때 한 덩어리로
    private readonly Dictionary<UIElement, UIElement[]> _groupOf = new();
    private void RegisterGroup(params UIElement[] els) { foreach (var el in els) _groupOf[el] = els; }

    /// <summary>화살표 정보: 이동·크기 조절 시 선과 촉을 다시 계산.</summary>
    private sealed class ArrowInfo { public required Line Line; public required Polygon Head; public Point From, Tip; }
    private readonly Dictionary<UIElement, ArrowInfo> _arrowOf = new();
    private void RegisterArrow(Line ln, Polygon head, Point from, Point tip)
    {
        var ai = new ArrowInfo { Line = ln, Head = head, From = from, Tip = tip };
        _arrowOf[ln] = ai; _arrowOf[head] = ai;
        RegisterGroup(ln, head);
    }

    private void RebuildArrow(ArrowInfo ai)
    {
        const double headLen = 16;
        double ang = Math.Atan2(ai.Tip.Y - ai.From.Y, ai.Tip.X - ai.From.X);
        double back = Math.Min(headLen * 0.9, Dist(ai.From, ai.Tip));
        ai.Line.X1 = ai.From.X; ai.Line.Y1 = ai.From.Y;
        ai.Line.X2 = ai.Tip.X - back * Math.Cos(ang); ai.Line.Y2 = ai.Tip.Y - back * Math.Sin(ang);
        ai.Head.Points = ArrowHead(ai.From, ai.Tip).Points;
    }

    public AnnotationWindow(string path, Settings settings)
    {
        InitializeComponent();
        _srcPath = path;
        _settings = settings;

        var bmp = Load(path);
        _srcBmp = bmp;
        _origW = bmp.PixelWidth; _origH = bmp.PixelHeight;

        // 창은 이미지에 맞추지 않고 넉넉한 고정 크기로 연다 (작업영역 높이 80%, 폭은 툴바 이상·70%) — 가장자리 끌어서 조절
        var wa = SystemParameters.WorkArea;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Min(wa.Width - 20, Math.Max(1220, wa.Width * 0.7));   // 툴바(선택·자르기·다시 포함)가 다 들어가게
        Height = Math.Min(wa.Height - 20, Math.Max(560, wa.Height * 0.8));
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(14), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });

        // 이미지는 창 안 보이는 영역(그림자·제목줄·툴바 자리 제외)에 맞춤
        double maxW = Width - 36 - 2 - 20 - 20, maxH = Height - 36 - 2 - 34 - 100 - 20;
        // 세로로 아주 긴 이미지(스크롤 캡처)는 폭 기준으로 맞추고 세로는 스크롤 — 높이까지 맞추면 너무 작아짐
        bool tall = _origH > _origW * 2.5;
        double scale = tall ? Math.Min(1.0, maxW / _origW) : Math.Min(1.0, Math.Min(maxW / _origW, maxH / _origH));
        _dispW = Math.Max(1, _origW * scale);
        _dispH = Math.Max(1, _origH * scale);

        // 이미지 둘레 여백(여기에도 마크업 가능)
        Surface.Width = _dispW + 2 * Margin; Surface.Height = _dispH + 2 * Margin;

        // 배경(여백 포함) — index 0
        _bg = new Rectangle { Width = Surface.Width, Height = Surface.Height, Fill = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1F)) };   // 창 배경과 같은 색 (이미지만 도드라지게)
        Canvas.SetLeft(_bg, 0); Canvas.SetTop(_bg, 0);
        Surface.Children.Insert(0, _bg);

        // 이미지 — 여백만큼 오프셋
        BaseImage.Source = bmp; BaseImage.Width = _dispW; BaseImage.Height = _dispH;
        Canvas.SetLeft(BaseImage, Margin); Canvas.SetTop(BaseImage, Margin);

        // 형광펜 전용 레이어 (이미지 위) — 멀티플라이 블렌드
        _hlLayer = new Canvas
        {
            Width = _dispW, Height = _dispH, IsHitTestVisible = false,
            Effect = new MultiplyEffect { Bg = new ImageBrush(bmp) { Stretch = Stretch.Fill } },
        };
        Canvas.SetLeft(_hlLayer, Margin); Canvas.SetTop(_hlLayer, Margin);
        Surface.Children.Add(_hlLayer);

        MinWidth = 640; MinHeight = 420;
        // 처음엔 이미지가 화면 가운데 오게 스크롤 (여백 140px은 축소하거나 스크롤하면 보임)
        Loaded += (_, _) =>
        {
            Scroller.UpdateLayout();
            Scroller.ScrollToHorizontalOffset(Math.Max(0, (Scroller.ExtentWidth - Scroller.ViewportWidth) / 2));
            Scroller.ScrollToVerticalOffset(Math.Max(0, (Scroller.ExtentHeight - Scroller.ViewportHeight) / 2));
        };

        BuildSwatches();
        PenAutoChk.IsChecked = _settings.PenAutoShape;
        SelectWidth(3);
        SetFont(12);
        BtnAL.Content = AlignIcon(HorizontalAlignment.Left);
        BtnAC.Content = AlignIcon(HorizontalAlignment.Center);
        BtnAR.Content = AlignIcon(HorizontalAlignment.Right);
        SelectAlign(TextAlignment.Left);
        // 옵션에서 고른 시작 도구 (없거나 잘못된 값이면 화살표)
        var startTool = DefaultToolBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.DefaultTool) ?? DefaultToolBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "arrow");
        _toolBoxSync = true; DefaultToolBox.SelectedItem = startTool; _toolBoxSync = false;
        SelectTool((string)startTool.Tag);
    }

    private bool _fontSync;
    private void OnFontBox(object sender, RoutedEventArgs e)
    {
        if (_fontSync) return;
        var s = FontBox.Text ?? "";
        var digits = new string(s.Where(char.IsDigit).ToArray());
        if (double.TryParse(digits, out var f)) _fontSize = Math.Clamp(f, 8, 200);
    }

    private void OnFontStep(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && double.TryParse(b.Tag as string, out var d)) SetFont(_fontSize + d);
    }

    private void SetFont(double f)
    {
        _fontSize = Math.Clamp(f, 8, 200);
        _fontSync = true; FontBox.Text = ((int)_fontSize).ToString(); _fontSync = false;
    }

    private static UIElement AlignIcon(HorizontalAlignment ha)
    {
        var sp = new StackPanel { Width = 16 };
        foreach (var w in new double[] { 14, 9, 14 })
            sp.Children.Add(new Rectangle { Height = 2, Width = w, Fill = Brushes.White, Margin = new Thickness(0, 1.5, 0, 1.5), HorizontalAlignment = ha });
        return sp;
    }

    private void OnAlign(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && Enum.TryParse<TextAlignment>(b.Tag as string, out var al)) SelectAlign(al);
    }

    private void SelectAlign(TextAlignment al)
    {
        _align = al;
        var a = ActiveBg;
        var n = NormalBg;
        BtnAL.Background = al == TextAlignment.Left ? a : n;
        BtnAC.Background = al == TextAlignment.Center ? a : n;
        BtnAR.Background = al == TextAlignment.Right ? a : n;
    }

    // 선택 상태 배경 (macOS 파랑) / 기본 (투명)
    private static readonly SolidColorBrush ActiveBg = new(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly SolidColorBrush NormalBg = new(Colors.Transparent);

    private static readonly Color DefaultColor = Color.FromRgb(0xFF, 0x3B, 0x30);
    // macOS 시스템 색 계열: 빨강·주황·노랑·초록·민트·파랑·보라·분홍·흰색·회색·검정
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00),
        Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x00, 0xC7, 0xBE), Color.FromRgb(0x0A, 0x84, 0xFF),
        Color.FromRgb(0xAF, 0x52, 0xDE), Color.FromRgb(0xFF, 0x2D, 0x55), Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0x8E, 0x8E, 0x93), Color.FromRgb(0x11, 0x11, 0x11),
    };

    private void BuildSwatches()
    {
        foreach (var col in Palette)
        {
            Swatches.Children.Add(MakeSwatch(col, 16, new Thickness(0, 0, 4, 0)));   // 옵션 줄
            ColorGrid.Children.Add(MakeSwatch(col, 24, new Thickness(3)));          // 색상 팝업
        }
        var start = DefaultColor;
        try { if (!string.IsNullOrEmpty(_settings.AnnotColor)) start = (Color)ColorConverter.ConvertFromString(_settings.AnnotColor); }
        catch { }
        SelectColor(start, save: false);
    }

    private System.Windows.Controls.Border MakeSwatch(Color col, double size, Thickness margin)
    {
        var b = new System.Windows.Controls.Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Margin = margin,
            Background = new SolidColorBrush(col), Cursor = System.Windows.Input.Cursors.Hand,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2),
            Tag = col, ToolTip = $"#{col.R:X2}{col.G:X2}{col.B:X2}",
        };
        b.MouseLeftButtonDown += (_, _) => { SelectColor(col); ColorPopup.IsOpen = false; };
        return b;
    }

    private void SelectColor(Color col, bool save = true)
    {
        _color = new SolidColorBrush(col);
        ColorDot.Fill = new SolidColorBrush(col);
        foreach (var panel in new Panel[] { Swatches, ColorGrid })
            foreach (System.Windows.Controls.Border b in panel.Children)
                b.BorderBrush = (b.Tag is Color bc && bc == col)
                    ? (col.R + col.G + col.B > 600 ? Brushes.DodgerBlue : Brushes.White)
                    : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        if (save) RecolorSelection(col);   // 선택된 주석이 있으면 그 색으로
        if (save)
        {
            _settings.AnnotColor = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
            _settings.Save();   // 다음에 열 때도 이 색
        }
    }

    private void OnColorBtn(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = !ColorPopup.IsOpen;

    private void OnCustomColor(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = false;
        var c = _color.Color;
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SelectColor(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
    }

    private void OnTitleDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { OnZoomFit(this, new RoutedEventArgs()); return; }   // 제목줄 더블클릭 = 맞춤
        try { DragMove(); } catch { }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnHatch(object sender, RoutedEventArgs e)
    {
        _hatch = !_hatch;
        BtnHatch.Background = _hatch
            ? ActiveBg
            : NormalBg;
    }

    private Brush HatchBrush()
    {
        var pen = new Pen(new SolidColorBrush(_color.Color), 2); pen.Freeze();
        var gd = new System.Windows.Media.GeometryDrawing { Pen = pen, Geometry = new LineGeometry(new Point(0, 7), new Point(7, 0)) };
        var b = new DrawingBrush(gd)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 7, 7),
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 7, 7),
        };
        b.Freeze();
        return b;
    }

    private void OnWidth(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && double.TryParse(btn.Tag as string, out var w)) SelectWidth(w);
    }

    private void SelectWidth(double w)
    {
        _strokeW = w;
        var active = ActiveBg;
        var normal = NormalBg;
        BtnW1.Background = w <= 2 ? active : normal;
        BtnW2.Background = (w > 2 && w <= 4) ? active : normal;
        BtnW3.Background = w > 4 ? active : normal;
    }

    private static BitmapSource Load(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private void OnTool(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t) SelectTool(t);
    }

    private void SelectTool(string t)
    {
        if (t == "crop" && _tool != "crop") _toolBeforeCrop = _tool == "select" ? "select" : _tool;
        if (t != "crop") CancelCropLive();
        if (t != "select") Deselect();
        _tool = t;
        Surface.Cursor = t switch { "select" => System.Windows.Input.Cursors.Arrow, "crop" => System.Windows.Input.Cursors.Cross, _ => null };
        BtnSel.Background = t == "select" ? ActiveBg : NormalBg;
        BtnCrop.Background = t == "crop" ? ActiveBg : NormalBg;
        var active = ActiveBg;
        var normal = NormalBg;
        foreach (var (btn, tag) in new[] { (BtnSpot, "spot"), (BtnArrow, "arrow"), (BtnRect, "rect"), (BtnEll, "ellipse"), (BtnPen, "pen"), (BtnHl, "hl"), (BtnBlur, "blur"), (BtnText, "text"), (BtnCallout, "callout"), (BtnNum, "num") })
            btn.Background = tag == t ? active : normal;
    }

    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_spotlight == null) return;   // 스포트라이트 있을 때만 딤 조절
        _dimTransPct = Math.Clamp(_dimTransPct + (e.Delta > 0 ? 5 : -5), 10, 100);
        _spotlight.Fill = new SolidColorBrush(Color.FromArgb(DimAlpha, 0, 0, 0));
        ShowDimToast();
        e.Handled = true;
    }

    // ---- 보기 확대/축소 (저장 결과에는 영향 없음) ----
    // 1% 단위 정수(퍼센트)로 관리. 목표값을 정하면 매 프레임 부드럽게 따라가고, 보고 있던 화면 가운데 지점이 가운데에 그대로 남는다.
    private int _zoomPct = 100, _zoomTargetPct = 100;
    private const int ZoomMinPct = 5, ZoomMaxPct = 400;
    private bool _zoomAnimating, _zoomHooked;

    private void OnPreviewWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        // 휠 한 칸(120) = 5%, 터치패드처럼 잘게 오면 그만큼 잘게 (최소 1%)
        int step = (int)Math.Round(e.Delta / 120.0 * 5);
        if (step == 0) step = Math.Sign(e.Delta);
        ZoomBy(step);
        e.Handled = true;   // ScrollViewer 스크롤 / 딤 조절로 안 넘어가게
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(+10);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(-10);

    /// <summary>100%가 아니면 100%로, 100%면 창 안에 전체(여백 포함)가 다 보이게 맞춤.</summary>
    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
        if (_zoomTargetPct != 100) { _zoomAnchor = new Point(Margin + _dispW / 2, Margin + _dispH / 2); SetZoomTarget(100); return; }
        double vw = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
        double vh = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        // 캔버스는 사방 넉넉한 가상 영역이라, 맞춤은 "이미지(+약간 여유)"가 다 보이게
        double fit = Math.Min(vw / (_dispW + 80), vh / (_dispH + 80));
        _zoomAnchor = new Point(Margin + _dispW / 2, Margin + _dispH / 2);   // 맞춤은 이미지 가운데 기준
        SetZoomTarget((int)Math.Floor(Math.Min(1.0, fit) * 100));
    }

    private Point? _zoomAnchor;   // 줌 기준점(콘텐츠 좌표). null이면 지금 화면 가운데

    private void ZoomBy(int deltaPct) => SetZoomTarget(_zoomTargetPct + deltaPct);

    private void SetZoomTarget(int pct)
    {
        _zoomTargetPct = Math.Clamp(pct, ZoomMinPct, ZoomMaxPct);
        ZoomLabel.Text = $"{_zoomTargetPct}%";
        FlashToast($"보기 {_zoomTargetPct}%");
        if (!_zoomHooked)
        {
            _zoomHooked = true;
            Closed += (_, _) => System.Windows.Media.CompositionTarget.Rendering -= ZoomFrame;
        }
        if (!_zoomAnimating)
        {
            _zoomAnimating = true;
            System.Windows.Media.CompositionTarget.Rendering += ZoomFrame;
        }
    }

    /// <summary>매 프레임 목표까지 남은 거리의 1/4씩(최소 1%) 이동 — 1% 단위로 부드럽게.</summary>
    private void ZoomFrame(object? sender, EventArgs e)
    {
        int diff = _zoomTargetPct - _zoomPct;
        if (diff == 0)
        {
            System.Windows.Media.CompositionTarget.Rendering -= ZoomFrame;
            _zoomAnimating = false;
            _zoomAnchor = null;
            return;
        }
        int move = (int)Math.Round(diff / 4.0);
        if (move == 0) move = Math.Sign(diff);

        // 지금 화면 가운데에 있는 지점(콘텐츠 좌표)을 기억 → 배율 바꾼 뒤 다시 가운데로 (맞춤일 땐 이미지 가운데)
        double oldZ = _zoomPct / 100.0;
        double cx = _zoomAnchor?.X ?? (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / oldZ;
        double cy = _zoomAnchor?.Y ?? (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / oldZ;

        _zoomPct += move;
        double z = _zoomPct / 100.0;
        ZoomTx.ScaleX = ZoomTx.ScaleY = z;
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(Math.Max(0, cx * z - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, cy * z - Scroller.ViewportHeight / 2));
    }

    private void ShowDimToast() => FlashToast($"투명도 {_dimTransPct}%");

    private void FlashToast(string text)
    {
        DimToastText.Text = text;
        var kf = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        kf.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
        kf.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150))));
        DimToast.BeginAnimation(OpacityProperty, kf);
    }

    private void OnToggleOpts(object sender, RoutedEventArgs e)
    {
        bool show = OptionsPanel.Visibility != Visibility.Visible;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnOpts.Background = show ? ActiveBg : NormalBg;
    }

    // 툴바를 캔버스 안에서 이동(빈 영역 잡고 드래그) — 아래쪽도 표기 가능
    private bool _tbDrag;
    private Point _tbStart; private double _tbOx, _tbOy;
    private void OnToolbarDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button || e.OriginalSource is Image) return;   // 버튼 클릭은 통과
        _tbDrag = true; _tbStart = e.GetPosition(this); _tbOx = TbTx.X; _tbOy = TbTx.Y;
        ToolBar.CaptureMouse();
    }
    private void OnToolbarMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_tbDrag) return;
        var p = e.GetPosition(this);
        TbTx.X = _tbOx + (p.X - _tbStart.X);
        TbTx.Y = _tbOy + (p.Y - _tbStart.Y);
    }
    private void OnToolbarUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _tbDrag = false; ToolBar.ReleaseMouseCapture();
    }

    // ---- mouse ----
    private void OnDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);
        GrowRightBottom(_start.X, _start.Y);
        if (_tool == "select") { SelectDown(e, _start); return; }
        if (_tool == "crop") { CropDown(e, _start); return; }
        if (_tool == "callout") _start = SnapToAnnotation(_start, out _calloutHost);   // 콜아웃 점을 근처 도형·선에 붙임 (붙은 도형 기억)

        if (_tool == "num") { PlaceNumber(_start); return; }
        if (_tool == "text") { PlaceTextBox(_start); return; }

        _dragging = true;
        Surface.CaptureMouse();   // 창/캔버스 밖으로 나가도 계속 그려지게
        if (_tool == "blur")
        {
            _blurLive = null;   // 드래그로 영역 결정 후 OnMove에서 생성
            return;
        }
        if (_tool == "rect")
        {
            _live = new Rectangle { Stroke = Accent, StrokeThickness = Stroke, Fill = _hatch ? HatchBrush() : Brushes.Transparent };
            AddShape(_live, _start);
        }
        else if (_tool == "ellipse")
        {
            _live = new Ellipse { Stroke = Accent, StrokeThickness = Stroke, Fill = _hatch ? HatchBrush() : Brushes.Transparent };
            AddShape(_live, _start);
        }
        else if (_tool == "pen")
        {
            var pl = new Polyline
            {
                Stroke = Accent, StrokeThickness = Stroke,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            pl.Points.Add(_start);
            _live = pl;
            Surface.Children.Add(pl);
        }
        else if (_tool == "hl")
        {
            var pl = new Polyline
            {
                Stroke = HlBrush, StrokeThickness = HlWidth,
                StrokeLineJoin = PenLineJoin.Miter, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
            };
            pl.Points.Add(new Point(_start.X - Margin, _start.Y - Margin));   // 레이어 로컬좌표
            _live = pl;
            _hlLayer?.Children.Add(pl);
        }
        else if (_tool == "arrow")
        {
            _live = new Line { Stroke = Accent, StrokeThickness = Stroke + 1, StrokeStartLineCap = PenLineCap.Round };
            Surface.Children.Add(_live);
        }
        else if (_tool == "callout")
        {
            var ln = new Line { Stroke = Accent, StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            ln.X1 = _start.X; ln.Y1 = _start.Y; ln.X2 = _start.X; ln.Y2 = _start.Y;
            _live = ln;
            Surface.Children.Add(ln);
        }
        else if (_tool == "spot")
        {
            EnsureSpotlight();
            _holes.Add(new Rect(_start, new Size(0, 0)));   // 새 구멍 추가
        }
    }

    private void OnMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_selDrag != SelDrag.None) { SelectMove(e.GetPosition(Surface)); return; }
        if (_cropDragging) { CropMove(e.GetPosition(Surface)); return; }
        if (!_dragging) return;
        var raw = e.GetPosition(Surface);
        GrowRightBottom(raw.X, raw.Y);
        var p = Clamp(raw);

        if (_tool == "blur")
        {
            var r = new Rect(new Point(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y)),
                             new Size(Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y)));
            if (_blurLive != null) Surface.Children.Remove(_blurLive);
            _blurLive = MakeMosaic(r);
            if (_blurLive != null) Surface.Children.Add(_blurLive);
            return;
        }
        if ((_tool == "rect" || _tool == "ellipse") && _live is Shape sh)
        {
            double x = Math.Min(_start.X, p.X), y = Math.Min(_start.Y, p.Y);
            Canvas.SetLeft(sh, x); Canvas.SetTop(sh, y);
            sh.Width = Math.Abs(p.X - _start.X); sh.Height = Math.Abs(p.Y - _start.Y);
        }
        else if (_tool == "hl" && _live is Polyline pl)
        {
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            var s = new Point(_start.X - Margin, _start.Y - Margin);
            var q = new Point(p.X - Margin, p.Y - Margin);
            if (shift) pl.Points = new PointCollection { s, new Point(q.X, s.Y) };   // 수평 직선(Y 고정)
            else pl.Points.Add(q);                                                    // 자유곡선
        }
        else if (_tool == "pen" && _live is Polyline pp)
        {
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (shift) pp.Points = new PointCollection { _start, p };   // 직선
            else pp.Points.Add(p);                                      // 자유곡선
        }
        else if (_tool == "arrow" && _live is Line ln)
        {
            ln.X1 = _start.X; ln.Y1 = _start.Y; ln.X2 = p.X; ln.Y2 = p.Y;
        }
        else if (_tool == "callout" && _live is Line cl)
        {
            cl.X2 = p.X; cl.Y2 = p.Y;
        }
        else if (_tool == "spot" && _holes.Count > 0)
        {
            _holes[^1] = new Rect(new Point(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y)),
                                  new Size(Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y)));
            RebuildSpotlight();
        }
    }

    private void OnUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_selDrag != SelDrag.None) { SelectUp(e.GetPosition(Surface)); return; }
        if (_cropDragging) { CropUp(e.GetPosition(Surface)); return; }
        if (!_dragging) return;
        _dragging = false;
        Surface.ReleaseMouseCapture();
        var raw = e.GetPosition(Surface);
        GrowRightBottom(raw.X, raw.Y);
        var p = Clamp(raw);

        if (_tool == "arrow" && _live is Line ln)
        {
            // drop tiny drags
            if (Dist(_start, p) < 6) { Surface.Children.Remove(ln); _live = null; return; }
            var from = new Point(ln.X1, ln.Y1);
            // 선을 화살촉 밑변까지만 그어 끝이 촉 밖으로 안 튀어나오게
            const double headLen = 16;
            double ang = Math.Atan2(p.Y - from.Y, p.X - from.X);
            double d = Dist(from, p);
            double back = Math.Min(headLen * 0.9, d);
            ln.X1 = from.X; ln.Y1 = from.Y;
            ln.X2 = p.X - back * Math.Cos(ang); ln.Y2 = p.Y - back * Math.Sin(ang);
            var head = ArrowHead(from, p);
            Surface.Children.Add(head);
            RegisterArrow(ln, head, from, p);
            PushAdded(Surface, ln, head);
        }
        else if ((_tool == "rect" || _tool == "ellipse") && _live is Shape sh)
        {
            if (sh.Width < 4 || sh.Height < 4) Surface.Children.Remove(sh);
            else PushAdded(Surface, sh);
        }
        else if (_tool == "hl" && _live is Polyline pl)
        {
            if (pl.Points.Count < 2 || Dist(_start, p) < 4) _hlLayer?.Children.Remove(pl);
            else if (_hlLayer != null) PushAdded(_hlLayer, pl);
        }
        else if (_tool == "pen" && _live is Polyline pp)
        {
            // 시작점·끝점 거리로 판단하면 닫힌 작은 원이 지워짐 → 실제로 그은 길이로 판단 (제자리 클릭만 버림)
            if (pp.Points.Count < 2 || PathLen(pp.Points) < 2) Surface.Children.Remove(pp);
            else
            {
                PushAdded(Surface, pp);
                if (PenAutoChk.IsChecked == true && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
                    TrySnapPen(pp);
            }
        }
        else if (_tool == "spot" && _holes.Count > 0)
        {
            var h = _holes[^1];
            if (h.Width < 6 || h.Height < 6) { _holes.RemoveAt(_holes.Count - 1); RebuildSpotlight(); }
            else Push(new EditCmd(   // undo = 이 구멍 제거
                () => { _holes.Remove(h); RebuildSpotlight(); },
                () => { EnsureSpotlight(); _holes.Add(h); RebuildSpotlight(); }));
        }
        else if (_tool == "blur")
        {
            if (_blurLive != null && _blurLive.Width >= 6 && _blurLive.Height >= 6) PushAdded(Surface, _blurLive);
            else if (_blurLive != null) Surface.Children.Remove(_blurLive);
            _blurLive = null;
        }
        else if (_tool == "callout" && _live is Line cl)
        {
            if (Dist(_start, p) < 6) { Surface.Children.Remove(cl); _live = null; return; }
            cl.X2 = p.X; cl.Y2 = p.Y;
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Accent };
            Canvas.SetLeft(dot, _start.X - 4); Canvas.SetTop(dot, _start.Y - 4);
            Surface.Children.Add(dot);
            PlaceCalloutText(cl, dot, new Point(p.X, p.Y));
        }

        bool rectDone = _tool == "rect" && _live is Rectangle rc && rc.Width >= 4 && rc.Height >= 4;
        _live = null;
        if (rectDone && AutoCalloutChk.IsChecked == true) SelectTool("callout");   // 네모 그린 뒤 콜아웃 자동
    }

    private UIElement? _calloutHost;   // 지금 그리는 콜아웃이 붙은 도형

    private void PlaceCalloutText(Line line, Ellipse dot, Point at)
    {
        var attachTo = _calloutHost;
        var fg = ContrastOn(_color.Color);
        double fs = _fontSize;
        var align = _align;
        var host = new System.Windows.Controls.Border
        {
            Background = _color, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
        };
        var tb = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = fg,
            FontSize = fs, FontWeight = FontWeights.Bold, MinWidth = 30, CaretBrush = fg,
            AcceptsReturn = true, TextAlignment = align,
        };
        host.Child = tb;
        Canvas.SetLeft(host, at.X); Canvas.SetTop(host, at.Y);
        Surface.Children.Add(host);
        host.SizeChanged += (_, _) => GrowRightBottom(at.X + host.ActualWidth, at.Y + host.ActualHeight);   // 입력 중 실시간 확장
        tb.Loaded += (_, _) => tb.Focus();

        void Done()
        {
            if (!Surface.Children.Contains(host)) return;
            Surface.Children.Remove(host);
            var text = tb.Text.Trim();
            if (text.Length == 0) { Surface.Children.Remove(line); Surface.Children.Remove(dot); return; }
            var chip = new System.Windows.Controls.Border
            {
                Background = _color, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock { Text = text, Foreground = fg, FontSize = fs, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = align },
            };
            Canvas.SetLeft(chip, at.X); Canvas.SetTop(chip, at.Y);
            Surface.Children.Add(chip);
            RegisterGroup(line, dot, chip);
            PushAdded(Surface, line, dot, chip);   // undo = 셋 다
            var parts = new UIElement[] { line, dot, chip };   // 한 배열을 공유해야 화살표(선+촉)에 붙어도 한 번만 움직임
            if (attachTo != null)   // 붙은 도형(화살표면 선·촉 어느 쪽을 잡아도)을 옮기면 따라오게
                foreach (var h in _groupOf.TryGetValue(attachTo, out var g) ? g : new[] { attachTo })
                    AttachCallout(h, parts);
            AttachChipEdit(chip);
            chip.Loaded += (_, _) => GrowRightBottom(at.X + chip.ActualWidth, at.Y + chip.ActualHeight);
        }
        tb.KeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape || (ke.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)) Done(); };
        tb.LostFocus += (_, _) => Done();
    }

    // ---- blur / mosaic ----
    private Image? MakeMosaic(Rect r)
    {
        try
        {
            double kx = _origW / _dispW, ky = _origH / _dispH;
            int sx = (int)((r.X - Margin) * kx), sy = (int)((r.Y - Margin) * ky);
            int sw = (int)(r.Width * kx), sh = (int)(r.Height * ky);
            sw = Math.Min(sw, _origW - sx); sh = Math.Min(sh, _origH - sy);
            if (sw < 2 || sh < 2 || sx < 0 || sy < 0) return null;

            var crop = new CroppedBitmap(_srcBmp, new Int32Rect(sx, sy, sw, sh));
            double block = Math.Max(1, Math.Max(sw, sh) / 16.0);   // ~16 blocks across
            var small = new TransformedBitmap(crop, new ScaleTransform(1.0 / block, 1.0 / block));
            small.Freeze();

            var img = new Image { Source = small, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, r.X); Canvas.SetTop(img, r.Y);
            return img;
        }
        catch { return null; }
    }

    // ---- text ----
    private void PlaceTextBox(Point at)
    {
        var fg = ContrastOn(_color.Color);
        double fs = _fontSize;
        var host = new System.Windows.Controls.Border
        {
            Background = _color, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
        };
        var tb = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = fg,
            FontSize = fs, FontWeight = FontWeights.Bold, MinWidth = 30, CaretBrush = fg,
            AcceptsReturn = true, TextAlignment = _align,
        };
        host.Child = tb;
        Canvas.SetLeft(host, at.X); Canvas.SetTop(host, at.Y);
        Surface.Children.Add(host);
        host.SizeChanged += (_, _) => GrowRightBottom(at.X + host.ActualWidth, at.Y + host.ActualHeight);   // 입력 중 실시간 확장
        tb.Loaded += (_, _) => tb.Focus();
        tb.KeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape || (ke.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)) FinalizeText(host, tb, at, fg, fs, _align); };
        tb.LostFocus += (_, _) => FinalizeText(host, tb, at, fg, fs, _align);
    }

    private void FinalizeText(System.Windows.Controls.Border host, TextBox tb, Point at, Brush fg, double fs, TextAlignment align)
    {
        if (!Surface.Children.Contains(host)) return;
        Surface.Children.Remove(host);
        var text = tb.Text.Trim();
        if (text.Length == 0) return;
        var chip = new System.Windows.Controls.Border
        {
            Background = _color, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock { Text = text, Foreground = fg, FontSize = fs, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = align },
        };
        Canvas.SetLeft(chip, at.X); Canvas.SetTop(chip, at.Y);
        Surface.Children.Add(chip);
        PushAdded(Surface, chip);
        AttachChipEdit(chip);
        chip.Loaded += (_, _) => GrowRightBottom(at.X + chip.ActualWidth, at.Y + chip.ActualHeight);
    }

    /// <summary>칩(텍스트/콜아웃) 더블클릭 → 인플레이스 텍스트 편집.</summary>
    private void AttachChipEdit(System.Windows.Controls.Border chip)
    {
        chip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            e.Handled = true;
            EditChip(chip);
        };
    }

    private void EditChip(System.Windows.Controls.Border chip)
    {
        if (chip.Child is not TextBlock tbk) return;
        string orig = tbk.Text;
        var fg = tbk.Foreground; double fs = tbk.FontSize; var align = tbk.TextAlignment;
        var tb = new TextBox
        {
            Text = orig, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = fg, FontSize = fs, FontWeight = FontWeights.Bold, MinWidth = 30,
            CaretBrush = fg, AcceptsReturn = true, TextAlignment = align,
        };
        chip.Child = tb;
        tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        chip.SizeChanged += ChipGrow;
        void ChipGrow(object? s, SizeChangedEventArgs e)
            => GrowRightBottom(Canvas.GetLeft(chip) + chip.ActualWidth, Canvas.GetTop(chip) + chip.ActualHeight);

        void Done()
        {
            if (chip.Child is not TextBox box) return;
            chip.SizeChanged -= ChipGrow;
            var t = box.Text.Trim();
            chip.Child = new TextBlock
            {
                Text = t.Length > 0 ? t : orig, Foreground = fg, FontSize = fs, FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap, TextAlignment = align,
            };
        }
        tb.KeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape || (ke.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)) Done(); };
        tb.LostFocus += (_, _) => Done();
    }

    private static Brush ContrastOn(Color bg)
    {
        double lum = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        return lum > 140 ? Brushes.Black : Brushes.White;
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));
    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double PathLen(IList<Point> pts)
    {
        double l = 0;
        for (int i = 1; i < pts.Count; i++) l += Dist(pts[i - 1], pts[i]);
        return l;
    }

    /// <summary>점 p와 직선 a-b 사이 거리.</summary>
    private static double PerpDist(Point p, Point a, Point b)
    {
        double len = Dist(a, b);
        if (len < 1e-6) return Dist(p, a);
        return Math.Abs((b.X - a.X) * (a.Y - p.Y) - (a.X - p.X) * (b.Y - a.Y)) / len;
    }

    // ---- 펜 자동 도형: 손으로 그린 원·화살표·직선을 깔끔한 도형으로 바꿈 (Ctrl+Z 한 번 = 원래 선) ----
    private void OnPenAuto(object sender, RoutedEventArgs e)
    {
        _settings.PenAutoShape = PenAutoChk.IsChecked == true;
        _settings.Save();
    }

    private void TrySnapPen(Polyline pp)
    {
        var pts = pp.Points.ToList();
        if (pts.Count < 3) return;
        Point s = pts[0], e = pts[^1];
        double minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X), minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
        double w = maxX - minX, h = maxY - minY, diag = Math.Sqrt(w * w + h * h);
        double len = PathLen(pts);

        UIElement[]? parts = null; string? label = null;

        bool closed = pts.Count >= 8 && w >= 12 && h >= 12 && Dist(s, e) < 0.2 * diag;

        // 0) 네모: 닫힌 선이 bbox 테두리에 붙어 있고 네 모서리를 다 지나감 (원보다 먼저 판정)
        if (closed)
        {
            double m = Math.Min(w, h), sum = 0, worst = 0;
            foreach (var q in pts)
            {
                double d = Math.Min(Math.Min(q.X - minX, maxX - q.X), Math.Min(q.Y - minY, maxY - q.Y)) / m;
                sum += d; worst = Math.Max(worst, d);
            }
            var corners = new[] { new Point(minX, minY), new Point(maxX, minY), new Point(maxX, maxY), new Point(minX, maxY) };
            bool allCorners = corners.All(c => pts.Any(q => Dist(q, c) < 0.15 * m + 3));
            if (sum / pts.Count < 0.05 && worst < 0.12 && allCorners)   // 원은 45° 지점이 테두리에서 0.15 이상 떨어져서 걸러짐
            {
                var rc = new Rectangle { Stroke = pp.Stroke, StrokeThickness = pp.StrokeThickness, Fill = _hatch ? HatchBrush() : Brushes.Transparent, Width = w, Height = h };
                Canvas.SetLeft(rc, minX); Canvas.SetTop(rc, minY);
                parts = new UIElement[] { rc }; label = "네모로 정리";
            }
        }

        // 1) 원/타원: 끝이 시작 근처로 돌아오고, 점들이 bbox 타원 위에 고르게 있음
        if (parts == null && closed && len > 2.2 * Math.Max(w, h))
        {
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, rx = w / 2, ry = h / 2;
            double sum = 0, worst = 0;
            foreach (var q in pts)
            {
                double dx = (q.X - cx) / rx, dy = (q.Y - cy) / ry;
                double dev = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1);
                sum += dev; worst = Math.Max(worst, dev);
            }
            if (sum / pts.Count < 0.12 && worst < 0.32)   // 네모(모서리 1.41배)는 여기서 걸러짐
            {
                if (Math.Abs(rx - ry) / Math.Max(rx, ry) < 0.12) { rx = ry = (rx + ry) / 2; }   // 거의 동그라면 정원
                var el = new Ellipse { Stroke = pp.Stroke, StrokeThickness = pp.StrokeThickness, Fill = Brushes.Transparent, Width = rx * 2, Height = ry * 2 };
                Canvas.SetLeft(el, cx - rx); Canvas.SetTop(el, cy - ry);
                parts = new UIElement[] { el }; label = "원으로 정리";
            }
        }

        // 2) 화살표: 거의 곧은 몸통 + 끝에서 되돌아오는 짧은 꺾임(V)
        if (parts == null)
        {
            int iE = 0; double far = 0;
            for (int i = 0; i < pts.Count; i++) { double d = Dist(s, pts[i]); if (d > far) { far = d; iE = i; } }
            var tip = pts[iE];
            if (far >= 30 && iE < pts.Count - 2)
            {
                double shaftDev = 0;
                for (int i = 0; i <= iE; i++) shaftDev = Math.Max(shaftDev, PerpDist(pts[i], s, tip));
                double tailMax = 0; int back = 0, tailN = 0;
                for (int i = iE + 1; i < pts.Count; i++)
                {
                    var t = pts[i];
                    tailMax = Math.Max(tailMax, Dist(tip, t));
                    tailN++;
                    if ((t.X - tip.X) * (s.X - tip.X) + (t.Y - tip.Y) * (s.Y - tip.Y) > 0) back++;   // 시작점 쪽으로 되돌아감
                }
                if (shaftDev < 0.1 * far && tailMax >= Math.Max(8, 0.06 * far) && tailMax <= 0.5 * far && back >= 0.7 * tailN)
                {
                    var ln = new Line { Stroke = pp.Stroke, StrokeThickness = pp.StrokeThickness + 1, StrokeStartLineCap = PenLineCap.Round };
                    const double headLen = 16;
                    double ang = Math.Atan2(tip.Y - s.Y, tip.X - s.X), bk = Math.Min(headLen * 0.9, far);
                    ln.X1 = s.X; ln.Y1 = s.Y; ln.X2 = tip.X - bk * Math.Cos(ang); ln.Y2 = tip.Y - bk * Math.Sin(ang);
                    var head = ArrowHead(s, tip);
                    head.Fill = pp.Stroke;
                    RegisterArrow(ln, head, s, tip);
                    parts = new UIElement[] { ln, head }; label = "화살표로 정리";
                }
            }
        }

        // 3) 직선: 전체가 거의 곧음
        if (parts == null)
        {
            double se = Dist(s, e);
            if (se >= 20)
            {
                double dev = 0;
                foreach (var q in pts) dev = Math.Max(dev, PerpDist(q, s, e));
                if (dev < Math.Max(2.5, 0.04 * se))
                {
                    var ln = new Line { X1 = s.X, Y1 = s.Y, X2 = e.X, Y2 = e.Y, Stroke = pp.Stroke, StrokeThickness = pp.StrokeThickness,
                                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    parts = new UIElement[] { ln }; label = "직선으로 정리";
                }
            }
        }

        if (parts == null) return;   // 자신 없으면 손그림 그대로
        if (parts.Length > 1) RegisterGroup(parts);
        var snapParts = parts;
        void ToShape() { Surface.Children.Remove(pp); foreach (var el in snapParts) if (!Surface.Children.Contains(el)) Surface.Children.Add(el); }
        void ToRaw() { foreach (var el in snapParts) Surface.Children.Remove(el); if (!Surface.Children.Contains(pp)) Surface.Children.Add(pp); Deselect(); }
        ToShape();
        Push(new EditCmd(ToRaw, ToShape));   // Ctrl+Z 한 번 = 원래 손그림, 한 번 더 = 지움
        FlashToast($"{label}  ·  Ctrl+Z = 원래 선");
    }

    private void AddShape(Shape s, Point at)
    {
        Canvas.SetLeft(s, at.X); Canvas.SetTop(s, at.Y);
        Surface.Children.Add(s);   // 실행취소 기록은 다 그린 뒤(OnUp)에
    }

    private Polygon ArrowHead(Point from, Point to)
    {
        double ang = Math.Atan2(to.Y - from.Y, to.X - from.X);
        double len = 16, spread = Math.PI / 7;
        var p1 = new Point(to.X - len * Math.Cos(ang - spread), to.Y - len * Math.Sin(ang - spread));
        var p2 = new Point(to.X - len * Math.Cos(ang + spread), to.Y - len * Math.Sin(ang + spread));
        return new Polygon { Points = new PointCollection { to, p1, p2 }, Fill = Accent };
    }

    private void PlaceNumber(Point at)
    {
        _num++;
        const double d = 18;
        var ell = new Ellipse { Width = d, Height = d, Fill = Accent };
        var tb = new TextBlock
        {
            Text = _num.ToString("D2"), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 9,
            Width = d, Height = d, TextAlignment = TextAlignment.Center,
        };
        tb.Padding = new Thickness(0, 2, 0, 0);
        var g = new Grid { Width = d, Height = d };
        g.Children.Add(ell); g.Children.Add(tb);
        Canvas.SetLeft(g, at.X - d / 2); Canvas.SetTop(g, at.Y - d / 2);
        Surface.Children.Add(g);
        PushAdded(Surface, g);
    }

    // ---- spotlight ----
    private void EnsureSpotlight()
    {
        if (_spotlight != null) return;
        _spotlight = new ShapePath { Fill = new SolidColorBrush(Color.FromArgb(DimAlpha, 0, 0, 0)), IsHitTestVisible = false };
        int idx = _hlLayer != null ? Surface.Children.IndexOf(_hlLayer) + 1 : 1;
        Surface.Children.Insert(idx, _spotlight);   // 이미지/형광펜 위, 다른 주석 아래
    }

    private void RebuildSpotlight()
    {
        if (_spotlight == null) return;
        if (_holes.Count == 0) { Surface.Children.Remove(_spotlight); _spotlight = null; return; }
        var grp = new GeometryGroup { FillRule = FillRule.EvenOdd };
        grp.Children.Add(new RectangleGeometry(new Rect(0, 0, Surface.Width, Surface.Height)));
        foreach (var h in _holes) grp.Children.Add(new RectangleGeometry(h, 6, 6));
        _spotlight.Data = grp;
    }

    // ---- toolbar ----
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_cropLive != null) { CancelCropLive(); return; }
        if (_undo.Count == 0) return;
        var c = _undo.Pop();
        c.Undo();
        _redo.Push(c);
        RefreshSelection();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var c = _redo.Pop();
        c.Redo();
        _undo.Push(c);
        RefreshSelection();
    }

    private RenderTargetBitmap RenderComposite()
    {
        // 이미지(자르기 적용) + (밖으로 삐져나간) 주석만 잘라서 원본 해상도 비율로 렌더 — 회색 여백 테두리는 안 붙음
        Deselect(); CancelCropLive();   // 선택 상자·자르기 표시는 결과에 안 들어가게
        var crop = ImageRect;
        foreach (UIElement el in Surface.Children)
        {
            if (el == _bg || el == BaseImage || el == _hlLayer || el == _spotlight) continue;
            if (el.Visibility != Visibility.Visible) continue;
            try
            {
                var b = VisualTreeHelper.GetDescendantBounds(el);
                if (b.IsEmpty) continue;
                crop.Union(el.TransformToAncestor(Surface).TransformBounds(b));
            }
            catch { }
        }
        crop.Intersect(new Rect(0, 0, Surface.Width, Surface.Height));
        double k = _origW / _dispW;
        int outW = Math.Max(1, (int)Math.Round(crop.Width * k)), outH = Math.Max(1, (int)Math.Round(crop.Height * k));
        var vb = new VisualBrush(Surface)
        {
            Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = crop,
        };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(vb, null, new Rect(0, 0, outW, outH));
        var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        // 클립보드는 다른 앱(우리 ClipboardWatcher 포함)이 잠깐 잡고 있으면 실패 → 몇 번 재시도 후, 성공하면 바로 닫기
        BitmapSource img;
        try { img = RenderComposite(); img.Freeze(); }
        catch (Exception ex) { FlashToast("복사 실패: " + ex.Message); return; }
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetImage(img); Close(); return; }
            catch { System.Threading.Thread.Sleep(60); }
        }
        FlashToast("복사 실패 (클립보드 사용 중) — 다시 눌러주세요");
    }

    private bool _toolBoxSync;
    private void OnDefaultTool(object sender, SelectionChangedEventArgs e)
    {
        if (_toolBoxSync || DefaultToolBox.SelectedItem is not ComboBoxItem it) return;
        _settings.DefaultTool = (string)it.Tag;
        _settings.Save();
        FlashToast($"다음부터 '{it.Content}' 도구로 시작");
    }

    /// <summary>지금 편집 결과를 화면에 고정(핀)하고 편집창은 닫음.</summary>
    private void OnPin(object sender, RoutedEventArgs e)
    {
        try
        {
            var img = RenderComposite(); img.Freeze();
            var settings = _settings;
            new PinWindow(img, null, settings, p => new AnnotationWindow(p, settings).Show()).Show();
            Close();
        }
        catch (Exception ex) { FlashToast("핀 실패: " + ex.Message); }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(_srcPath) ?? Capture.ScreenshotFolder(_settings);
            var name = Path.GetFileNameWithoutExtension(_srcPath) + "_편집.png";
            var dest = Unique(Path.Combine(dir, name));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(RenderComposite()));
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write)) enc.Save(fs);
            MetaFlash("저장: " + Path.GetFileName(dest));
        }
        catch (Exception ex) { MetaFlash("저장 실패: " + ex.Message); }
    }

    private static string Unique(string dest)
    {
        if (!File.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest)!;
        var n = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 1; ; i++) { var c = Path.Combine(dir, $"{n} ({i}){ext}"); if (!File.Exists(c)) return c; }
    }

    private void MetaFlash(string s) => Title = "캡처 편집 — " + s;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool inText = e.OriginalSource is TextBox;
        if (e.Key == System.Windows.Input.Key.Escape && _cropLive != null) { CancelCropLive(); SelectTool(_toolBeforeCrop); return; }
        if (e.Key == System.Windows.Input.Key.Escape && _sel != null && !inText) { Deselect(); return; }
        if (e.Key == System.Windows.Input.Key.Enter && _cropLive != null && !inText) { ApplyCrop(); return; }
        bool shiftKey = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        if (ctrl && (e.Key == System.Windows.Input.Key.Y || (e.Key == System.Windows.Input.Key.Z && shiftKey))) { OnRedo(this, new RoutedEventArgs()); return; }
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (_escArmed) { Close(); return; }
            _escArmed = true;
            FlashToast("한 번 더 ESC = 닫기");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            t.Tick += (_, _) => { t.Stop(); _escArmed = false; };
            t.Start();
        }
        else if (ctrl && e.Key == System.Windows.Input.Key.Z) OnUndo(this, new RoutedEventArgs());
        else if (ctrl && e.Key == System.Windows.Input.Key.C) OnCopy(this, new RoutedEventArgs());
        else if (ctrl && e.Key == System.Windows.Input.Key.S) OnSave(this, new RoutedEventArgs());
        else if (ctrl && (e.Key == System.Windows.Input.Key.OemPlus || e.Key == System.Windows.Input.Key.Add)) ZoomBy(+5);
        else if (ctrl && (e.Key == System.Windows.Input.Key.OemMinus || e.Key == System.Windows.Input.Key.Subtract)) ZoomBy(-5);
        else if (ctrl && (e.Key == System.Windows.Input.Key.D0 || e.Key == System.Windows.Input.Key.NumPad0)) OnZoomFit(this, new RoutedEventArgs());
        else if (!ctrl && e.OriginalSource is not TextBox)   // 숫자 단축키 (텍스트 입력 중엔 무시)
        {
            var t = e.Key switch
            {
                System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1 => "spot",
                System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2 => "arrow",
                System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3 => "rect",
                System.Windows.Input.Key.D4 or System.Windows.Input.Key.NumPad4 => "ellipse",
                System.Windows.Input.Key.D5 or System.Windows.Input.Key.NumPad5 => "pen",
                System.Windows.Input.Key.D6 or System.Windows.Input.Key.NumPad6 => "hl",
                System.Windows.Input.Key.D7 or System.Windows.Input.Key.NumPad7 => "blur",
                System.Windows.Input.Key.D8 or System.Windows.Input.Key.NumPad8 => "text",
                System.Windows.Input.Key.D9 or System.Windows.Input.Key.NumPad9 => "callout",
                System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0 => "num",
                System.Windows.Input.Key.V => "select",
                System.Windows.Input.Key.C => "crop",
                _ => null,
            };
            if (t != null) SelectTool(t);
        }
    }
}
