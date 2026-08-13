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
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33));
    private const double Stroke = 3;

    private Point _start;
    private bool _dragging;
    private Shape? _live;              // shape being dragged
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
        _origW = bmp.PixelWidth; _origH = bmp.PixelHeight;

        // fit to ~85% of the work area
        var wa = SystemParameters.WorkArea;
        double maxW = wa.Width * 0.85, maxH = wa.Height * 0.80;
        double scale = Math.Min(1.0, Math.Min(maxW / _origW, maxH / _origH));
        _dispW = Math.Max(1, _origW * scale);
        _dispH = Math.Max(1, _origH * scale);

        Surface.Width = _dispW; Surface.Height = _dispH;
        BaseImage.Source = bmp; BaseImage.Width = _dispW; BaseImage.Height = _dispH;

        SelectTool("arrow");
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
        foreach (var (btn, tag) in new[] { (BtnSpot, "spot"), (BtnArrow, "arrow"), (BtnRect, "rect"), (BtnEll, "ellipse"), (BtnNum, "num") })
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

    private void ShowDimToast()
    {
        DimToastText.Text = $"투명도 {_dimTransPct}%";
        var kf = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        kf.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
        kf.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150))));
        DimToast.BeginAnimation(OpacityProperty, kf);
    }

    private void OnTitleDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    // ---- mouse ----
    private void OnDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);

        if (_tool == "num") { PlaceNumber(_start); return; }

        _dragging = true;
        if (_tool == "rect")
        {
            _live = new Rectangle { Stroke = Accent, StrokeThickness = Stroke, Fill = Brushes.Transparent };
            AddShape(_live, _start);
        }
        else if (_tool == "ellipse")
        {
            _live = new Ellipse { Stroke = Accent, StrokeThickness = Stroke, Fill = Brushes.Transparent };
            AddShape(_live, _start);
        }
        else if (_tool == "arrow")
        {
            _live = new Line { Stroke = Accent, StrokeThickness = Stroke + 1, StrokeStartLineCap = PenLineCap.Round };
            Surface.Children.Add(_live);
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
        var p = Clamp(e.GetPosition(Surface));

        if ((_tool == "rect" || _tool == "ellipse") && _live is Shape sh)
        {
            double x = Math.Min(_start.X, p.X), y = Math.Min(_start.Y, p.Y);
            Canvas.SetLeft(sh, x); Canvas.SetTop(sh, y);
            sh.Width = Math.Abs(p.X - _start.X); sh.Height = Math.Abs(p.Y - _start.Y);
        }
        else if (_tool == "arrow" && _live is Line ln)
        {
            ln.X1 = _start.X; ln.Y1 = _start.Y; ln.X2 = p.X; ln.Y2 = p.Y;
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
        var p = Clamp(e.GetPosition(Surface));

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
        else if (_tool == "spot" && _holes.Count > 0)
        {
            var h = _holes[^1];
            if (h.Width < 6 || h.Height < 6) { _holes.RemoveAt(_holes.Count - 1); RebuildSpotlight(); }
            else _added.Add(new SpotMarker());   // undo = 마지막 구멍 제거
        }
        _live = null;
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _dispW), Math.Clamp(p.Y, 0, _dispH));
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
        Surface.Children.Insert(1, _spotlight);   // 이미지 위, 다른 주석 아래
    }

    private void RebuildSpotlight()
    {
        if (_spotlight == null) return;
        if (_holes.Count == 0) { Surface.Children.Remove(_spotlight); _spotlight = null; return; }
        var grp = new GeometryGroup { FillRule = FillRule.EvenOdd };
        grp.Children.Add(new RectangleGeometry(new Rect(0, 0, _dispW, _dispH)));
        foreach (var h in _holes) grp.Children.Add(new RectangleGeometry(h, 6, 6));
        _spotlight.Data = grp;
    }

    // ---- toolbar ----
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_added.Count == 0) return;
        var last = _added[^1];
        _added.RemoveAt(_added.Count - 1);
        if (last is ArrowGroup.MarkerElement m) { Surface.Children.Remove(m.Line); Surface.Children.Remove(m.Head); }
        else if (last is SpotMarker) { if (_holes.Count > 0) _holes.RemoveAt(_holes.Count - 1); RebuildSpotlight(); }
        else Surface.Children.Remove(last);
    }

    private RenderTargetBitmap RenderComposite()
    {
        // draw the display surface scaled up to original resolution
        var vb = new VisualBrush(Surface) { Stretch = Stretch.Fill };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(vb, null, new Rect(0, 0, _origW, _origH));
        var rtb = new RenderTargetBitmap(_origW, _origH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetImage(RenderComposite()); Close(); } catch { }
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
        if (e.Key == System.Windows.Input.Key.Escape) Close();
        else if (ctrl && e.Key == System.Windows.Input.Key.Z) OnUndo(this, new RoutedEventArgs());
        else if (ctrl && e.Key == System.Windows.Input.Key.C) OnCopy(this, new RoutedEventArgs());
        else if (ctrl && e.Key == System.Windows.Input.Key.S) OnSave(this, new RoutedEventArgs());
    }
}

/// <summary>Undo token for one spotlight hole.</summary>
public class SpotMarker : System.Windows.UIElement { }

/// <summary>Ties an arrow's line + head together for single-step undo.</summary>
public class ArrowGroup
{
    public class MarkerElement : System.Windows.UIElement { public required Line Line; public required Polygon Head; }
    public MarkerElement Marker { get; }
    public ArrowGroup(Line line, Polygon head) => Marker = new MarkerElement { Line = line, Head = head };
}
