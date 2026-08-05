using System.Windows;

namespace Snatch;

/// <summary>Positions/stacks toast cards. Anchor configurable: Right/Left/Center (bottom) or Mouse.</summary>
public static class ToastManager
{
    private static readonly List<ToastWindow> _open = new();
    private const double Gap = 0; // margin already baked into card Border

    /// <summary>"Right" | "Left" | "Center" | "Mouse"</summary>
    public static string Position { get; set; } = "Right";

    private static double _mouseX, _mouseY; // anchor in DIU, captured on Register in Mouse mode

    public static void Register(ToastWindow w)
    {
        if (!_open.Contains(w)) _open.Add(w);
        if (Position == "Mouse") CaptureMouseAnchor(w);
        Reflow();
    }

    public static void Unregister(ToastWindow w)
    {
        _open.Remove(w);
        Reflow();
    }

    private static void CaptureMouseAnchor(ToastWindow w)
    {
        var p = System.Windows.Forms.Cursor.Position; // physical px
        var ct = PresentationSource.FromVisual(w)?.CompositionTarget;
        double fx = ct?.TransformFromDevice.M11 ?? 1.0;
        double fy = ct?.TransformFromDevice.M22 ?? 1.0;
        _mouseX = p.X * fx;
        _mouseY = p.Y * fy;
    }

    private static void Reflow()
    {
        if (_open.Count == 0) return;

        // 마우스 포인터가 있는 모니터의 작업영역(DIU) 기준 — 멀티모니터 대응
        var (waL, waT, waR, waB) = CursorWorkAreaDiu(_open[^1]);

        double baseLeft, baseBottom;
        switch (Position)
        {
            case "Left": baseLeft = waL; baseBottom = waB; break;
            case "Center": baseLeft = (waL + waR) / 2; baseBottom = waB; break;
            case "Mouse": baseLeft = _mouseX; baseBottom = _mouseY; break;
            default: baseLeft = waR; baseBottom = waB; break; // Right
        }

        double y = baseBottom;
        for (int i = _open.Count - 1; i >= 0; i--) // newest sits lowest
        {
            var w = _open[i];
            double h = w.ActualHeight > 0 ? w.ActualHeight : 90;
            double left = Position switch
            {
                "Left" => baseLeft,
                "Center" => baseLeft - w.Width / 2,
                "Mouse" => baseLeft,
                _ => baseLeft - w.Width, // Right
            };
            left = Math.Max(waL, Math.Min(left, waR - w.Width));
            double top = y - h;
            top = Math.Max(waT, Math.Min(top, waB - h));
            w.Left = left;
            w.Top = top;
            y -= (h + Gap);
        }
    }

    private static (double L, double T, double R, double B) CursorWorkAreaDiu(ToastWindow any)
    {
        var c = System.Windows.Forms.Cursor.Position; // physical px
        var wp = System.Windows.Forms.Screen.FromPoint(c).WorkingArea; // physical px
        var ct = PresentationSource.FromVisual(any)?.CompositionTarget;
        double fx = ct?.TransformFromDevice.M11 ?? 1.0;
        double fy = ct?.TransformFromDevice.M22 ?? 1.0;
        return (wp.Left * fx, wp.Top * fy, wp.Right * fx, wp.Bottom * fy);
    }
}
