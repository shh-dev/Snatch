using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Point = System.Windows.Point;

namespace Snatch;

/// <summary>편집 기본기: 선택(이동·크기·삭제·색 바꾸기·방향키) + 자르기.</summary>
public partial class AnnotationWindow
{
    // ======================= 선택 =======================
    private UIElement[]? _sel;                 // 선택된 주석(한 덩어리)
    private Canvas? _selLayer;                 // 선택 상자 + 손잡이 8개
    private enum SelDrag { None, Move, Resize }
    private SelDrag _selDrag;
    private int _selHandle;
    private Point _selStartP;
    private Rect _selB0;
    private ElemState[]? _selStates;

    private static readonly Brush SelBlue = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    /// <summary>이동·크기 조절 전후 상태 (되돌리기용).</summary>
    private sealed class ElemState
    {
        public double L, T, W, H, X1, Y1, X2, Y2, Fs;
        public PointCollection? Pts;
        public Point From, Tip;
    }

    private ElemState Cap(UIElement el)
    {
        var s = new ElemState { L = Canvas.GetLeft(el), T = Canvas.GetTop(el), W = double.NaN, H = double.NaN };
        if (el is FrameworkElement fe) { s.W = fe.Width; s.H = fe.Height; }
        if (el is Line ln) { s.X1 = ln.X1; s.Y1 = ln.Y1; s.X2 = ln.X2; s.Y2 = ln.Y2; }
        if (el is Polyline pl) s.Pts = pl.Points.Clone();
        else if (el is Polygon pg) s.Pts = pg.Points.Clone();
        if (el is Border b && b.Child is TextBlock tb) s.Fs = tb.FontSize;
        if (_arrowOf.TryGetValue(el, out var ai)) { s.From = ai.From; s.Tip = ai.Tip; }
        return s;
    }

    /// <summary>저장해 둔 상태 s에 표면 좌표 변환 d(이동+배율)를 적용. d=Identity면 그대로 복원.</summary>
    private void ApplyState(UIElement el, ElemState s, Matrix d)
    {
        if (_arrowOf.TryGetValue(el, out var ai))
        {
            if (el == ai.Line) { ai.From = d.Transform(s.From); ai.Tip = d.Transform(s.Tip); RebuildArrow(ai); }
            return;   // 촉은 선이 다시 그림
        }
        switch (el)
        {
            case Line ln:
            {
                var a = d.Transform(new Point(s.X1, s.Y1)); var b = d.Transform(new Point(s.X2, s.Y2));
                ln.X1 = a.X; ln.Y1 = a.Y; ln.X2 = b.X; ln.Y2 = b.Y;
                break;
            }
            case Polyline pl when s.Pts != null:
                pl.Points = new PointCollection(s.Pts.Select(d.Transform)); break;
            case Polygon pg when s.Pts != null:
                pg.Points = new PointCollection(s.Pts.Select(d.Transform)); break;
            case Border b:
            {
                var tl = d.Transform(new Point(s.L, s.T));
                Canvas.SetLeft(b, tl.X); Canvas.SetTop(b, tl.Y);
                if (b.Child is TextBlock tb && s.Fs > 0)
                    tb.FontSize = Math.Clamp(s.Fs * Math.Sqrt(Math.Abs(d.M11 * d.M22)), 6, 300);   // 글자는 크기 조절 = 폰트 크기
                break;
            }
            case Grid g:   // 넘버 배지: 크기는 그대로, 가운데만 이동
            {
                double w = double.IsNaN(s.W) ? g.ActualWidth : s.W, h = double.IsNaN(s.H) ? g.ActualHeight : s.H;
                var c = d.Transform(new Point(s.L + w / 2, s.T + h / 2));
                Canvas.SetLeft(g, c.X - w / 2); Canvas.SetTop(g, c.Y - h / 2);
                break;
            }
            case FrameworkElement fe when !double.IsNaN(s.L) && !double.IsNaN(s.W):   // 네모·원·블러 등
            {
                var a = d.Transform(new Point(s.L, s.T)); var b2 = d.Transform(new Point(s.L + s.W, s.T + s.H));
                Canvas.SetLeft(fe, Math.Min(a.X, b2.X)); Canvas.SetTop(fe, Math.Min(a.Y, b2.Y));
                fe.Width = Math.Max(1, Math.Abs(b2.X - a.X)); fe.Height = Math.Max(1, Math.Abs(b2.Y - a.Y));
                break;
            }
            default:
                if (!double.IsNaN(s.L))
                {
                    var tl = d.Transform(new Point(s.L, s.T));
                    Canvas.SetLeft(el, tl.X); Canvas.SetTop(el, tl.Y);
                }
                break;
        }
    }

