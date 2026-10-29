using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Snatch;

/// <summary>
/// 커서 옆에 뜨는 퀵 히스토리 — 최근 10개에 1~0 번호.
/// 숫자 하나 = 바로 붙여넣기(그대로). 숫자 여러 개(예: 1 3 4)·클릭·Space = 여러 개 선택(순서 배지),
/// A = 전체, Enter = 선택한 순서대로 하나씩 붙여넣기.
/// </summary>
public partial class QuickPasteWindow : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    private readonly List<HistoryItem> _items;
    private readonly Action<HistoryItem> _onPick;
    private readonly Action<List<HistoryItem>> _onPickMany;

    private readonly List<int> _order = new();            // 선택 순서 (행 index)
    private readonly List<Border> _rows = new();
    private readonly List<Border> _badges = new();         // 행마다 선택 순서 배지
    private int _cursor;                                   // 방향키 하이라이트
    private System.Windows.Threading.DispatcherTimer? _singleTimer;   // 숫자 하나만 누르면 잠깐 뒤 바로 붙여넣기

    private static readonly Brush RowNormal = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowCursor = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowSelected = new SolidColorBrush(Color.FromArgb(0x55, 0x0A, 0x84, 0xFF));
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    public QuickPasteWindow(List<HistoryItem> items, Action<HistoryItem> onPick, Action<List<HistoryItem>> onPickMany)
    {
        InitializeComponent();
        _items = items;
        _onPick = onPick;
        _onPickMany = onPickMany;
        Build();
        Loaded += OnLoaded;
    }

    private void Build()
    {
        for (int i = 0; i < _items.Count && i < 10; i++)
        {
            int idx = i;
            int label = (i + 1) % 10;   // 1..9, 0
            Rows.Children.Add(MakeRow(_items[i], label, idx));
        }
        Refresh();
    }

    private Border MakeRow(HistoryItem item, int label, int idx)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var num = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label.ToString(), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Cursor = Cursors.Hand, ToolTip = "선택/해제",
        };
        num.MouseLeftButtonUp += (_, e) => { e.Handled = true; Toggle(idx); };
        Grid.SetColumn(num, 0); grid.Children.Add(num);

        var box = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) };
        if (item.Kind == "color") { try { box.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.ColorHex!)); } catch { } }
        else if (ClipHistory.Thumb(item, 60) is { } th) box.Child = new Image { Source = th, Stretch = Stretch.UniformToFill };
        else box.Child = new TextBlock { Text = "T", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(box, 1); grid.Children.Add(box);

        var txt = new TextBlock
        {
            Text = item.Kind == "image" ? System.IO.Path.GetFileName(item.File ?? "이미지") : (item.Text ?? "").Trim(),
            Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(txt, 2); grid.Children.Add(txt);

        // 선택 순서 배지 (①②③)
        var badge = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Blue, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            Child = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Grid.SetColumn(badge, 3); grid.Children.Add(badge);
        _badges.Add(badge);

        var row = new Border
        {
            Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(8),
            Background = RowNormal, Cursor = Cursors.Hand,
        };
        row.Child = grid;
        row.MouseEnter += (_, _) => { _cursor = idx; Refresh(); };
        // 아무것도 안 골랐으면 클릭 = 바로 붙여넣기(예전 그대로), 골라 둔 게 있거나 Ctrl+클릭이면 선택/해제
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (_order.Count == 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0) Pick(idx);
            else Toggle(idx);
        };
        _rows.Add(row);
        return row;
    }

    private void Toggle(int idx)
    {
        if (idx < 0 || idx >= _rows.Count) return;
        CancelSingle();
        if (_order.Contains(idx)) _order.Remove(idx); else _order.Add(idx);
        Refresh();
    }

    private void Refresh()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            int pos = _order.IndexOf(i);
            _rows[i].Background = pos >= 0 ? RowSelected : (i == _cursor ? RowCursor : RowNormal);
            _badges[i].Visibility = pos >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_badges[i].Child is TextBlock tb) tb.Text = (pos + 1).ToString();
        }
        Hint.Text = _order.Count > 0
            ? $"{_order.Count}개 선택 · Enter 차례로 붙여넣기 · 번호 다시 누르면 해제"
            : "번호 여러 개 = 선택 · A 전체 · Enter 차례로 붙여넣기";
    }

    private void OnLoaded(object? s, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        double dpiX = 1, dpiY = 1;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct) { dpiX = ct.TransformToDevice.M11; dpiY = ct.TransformToDevice.M22; }
        if (GetCursorPos(out var p))
        {
            int wpx = (int)(ActualWidth * dpiX), hpx = (int)(ActualHeight * dpiY);
            int x = p.X + (int)(12 * dpiX), y = p.Y + (int)(12 * dpiY);
            var mon = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).Bounds;
            if (x + wpx > mon.Right) x = mon.Right - wpx;
            if (y + hpx > mon.Bottom) y = mon.Bottom - hpx;
            if (x < mon.Left) x = mon.Left; if (y < mon.Top) y = mon.Top;
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
        Activate();
    }

    private void Pick(int idx)
    {
        if (idx < 0 || idx >= _items.Count) return;
        CancelSingle();
        var item = _items[idx];
        Close();
        _onPick(item);
    }

    private void PickMany()
    {
        CancelSingle();
        if (_order.Count == 0) { Pick(_cursor); return; }
        if (_order.Count == 1) { Pick(_order[0]); return; }
        var list = _order.Where(i => i < _items.Count).Select(i => _items[i]).ToList();
        Close();
        _onPickMany(list);
    }

    /// <summary>머리말에 이 창을 여는 단축키 표시 (예: Alt+V).</summary>
    public void SetHotkeyLabel(string hk) { if (!string.IsNullOrEmpty(hk) && hk != "(없음)") Header.Text = $"붙여넣기 — 숫자 키  ·  {hk}"; }

    private void CancelSingle() { _singleTimer?.Stop(); _singleTimer = null; }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.Enter) { e.Handled = true; PickMany(); return; }
        if (e.Key == Key.A) { e.Handled = true; CancelSingle(); _order.Clear(); for (int i = 0; i < _rows.Count; i++) _order.Add(i); Refresh(); return; }
        if (e.Key == Key.Space) { e.Handled = true; Toggle(_cursor); return; }
        if (e.Key == Key.Down) { e.Handled = true; _cursor = Math.Min(_rows.Count - 1, _cursor + 1); Refresh(); return; }
        if (e.Key == Key.Up) { e.Handled = true; _cursor = Math.Max(0, _cursor - 1); Refresh(); return; }

        int d = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1, Key.D2 or Key.NumPad2 => 2, Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4, Key.D5 or Key.NumPad5 => 5, Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7, Key.D8 or Key.NumPad8 => 8, Key.D9 or Key.NumPad9 => 9,
            Key.D0 or Key.NumPad0 => 10, _ => -1,
        };
        if (d <= 0 || d - 1 >= _rows.Count) return;
        e.Handled = true;
        int idx = d - 1;

        if (_order.Count == 0)
        {
            // 첫 숫자: 선택해 두고 잠깐 기다림 → 다른 숫자가 안 오면 예전처럼 바로 붙여넣기
            _order.Add(idx); Refresh();
            _singleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _singleTimer.Tick += (_, _) => { CancelSingle(); if (_order.Count == 1) Pick(_order[0]); };
            _singleTimer.Start();
        }
        else Toggle(idx);   // 두 번째 숫자부터 = 여러 개 선택 (Enter로 붙여넣기)
    }

    private void OnDeactivated(object? sender, EventArgs e) { CancelSingle(); Close(); }   // 딴 데 클릭하면 닫힘
}
