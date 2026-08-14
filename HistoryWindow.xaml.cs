using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Snatch;

/// <summary>Searchable clipboard history list. Click a row to paste it, ✕ to delete it.</summary>
public partial class HistoryWindow : Window
{
    private readonly ClipHistory _history;
    private readonly Action<HistoryItem> _onPick;

    public HistoryWindow(ClipHistory history, Action<HistoryItem> onPick)
    {
        InitializeComponent();
        _history = history;
        _onPick = onPick;
        Refresh();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private string _filter = "all";

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is string t) { _filter = t; Refresh(); }
    }

    private void Refresh()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var list = _history.Items
            .Where(i => _filter == "all" || i.Kind == _filter)
            .Where(i => q.Length == 0 ||
                        (i.Text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (i.ColorHex ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (i.Kind == "image" && "이미지".Contains(q)))
            .OrderByDescending(i => i.Pinned)   // 고정 먼저 (그 안에서 최신순 유지)
            .ToList();

        Rows.Children.Clear();
        for (int i = 0; i < list.Count; i++)
            Rows.Children.Add(MakeRow(list[i], i + 1));

        // 필터 버튼 하이라이트
        var act = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));
        foreach (var (btn, tag) in new[] { (FAll, "all"), (FImg, "image"), (FText, "text"), (FColor, "color") })
            btn.Background = tag == _filter ? act : SystemColors.ControlBrush;

        CountText.Text = $"{list.Count} / {_history.Items.Count}";
    }

    private Border MakeRow(HistoryItem item, int no)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // no
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // thumb
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // delete

        var noText = new TextBlock
        {
            Text = no.ToString("D2"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x7E, 0x88)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(noText, 0);
        grid.Children.Add(noText);

        var box = new Border
        {
            Width = 40, Height = 40,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 8, 0),
        };
        if (item.Kind == "color")
        {
            try { box.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.ColorHex!)); }
            catch { }
        }
        else if (ClipHistory.Thumb(item) is { } th)
        {
            box.Child = new Image { Source = th, Stretch = Stretch.UniformToFill };
        }
        else
        {
            box.Child = new TextBlock
            {
                Text = "T", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock
        {
            Text = item.Kind == "image" ? System.IO.Path.GetFileName(item.File ?? "") : (item.Text ?? "").Trim(),
            Foreground = Brushes.White,
            FontSize = 12,
            MaxHeight = 34,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock
        {
            Text = item.Time.ToString("MM-dd HH:mm"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x7E, 0x88)),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(body, 2);
        grid.Children.Add(body);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var pin = new Button
        {
            Content = item.Pinned ? "📌" : "📍",
            ToolTip = item.Pinned ? "고정 해제" : "고정",
            Width = 24, Height = 22, FontSize = 11,
            Foreground = new SolidColorBrush(item.Pinned ? Color.FromRgb(0x33, 0xFF, 0x33) : Color.FromRgb(0x77, 0x7E, 0x88)),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        pin.Click += (_, _) => { _history.TogglePin(item); Refresh(); };
        var del = new Button
        {
            Content = "✕", Width = 22, Height = 22, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xB0, 0xB8)),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        del.Click += (_, _) => { _history.Remove(item); Refresh(); };
        actions.Children.Add(pin); actions.Children.Add(del);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var row = new Border
        {
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(item.Pinned ? Color.FromArgb(0x22, 0x33, 0xFF, 0x33) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.Button) return; // delete button handles itself
            _onPick(item);
        };
        return row;
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        Refresh();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