    private bool IsSelectable(UIElement el) =>
        el != _bg && el != BaseImage && el != _hlLayer && el != _spotlight && el != _selLayer && el != _cropLayer;

    /// <summary>점 근처(반경 5px)에서 맨 위에 있는 주석.</summary>
    private UIElement? HitAnnotation(Point p)
    {
        UIElement? best = null; int bestIdx = -1;
        VisualTreeHelper.HitTest(Surface, null, r =>
        {
            DependencyObject? v = r.VisualHit;
            while (v != null && VisualTreeHelper.GetParent(v) is DependencyObject par && par != Surface) v = par;
            if (v is UIElement top && IsSelectable(top))
            {
                int i = Surface.Children.IndexOf(top);
                if (i > bestIdx) { bestIdx = i; best = top; }
            }
            return HitTestResultBehavior.Continue;
        }, new GeometryHitTestParameters(new EllipseGeometry(p, 5, 5)));
        return best;
    }

    private Rect GroupBounds(UIElement[] els)
    {
        Surface.UpdateLayout();
        var r = Rect.Empty;
        foreach (var el in els)
        {
            if (!Surface.Children.Contains(el)) continue;
            try
            {
                var b = VisualTreeHelper.GetDescendantBounds(el);
                if (!b.IsEmpty) r.Union(el.TransformToAncestor(Surface).TransformBounds(b));
            }
            catch { }
        }
        return r;
    }

    private void SetSelection(UIElement[] els) { _sel = els; DrawSelection(); }

    private void Deselect()
    {
        _sel = null; _selDrag = SelDrag.None;
        if (_selLayer != null) Surface.Children.Remove(_selLayer);
    }

    /// <summary>실행취소 등으로 선택된 것이 사라졌으면 해제, 아니면 상자 다시 그림.</summary>
    private void RefreshSelection()
    {
        if (_sel == null) return;
        if (_sel.Any(el => !Surface.Children.Contains(el))) Deselect(); else DrawSelection();
    }

