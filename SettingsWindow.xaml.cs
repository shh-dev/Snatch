using System.IO;
using System.Windows;

namespace Snatch;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    public event Action? Saved;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        RefreshList();
        HideBox.Text = _settings.AutoHideSeconds.ToString();
        DlText.Text = _settings.ResolvedDownloads;
        SoundChk.IsChecked = _settings.PlaySound;
        ClipChk.IsChecked = _settings.CopyToClipboard;
        PosBox.SelectedValue = _settings.ToastPosition;
        if (PosBox.SelectedIndex < 0) PosBox.SelectedValue = "Right";
        YtChk.IsChecked = _settings.YoutubeDownload;
        UpdateYtDirText();
        UpdateSoundText();
        UpdateCapSoundText();
        UpdateShotDirText();
        ExpandChk.IsChecked = _settings.TextExpand;
        RefreshSnips();
    }

    private void RefreshSnips()
    {
        SnipList.Items.Clear();
        foreach (var s in _settings.Snippets)
            SnipList.Items.Add($"{s.Trigger}  →  {s.Text.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ")}");
    }

    private void OnSnipSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int i = SnipList.SelectedIndex;
        if (i < 0 || i >= _settings.Snippets.Count) return;
        SnipTrigger.Text = _settings.Snippets[i].Trigger;
        SnipText.Text = _settings.Snippets[i].Text;
    }

    private void OnSnipSave(object sender, RoutedEventArgs e)
    {
        var t = SnipTrigger.Text.Trim().ToLowerInvariant();
        if (t.Length == 0 || !t.All(char.IsAsciiLetterOrDigit))
        {
            MessageBox.Show(this, "단축어는 영문·숫자만 쓸 수 있어요.", "단축어", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SnipText.Text.Length == 0) return;
        var hit = _settings.Snippets.FirstOrDefault(s => s.Trigger == t);
        if (hit != null) hit.Text = SnipText.Text;
        else _settings.Snippets.Add(new Snippet { Trigger = t, Text = SnipText.Text });
        _settings.Save();
        RefreshSnips();
        SnipTrigger.Clear(); SnipText.Clear();
    }

    private void OnSnipRemove(object sender, RoutedEventArgs e)
    {
        int i = SnipList.SelectedIndex;
        if (i < 0 || i >= _settings.Snippets.Count) return;
        _settings.Snippets.RemoveAt(i);
        _settings.Save();
        RefreshSnips();
    }

    private void UpdateShotDirText() => ShotDirText.Text = Capture.ScreenshotFolder(_settings);

    private void UpdateYtDirText() => YtDirText.Text = _settings.ResolvedYoutubeDir;

    private void OnPickYtDir(object sender, RoutedEventArgs e)
    {
        var picked = InputBox.AskFolder(this, "유튜브 다운로드 폴더", _settings.ResolvedYoutubeDir);
        if (picked != null) { _settings.YoutubeDir = picked; UpdateYtDirText(); }
    }

    private void OnOpenYtDir(object sender, RoutedEventArgs e)
    {
        var dir = _settings.ResolvedYoutubeDir;
        try { System.IO.Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); } catch { }
    }

    private void UpdateCapSoundText() =>
        CapSoundText.Text = string.IsNullOrWhiteSpace(_settings.CaptureSoundFile)
            ? "기본 셔터음" : System.IO.Path.GetFileName(_settings.CaptureSoundFile);

    private void OnPickCapSound(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "캡처 음원 선택 (.wav)", Filter = "WAV 오디오 (*.wav)|*.wav" };
        if (dlg.ShowDialog(this) == true) { _settings.CaptureSoundFile = dlg.FileName; UpdateCapSoundText(); }
    }

    private void OnDefaultCapSound(object sender, RoutedEventArgs e)
    {
        _settings.CaptureSoundFile = null;
        UpdateCapSoundText();
    }

    private void OnTestCapSound(object sender, RoutedEventArgs e)
        => Sound.PlayCapture(new Settings { PlaySound = true, CaptureSoundFile = _settings.CaptureSoundFile });

    private void OnPickShotDir(object sender, RoutedEventArgs e)
    {
        var picked = InputBox.AskFolder(this, "캡처 저장 폴더", Capture.ScreenshotFolder(_settings));
        if (picked != null)
        {
            _settings.ScreenshotFolder = picked;
            UpdateShotDirText();
        }
    }

    private void UpdateSoundText() =>
        SoundText.Text = string.IsNullOrWhiteSpace(_settings.SoundFile)
            ? "기본 알림음" : System.IO.Path.GetFileName(_settings.SoundFile);

    private void OnPickSound(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "알림음 선택 (.wav)",
            Filter = "WAV 오디오 (*.wav)|*.wav",
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _settings.SoundFile = dlg.FileName;
            UpdateSoundText();
        }
    }

    private void OnDefaultSound(object sender, RoutedEventArgs e)
    {
        _settings.SoundFile = null;
        UpdateSoundText();
    }

    private void OnTestSound(object sender, RoutedEventArgs e)
    {
        var probe = new Settings { PlaySound = true, SoundFile = _settings.SoundFile };
        Sound.Play(probe);
    }

    private void RefreshList()
    {
        FavList.Items.Clear();
        foreach (var f in _settings.Favorites)
            FavList.Items.Add($"{f.Name}  —  {f.Path}");
    }

    private void OnAddFav(object sender, RoutedEventArgs e)
    {
        var path = InputBox.AskFolder(this, "즐겨찾기 폴더 추가", _settings.ResolvedDownloads);
        if (path != null)
        {
            var name = new DirectoryInfo(path).Name;
            _settings.Favorites.Add(new Favorite { Name = name, Path = path });
            RefreshList();
        }
    }

    private void OnRemoveFav(object sender, RoutedEventArgs e)
    {
        int i = FavList.SelectedIndex;
        if (i >= 0 && i < _settings.Favorites.Count)
        {
            _settings.Favorites.RemoveAt(i);
            RefreshList();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(HideBox.Text, out var s)) _settings.AutoHideSeconds = Math.Clamp(s, 4, 120);
        _settings.PlaySound = SoundChk.IsChecked == true;
        _settings.CopyToClipboard = ClipChk.IsChecked == true;
        _settings.ToastPosition = PosBox.SelectedValue as string ?? "Right";
        _settings.YoutubeDownload = YtChk.IsChecked == true;
        _settings.TextExpand = ExpandChk.IsChecked == true;
        _settings.Save();
        Saved?.Invoke();
        MetaFlash();
    }

    private void MetaFlash()
    {
        SavedText.Text = "저장됨 ✓";
    }

    private void OnCloseBtn(object sender, RoutedEventArgs e) => Close();
}
