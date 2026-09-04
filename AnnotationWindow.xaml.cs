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
    private const double Margin = 140;      // 이미지 둘레 여백(마크업 여유)
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
    private readonly List<UIElement> _added = new();   // for undo (excludes base image)

    public AnnotationWindow(string path, Settings settings)
    {
        InitializeComponent();
        _srcPath = path;
        _settings = settings;

        var bmp = Load(path);
        _srcBmp = bmp;
        _origW = bmp.PixelWidth; _origH = bmp.PixelHeight;

        // fit to ~85% of the work area
        var wa = SystemParameters.WorkArea;
        double maxW = wa.Width * 0.85, maxH = wa.Height * 0.80;
        double scale = Math.Min(1.0, Math.Min(maxW / _origW, maxH / _origH));
        _dispW = Math.Max(1, _origW * scale);
        _dispH = Math.Max(1, _origH * scale);

        // 이미지 둘레 여백(여기에도 마크업 가능)
        Surface.Width = _dispW + 2 * Margin; Surface.Height = _dispH + 2 * Margin;

        // 배경(여백 포함) — index 0
        _bg = new Rectangle { Width = Surface.Width, Height = Surface.Height, Fill = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x33)) };
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

        MaxWidth = wa.Width; MaxHeight = wa.Height;   // 넘치면 스크롤

        BuildSwatches();
        SelectWidth(3);
        SetFont(12);
        BtnAL.Content = AlignIcon(HorizontalAlignment.Left);
        BtnAC.Content = AlignIcon(HorizontalAlignment.Center);
        BtnAR.Content = AlignIcon(HorizontalAlignment.Right);
        SelectAlign(TextAlignment.Left);
        SelectTool("arrow");
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
        var a = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));
        var n = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
        BtnAL.Background = al == TextAlignment.Left ? a : n;
        BtnAC.Background = al == TextAlignment.Center ? a : n;
        BtnAR.Background = al == TextAlignment.Right ? a : n;
    }

    private static readonly (string hex, byte r, byte g, byte b)[] Palette =
    {
        ("#FF3333", 0xFF, 0x33, 0x33), ("#FF9500", 0xFF, 0x95, 0x00), ("#FFEB3B", 0xFF, 0xEB, 0x3B),
        ("#33CC55", 0x33, 0xCC, 0x55), ("#2D8CFF", 0x2D, 0x8C, 0xFF), ("#FFFFFF", 0xFF, 0xFF, 0xFF), ("#111111", 0x11, 0x11, 0x11),
    };

    private void BuildSwatches()
    {
        foreach (var c in Palette)
        {
            var col = Color.FromRgb(c.r, c.g, c.b);
            var b = new System.Windows.Controls.Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 3, 0),
                Background = new SolidColorBrush(col), Cursor = System.Windows.Input.Cursors.Hand,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2),
                Tag = col,
            };
            b.MouseLeftButtonDown += (_, _) => SelectColor(col);
            Swatches.Children.Add(b);
        }
        SelectColor(Color.FromRgb(0xFF, 0x33, 0x33));
    }

    private void SelectColor(Color col)
    {
        _color = new SolidColorBrush(col);
        foreach (System.Windows.Controls.Border b in Swatches.Children)
            b.BorderBrush = (b.Tag is Color bc && bc == col) ? Brushes.White : Brushes.Transparent;
    }

    private void OnHatch(object sender, RoutedEventArgs e)
    {
        _hatch = !_hatch;
        BtnHatch.Background = _hatch
            ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF))
            : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
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
        var active = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));
        var normal = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
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
        _tool = t;
        var active = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));
        var normal = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
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
        BtnOpts.Background = show ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
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

        if (_tool == "num") { PlaceNumber(_start); return; }
        if (_tool == "text") { PlaceTextBox(_start); return; }

        _dragging = true;
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
            _added.Add(pl);
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
            _added.Add(pl);
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
        if (!_dragging) return;
        _dragging = false;
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
            _added.Add(new ArrowGroup(ln, head).Marker);   // track pair for undo
        }
        else if ((_tool == "rect" || _tool == "ellipse") && _live is Shape sh && (sh.Width < 4 || sh.Height < 4))
        {
            Surface.Children.Remove(sh); _added.Remove(sh);
        }
        else if (_tool == "hl" && _live is Polyline pl && (pl.Points.Count < 2 || Dist(_start, p) < 4))
        {
            _hlLayer?.Children.Remove(pl); _added.Remove(pl);
        }
        else if (_tool == "pen" && _live is Polyline pp && (pp.Points.Count < 2 || Dist(_start, p) < 3))
        {
            Surface.Children.Remove(pp); _added.Remove(pp);
        }
        else if (_tool == "spot" && _holes.Count > 0)
        {
            var h = _holes[^1];
            if (h.Width < 6 || h.Height < 6) { _holes.RemoveAt(_holes.Count - 1); RebuildSpotlight(); }
            else _added.Add(new SpotMarker());   // undo = 마지막 구멍 제거
        }
        else if (_tool == "blur")
        {
            if (_blurLive != null && _blurLive.Width >= 6 && _blurLive.Height >= 6) _added.Add(_blurLive);
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

    private void PlaceCalloutText(Line line, Ellipse dot, Point at)
    {
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
            _added.Add(new GroupMarker(new UIElement[] { line, dot, chip }));   // undo = 셋 다
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
        _added.Add(chip);
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

    private void AddShape(Shape s, Point at)
    {
        Canvas.SetLeft(s, at.X); Canvas.SetTop(s, at.Y);
        Surface.Children.Add(s);
        _added.Add(s);
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
        _added.Add(g);
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
        if (_added.Count == 0) return;
        var last = _added[^1];
        _added.RemoveAt(_added.Count - 1);
        if (last is GroupMarker gm) { foreach (var el in gm.Parts) Surface.Children.Remove(el); }
        else if (last is ArrowGroup.MarkerElement m) { Surface.Children.Remove(m.Line); Surface.Children.Remove(m.Head); }
        else if (last is SpotMarker) { if (_holes.Count > 0) _holes.RemoveAt(_holes.Count - 1); RebuildSpotlight(); }
        else if (last is Polyline) { _hlLayer?.Children.Remove(last); Surface.Children.Remove(last); }   // 형광펜(레이어)·펜(서피스)
        else Surface.Children.Remove(last);
    }

    private RenderTargetBitmap RenderComposite()
    {
        // 전체 서피스(이미지+여백)를 원본 해상도 비율로 렌더
        double k = _origW / _dispW;
        int outW = (int)Math.Round(Surface.Width * k), outH = (int)Math.Round(Surface.Height * k);
        var vb = new VisualBrush(Surface) { Stretch = Stretch.Fill };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(vb, null, new Rect(0, 0, outW, outH));
        var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetImage(RenderComposite());
            FlashToast("복사되었습니다");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
        }
        catch { }
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
                _ => null,
            };
            if (t != null) SelectTool(t);
        }
    }
}

/// <summary>Undo token for one spotlight hole.</summary>
public class SpotMarker : System.Windows.UIElement { }

/// <summary>Undo token grouping several elements (e.g. callout line + dot + chip).</summary>
public class GroupMarker : System.Windows.UIElement
{
    public System.Windows.UIElement[] Parts;
    public GroupMarker(System.Windows.UIElement[] parts) => Parts = parts;
}

/// <summary>Ties an arrow's line + head together for single-step undo.</summary>
public class ArrowGroup
{
    public class MarkerElement : System.Windows.UIElement { public required Line Line; public required Polygon Head; }
    public MarkerElement Marker { get; }
    public ArrowGroup(Line line, Polygon head) => Marker = new MarkerElement { Line = line, Head = head };
}