    private void DrawSelection()
    {
        if (_sel == null) return;
        var b = GroupBounds(_sel);
        if (b.IsEmpty) { Deselect(); return; }
        b.Inflate(4, 4);
        _selLayer ??= new Canvas();
        _selLayer.Children.Clear();
        if (!Surface.Children.Contains(_selLayer)) Surface.Children.Add(_selLayer);
        else if (Surface.Children[^1] != _selLayer) { Surface.Children.Remove(_selLayer); Surface.Children.Add(_selLayer); }

        var box = new Rectangle
        {
            Width = b.Width, Height = b.Height, Stroke = SelBlue, StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, b.Left); Canvas.SetTop(box, b.Top);
        _selLayer.Children.Add(box);

        var pts = new[]
        {
            new Point(b.Left, b.Top), new Point(b.Left + b.Width / 2, b.Top), new Point(b.Right, b.Top), new Point(b.Right, b.Top + b.Height / 2),
            new Point(b.Right, b.Bottom), new Point(b.Left + b.Width / 2, b.Bottom), new Point(b.Left, b.Bottom), new Point(b.Left, b.Top + b.Height / 2),
        };
        var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE };
        for (int i = 0; i < 8; i++)
        {
            const double hs = 9;
            var h = new Rectangle
            {
                Width = hs, Height = hs, Fill = Brushes.White, Stroke = SelBlue, StrokeThickness = 1.5,
                RadiusX = 2, RadiusY = 2, Tag = i, Cursor = cursors[i],
            };
            Canvas.SetLeft(h, pts[i].X - hs / 2); Canvas.SetTop(h, pts[i].Y - hs / 2);
            _selLayer.Children.Add(h);
        }
    }

    private static bool InTextBox(object? src)
    {
        var d = src as DependencyObject;
        while (d != null) { if (d is TextBox) return true; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d); }
        return false;
    }

    private void SelectDown(MouseButtonEventArgs e, Point p)
    {
        if (InTextBox(e.OriginalSource)) return;   // 글자 편집 중인 상자는 그대로
        if (_sel != null && e.OriginalSource is Rectangle h && h.Tag is int idx && _selLayer != null && _selLayer.Children.Contains(h))
        {
            _selDrag = SelDrag.Resize; _selHandle = idx;
        }
        else
        {
            var hit = HitAnnotation(p);
            if (hit == null) { Deselect(); return; }
            var grp = _groupOf.TryGetValue(hit, out var g) ? g : new[] { hit };
            if (_sel == null || !_sel.Contains(hit)) SetSelection(grp);
            _selDrag = SelDrag.Move;
        }
        _selStartP = p;
        _selStates = _sel!.Select(Cap).ToArray();
        CaptureFollowers(_sel!);
        _selB0 = GroupBounds(_sel!);
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void SelectMove(Point p)
    {
        if (_sel == null || _selStates == null) return;
        var d = Matrix.Identity;
        if (_selDrag == SelDrag.Move) d.Translate(p.X - _selStartP.X, p.Y - _selStartP.Y);
        else d = ResizeMatrix(p);
        for (int i = 0; i < _sel.Length; i++) ApplyState(_sel[i], _selStates[i], d);
        MoveFollowers(d);
        DrawSelection();
    }

    /// <summary>손잡이 위치로 배율 행렬 계산 (반대편 모서리 고정, Shift = 비율 유지).</summary>
    private Matrix ResizeMatrix(Point p)
    {
        var b0 = _selB0;
        int i = _selHandle;
        bool mL = i is 0 or 6 or 7, mR = i is 2 or 3 or 4, mT = i is 0 or 1 or 2, mB = i is 4 or 5 or 6;
        double ax = mL ? b0.Right : b0.Left, ay = mT ? b0.Bottom : b0.Top;
        double bw = Math.Max(1, b0.Width), bh = Math.Max(1, b0.Height);
        double sx = 1, sy = 1;
        if (mL) sx = Math.Max(4, ax - p.X) / bw;
        if (mR) sx = Math.Max(4, p.X - ax) / bw;
        if (mT) sy = Math.Max(4, ay - p.Y) / bh;
        if (mB) sy = Math.Max(4, p.Y - ay) / bh;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (shift && (mL || mR) && (mT || mB)) { double s = Math.Max(sx, sy); sx = sy = s; }
        var d = Matrix.Identity;
        d.Translate(-ax, -ay); d.Scale(sx, sy); d.Translate(ax, ay);
        return d;
    }

    private void SelectUp(Point p)
    {
        var mode = _selDrag;
        _selDrag = SelDrag.None;
        Surface.ReleaseMouseCapture();
        if (_sel == null || _selStates == null) return;
        if (Dist(p, _selStartP) < 1) return;   // 그냥 클릭(선택만)
        _ = mode;
        PushTransformWithFollowers(_sel, _selStates);
    }

    private void PushTransform(UIElement[] els, ElemState[] before, ElemState[] after)
    {
        Push(new EditCmd(
            () => { for (int i = 0; i < els.Length; i++) ApplyState(els[i], before[i], Matrix.Identity); },
            () => { for (int i = 0; i < els.Length; i++) ApplyState(els[i], after[i], Matrix.Identity); }));
    }

    /// <summary>방향키로 1px(Shift 10px) 이동.</summary>
    private void Nudge(double dx, double dy)
    {
        if (_sel == null) return;
        var before = _sel.Select(Cap).ToArray();
        CaptureFollowers(_sel);
        var d = Matrix.Identity; d.Translate(dx, dy);
        for (int i = 0; i < _sel.Length; i++) ApplyState(_sel[i], before[i], d);
        MoveFollowers(d);
        PushTransformWithFollowers(_sel, before);
        DrawSelection();
    }

    private void DeleteSelection()
    {
        if (_sel == null) return;
        var els = _sel;
        Deselect();
        foreach (var el in els) Surface.Children.Remove(el);
        Push(new EditCmd(
            () => { foreach (var el in els) if (!Surface.Children.Contains(el)) Surface.Children.Add(el); },
            () => { foreach (var el in els) Surface.Children.Remove(el); Deselect(); }));
    }

    // ---- 색 바꾸기 ----
    private sealed class ColorState { public Brush? Stroke, Fill, Bg, Fg; public Brush?[]? Badge; }

    private static ColorState CapColor(UIElement el)
    {
        var s = new ColorState();
        if (el is Shape sh) { s.Stroke = sh.Stroke; s.Fill = sh.Fill; }
        if (el is Border b) { s.Bg = b.Background; if (b.Child is TextBlock tb) s.Fg = tb.Foreground; }
        if (el is Grid g) s.Badge = g.Children.OfType<Ellipse>().Select(x => x.Fill).ToArray();
        return s;
    }

    private static void ApplyColor(UIElement el, ColorState s)
    {
        if (el is Shape sh) { sh.Stroke = s.Stroke; sh.Fill = s.Fill; }
        if (el is Border b) { b.Background = s.Bg; if (b.Child is TextBlock tb && s.Fg != null) tb.Foreground = s.Fg; }
        if (el is Grid g && s.Badge != null)
        {
            var ells = g.Children.OfType<Ellipse>().ToArray();
            for (int i = 0; i < ells.Length && i < s.Badge.Length; i++) ells[i].Fill = s.Badge[i];
        }
    }

    /// <summary>팔레트에서 색을 고르면 선택된 주석도 그 색으로.</summary>
    private void RecolorSelection(Color c)
    {
        if (_sel == null) return;
        var els = _sel;
        var before = els.Select(CapColor).ToArray();
        var br = new SolidColorBrush(c);
        foreach (var el in els)
        {
            if (el is Shape sh)
            {
                if (sh.Stroke != null) sh.Stroke = br;
                if (sh.Fill is SolidColorBrush f && f.Color.A > 0) sh.Fill = br;
                else if (sh.Fill is DrawingBrush) sh.Fill = HatchBrush();
            }
            else if (el is Border b && b.Child is TextBlock tb) { b.Background = br; tb.Foreground = ContrastOn(c); }
            else if (el is Grid g) foreach (var ell in g.Children.OfType<Ellipse>()) ell.Fill = br;
        }
        var after = els.Select(CapColor).ToArray();
        Push(new EditCmd(
            () => { for (int i = 0; i < els.Length; i++) ApplyColor(els[i], before[i]); },
            () => { for (int i = 0; i < els.Length; i++) ApplyColor(els[i], after[i]); }));
    }

    // ======================= 콜아웃 붙이기 =======================
    /// <summary>
    /// p가 도형 안이면 그 테두리로, 도형 테두리·선·펜 선에서 16px 안이면 그 위 가장 가까운 점으로 옮김. 해당 없으면 그대로.
    /// host = 붙은 도형(나중에 그 도형을 옮기면 콜아웃이 따라감). 안 붙었으면 null.
    /// </summary>
    private Point SnapToAnnotation(Point p, out UIElement? host)
    {
        const double radius = 16;
        Point best = p; double bestD = radius;
        UIElement? cur = null, bestHost = null;
        void Try(Point q) { double d = Dist(p, q); if (d < bestD) { bestD = d; best = q; bestHost = cur; } }

        static Point OnSegment(Point p, Point a, Point b)
        {
            var ab = b - a; double len2 = ab.X * ab.X + ab.Y * ab.Y;
            if (len2 < 1e-9) return a;
            double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
            return a + ab * t;
        }
        void TrySegments(IList<Point> pts, bool closed)
        {
            for (int i = 1; i < pts.Count; i++) Try(OnSegment(p, pts[i - 1], pts[i]));
            if (closed && pts.Count > 2) Try(OnSegment(p, pts[^1], pts[0]));
        }

        foreach (UIElement el in Surface.Children)
        {
            if (!IsSelectable(el) || el.Visibility != Visibility.Visible) continue;
            cur = el;
            switch (el)
            {
                case Line ln: Try(OnSegment(p, new Point(ln.X1, ln.Y1), new Point(ln.X2, ln.Y2))); break;
                case Polyline pl: TrySegments(pl.Points, false); break;
                case Polygon pg: TrySegments(pg.Points, true); break;
                case Ellipse ell when !double.IsNaN(Canvas.GetLeft(ell)) && ell.Width > 12:   // 콜아웃 점(8px)은 제외
                {
                    double rx = ell.Width / 2, ry = ell.Height / 2, cx = Canvas.GetLeft(ell) + rx, cy = Canvas.GetTop(ell) + ry;
                    double t = Math.Atan2((p.Y - cy) / ry, (p.X - cx) / rx);
                    var q = new Point(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t));
                    bool inside = Math.Pow((p.X - cx) / rx, 2) + Math.Pow((p.Y - cy) / ry, 2) <= 1;
                    if (inside) { best = q; bestD = 0; bestHost = el; } else Try(q);
                    break;
                }
                case FrameworkElement fe when (fe is Rectangle || fe is Border) && !double.IsNaN(Canvas.GetLeft(fe)):
                {
                    double l = Canvas.GetLeft(fe), t = Canvas.GetTop(fe);
                    double w = double.IsNaN(fe.Width) ? fe.ActualWidth : fe.Width, h = double.IsNaN(fe.Height) ? fe.ActualHeight : fe.Height;
                    var r = new Rect(l, t, w, h);
                    var c = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
                    if (r.Contains(p))   // 안쪽 클릭 → 가장 가까운 변으로 (거리 상관없이)
                    {
                        var q = c.Select((a, i) => OnSegment(p, a, c[(i + 1) % 4])).MinBy(x => Dist(p, x));
                        best = q; bestD = 0; bestHost = el;
                    }
                    else TrySegments(c, true);
                    break;
                }
            }
        }
        host = bestHost;
        return best;
    }

    // ---- 붙은 콜아웃 따라오기: 도형을 옮기면 콜아웃 통째로, 크기를 바꾸면 콜아웃 점만 따라감 ----
    private readonly Dictionary<UIElement, List<UIElement[]>> _attached = new();   // 도형 → [선, 점, 글자칩]
    private UIElement[][]? _followers;
    private ElemState[][]? _followerStates;

    private void AttachCallout(UIElement host, UIElement[] callout)
    {
        if (!_attached.TryGetValue(host, out var list)) _attached[host] = list = new();
        list.Add(callout);
    }

    /// <summary>선택된 도형들에 붙은 콜아웃 (콜아웃 자체가 같이 선택돼 있으면 제외).</summary>
    private void CaptureFollowers(UIElement[] sel)
    {
        _followers = sel.SelectMany(h => _attached.TryGetValue(h, out var l) ? l : Enumerable.Empty<UIElement[]>())
            .Distinct().Where(c => !c.Any(sel.Contains) && Surface.Children.Contains(c[0])).ToArray();
        _followerStates = _followers.Select(c => c.Select(Cap).ToArray()).ToArray();
    }

    private void MoveFollowers(Matrix d)
    {
        if (_followers == null || _followerStates == null) return;
        bool pureMove = Math.Abs(d.M11 - 1) < 1e-9 && Math.Abs(d.M22 - 1) < 1e-9;
        for (int k = 0; k < _followers.Length; k++)
        {
            var c = _followers[k]; var s = _followerStates[k];
            if (pureMove) { for (int i = 0; i < c.Length; i++) ApplyState(c[i], s[i], d); continue; }
            // 크기 조절: 붙은 점(선 시작 + 동그라미)만 도형과 같이, 글자칩은 그대로
            if (c[0] is Line ln)
            {
                var a = d.Transform(new Point(s[0].X1, s[0].Y1));
                ln.X1 = a.X; ln.Y1 = a.Y; ln.X2 = s[0].X2; ln.Y2 = s[0].Y2;
                if (c.Length > 1 && c[1] is FrameworkElement dot)
                {
                    double r = (double.IsNaN(s[1].W) ? dot.ActualWidth : s[1].W) / 2;
                    Canvas.SetLeft(dot, a.X - r); Canvas.SetTop(dot, a.Y - r);
                }
            }
        }
    }

    /// <summary>선택 + 따라온 콜아웃을 한 번의 되돌리기로.</summary>
    private void PushTransformWithFollowers(UIElement[] sel, ElemState[] selBefore)
    {
        var els = sel.Concat(_followers?.SelectMany(c => c) ?? Enumerable.Empty<UIElement>()).ToArray();
        var before = selBefore.Concat(_followerStates?.SelectMany(s => s) ?? Enumerable.Empty<ElemState>()).ToArray();
        PushTransform(els, before, els.Select(Cap).ToArray());
        _followers = null; _followerStates = null;
    }

    // ======================= 자르기 =======================
    private Rect? _cropRect;           // 적용된 자르기 (표면 좌표). null = 원본 전체
    private Rect? _cropLive;           // 드래그 중/확정 대기 영역
    private Canvas? _cropLayer;
    private bool _cropDragging, _cropMoved;
    private string _toolBeforeCrop = "arrow";

    private Rect FullImageRect => new(Margin, Margin, _dispW, _dispH);
    private Rect ImageRect => _cropRect ?? FullImageRect;

    private void CropDown(MouseButtonEventArgs e, Point p)
    {
        if (e.ClickCount >= 2 && _cropLive != null) { ApplyCrop(); e.Handled = true; return; }
        _cropDragging = true; _cropMoved = false;   // 클릭만 하면 기존 영역 유지 (더블클릭 적용 위해)
        _start = p;
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void CropMove(Point p)
    {
        if (!_cropMoved && Dist(p, _start) < 3) return;
        _cropMoved = true;
        var r = new Rect(_start, p);
        r.Intersect(FullImageRect);
        _cropLive = r.IsEmpty ? null : r;
        DrawCropLive();
    }

    private void CropUp(Point p)
    {
        _cropDragging = false;
        Surface.ReleaseMouseCapture();
        if (!_cropMoved) return;
        if (_cropLive is not { } r || r.Width < 8 || r.Height < 8) { CancelCropLive(); return; }
        FlashToast("Enter·더블클릭 = 자르기  ·  Esc = 취소");
    }

    private void DrawCropLive()
    {
        _cropLayer ??= new Canvas { IsHitTestVisible = false };
        _cropLayer.Children.Clear();
        if (!Surface.Children.Contains(_cropLayer)) Surface.Children.Add(_cropLayer);
        if (_cropLive is not { } r) return;
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(FullImageRect));
        dim.Children.Add(new RectangleGeometry(r));
        _cropLayer.Children.Add(new System.Windows.Shapes.Path { Data = dim, Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) });
        var box = new Rectangle
        {
            Width = r.Width, Height = r.Height, Stroke = Brushes.White, StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 5, 3 },
        };
        Canvas.SetLeft(box, r.X); Canvas.SetTop(box, r.Y);
        _cropLayer.Children.Add(box);
    }

    private void CancelCropLive()
    {
        _cropLive = null; _cropDragging = false;
        if (_cropLayer != null) Surface.Children.Remove(_cropLayer);
    }

    private void ApplyCrop()
    {
        if (_cropLive is not { } r) return;
        var before = _cropRect;
        Rect? after = r;
        SetCrop(after);
        Push(new EditCmd(() => SetCrop(before), () => SetCrop(after)));
        CancelCropLive();
        SelectTool(_toolBeforeCrop);
        FlashToast("잘랐어요  ·  Ctrl+Z = 되돌리기");
    }

    /// <summary>이미지(와 형광펜 층)를 자르기 영역만 보이게. 주석은 그대로.</summary>
    private void SetCrop(Rect? r)
    {
        _cropRect = r;
        Geometry? clip = r is { } c ? new RectangleGeometry(new Rect(c.X - Margin, c.Y - Margin, c.Width, c.Height)) : null;
        BaseImage.Clip = clip;
        if (_hlLayer != null) _hlLayer.Clip = clip;
    }

    // ======================= 키보드 =======================
    /// <summary>방향키는 스크롤보다 먼저 받아야 해서 Preview에서 처리.</summary>
    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (_sel == null || InTextBox(e.OriginalSource)) return;
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: Nudge(-step, 0); e.Handled = true; break;
            case Key.Right: Nudge(step, 0); e.Handled = true; break;
            case Key.Up: Nudge(0, -step); e.Handled = true; break;
            case Key.Down: Nudge(0, step); e.Handled = true; break;
            case Key.Delete: case Key.Back: DeleteSelection(); e.Handled = true; break;
        }
    }
}
