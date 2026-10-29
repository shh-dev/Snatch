using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace Snatch;

public partial class ToastWindow : Window
{
    private string _path;
    private readonly Settings _settings;
    private readonly DispatcherTimer _hideTimer;
    private Point _dragOrigin;
    private bool _dragArmed;
    private bool _closed;

    public ToastWindow(string path, Settings settings)
    {
        InitializeComponent();  Win32.ExcludeFromCapture(this);   // 스크린샷에 안 찍히게
        _path = path;
        _settings = settings;

        var name = Path.GetFileName(path);
        NameText.Text = name;
        try
        {
            var len = new FileInfo(path).Length;
            MetaText.Text = Win32.HumanSize(len);
        }
        catch { MetaText.Text = ""; }

        if (IsImage(path) && TryLoadThumb(path) is { } thumb)
        {
            IconHost.Width = 104; IconHost.Height = 72;
            FileIcon.Width = 104; FileIcon.Height = 72;
            FileIcon.Stretch = Stretch.UniformToFill;
            FileIcon.Source = thumb;
        }
        else
        {
            var ico = Win32.GetFileIcon(path, large: true);
            if (ico != null) FileIcon.Source = ico;
        }

        BuildFavButtons();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(4, _settings.AutoHideSeconds)) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); BeginClose(); };

        Loaded += (_, _) =>
        {
            ToastManager.Register(this);
            FadeTo(1, 180);
            _hideTimer.Start();
        };
    }

    private void BuildFavButtons()
    {
        FavPanel.Children.Clear();

        // open folder
        FavPanel.Children.Add(MakeChip("📂 폴더", "다운로드 폴더 열기", (_, _) =>
        {
            try { Process.Start("explorer.exe", $"/select,\"{_path}\""); } catch { }
        }));

        foreach (var fav in _settings.Favorites)
        {
            var f = fav;
            FavPanel.Children.Add(MakeChip("→ " + f.Name, $"{f.Path} 로 이동", (_, _) => MoveTo(f.Path)));
        }
    }

    private Button MakeChip(string text, string tip, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            ToolTip = tip,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 6),
            FontSize = 11,
            Cursor = Cursors.Hand,
        };
        // rounded
        b.Template = ChipTemplate();
        b.Click += onClick;
        return b;
    }

    private static ControlTemplate ChipTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 5, 10, 5));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        return t;
    }

    private void MoveTo(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            var dest = Path.Combine(folder, Path.GetFileName(_path));
            dest = Unique(dest);
            File.Move(_path, dest);
            _path = dest;
            MetaText.Text = "이동됨 → " + folder;
            _hideTimer.Stop();
            BeginClose(delayMs: 800);
        }
        catch (Exception ex)
        {
            MetaText.Text = "이동 실패: " + ex.Message;
        }
    }

    private static string Unique(string dest)
    {
        if (!File.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest)!;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 1; ; i++)
        {
            var cand = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(cand)) return cand;
        }
    }

    private static readonly string[] ImgExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };
    private static bool IsImage(string p) => ImgExts.Contains(Path.GetExtension(p).ToLowerInvariant());

    private static System.Windows.Media.Imaging.BitmapImage? TryLoadThumb(string path)
    {
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = 144; // thumbnail only
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // ---- drag-out ----
    private void OnDragStartDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(this);
        _dragArmed = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        if (!File.Exists(_path))
        {
            // clicked but file gone (moved) → if name click, treat as open
            return;
        }

        var data = new DataObject();
        var coll = new System.Collections.Specialized.StringCollection { _path };
        data.SetFileDropList(coll);
        _hideTimer.Stop();
        var result = DragDropEffects.None;
        try { result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch { }

        // 드롭 성공(어딘가에 놓음) → 카드 즉시 닫기. 취소/허공이면 유지
        if (result != DragDropEffects.None) { BeginClose(); return; }
        if (!_closed) _hideTimer.Start();
    }

    // ---- hover keep-alive ----
    private void OnMouseEnterCard(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        FadeTo(1, 100);
    }

    private void OnMouseLeaveCard(object sender, MouseEventArgs e)
    {
        if (_closed) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnClose(object sender, RoutedEventArgs e) => BeginClose();

    public void BeginClose(int delayMs = 0)
    {
        if (_closed) return;
        _closed = true;
        _hideTimer.Stop();
        if (delayMs > 0)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            t.Tick += (_, _) => { t.Stop(); FadeOutAndClose(); };
            t.Start();
        }
        else FadeOutAndClose();
    }

    private void FadeOutAndClose()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        a.Completed += (_, _) => { ToastManager.Unregister(this); Close(); };
        BeginAnimation(OpacityProperty, a);
    }

    private void FadeTo(double to, int ms)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }
}
