using System.Diagnostics;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Snatch;

public partial class App : Application
{
    private WinForms.NotifyIcon? _tray;
    private DownloadWatcher? _watcher;
    private ClipboardWatcher? _clip;
    private QueueStrip? _queueStrip;                      // mini-chip stack by the cursor
    private readonly ClipHistory _history = new();        // persistent clipboard history (500)
    private HistoryWindow? _historyWin;
    private IntPtr _prevFocus;                            // app to paste back into
    private readonly List<ClipContent> _queue = new();   // clipboard history, newest at index 0
    private const int MaxQueue = 5;
    private Settings _settings = new();
    private SettingsWindow? _settingsWin;
    private Hotkeys? _hotkeys;
    private Window? _hkHost;
    private bool _capturing;
    private static System.Threading.Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // single instance
        _mutex = new System.Threading.Mutex(true, "Snatch_SingleInstance", out bool fresh);
        if (!fresh)
        {
            Shutdown();
            return;
        }

        SetupCrashLog();
        _settings = Settings.Load();
        _history.Load();
        ToastManager.Position = _settings.ToastPosition;

        _watcher = new DownloadWatcher();
        _watcher.FileReady += OnFileReady;
        _watcher.Start(_settings.ResolvedDownloads);

        SetupHotkeys();
        SetupClipboard();
        BuildTray();
    }

    private void SetupCrashLog()
    {
        DispatcherUnhandledException += (_, e) => { LogEx("UI", e.Exception); e.Handled = true; };   // 트레이앱 생존 우선
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogEx("Domain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { LogEx("Task", e.Exception); e.SetObserved(); };
    }

    private static void LogEx(string src, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snatch");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {src}: {ex}\n\n");
        }
        catch { }
    }

    private void SetupClipboard()
    {
        if (_hotkeys == null) return; // shares the hidden host window's handle
        _clip = new ClipboardWatcher(_hotkeys.Handle);
        _clip.Changed += OnClipboardChanged;
        // Ctrl+V·K 감지는 QueueStrip 자체 타이머에서 처리 (GetAsyncKeyState)
    }

    private void EditLast()
    {
        var path = _lastShotPath;
        if (path == null || !System.IO.File.Exists(path)) return;
        _queueStrip?.CloseStrip(); _queueStrip = null;   // 편집기 열면 칩 치움
        try { var w = new AnnotationWindow(path, _settings); w.Show(); w.Activate(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private readonly HashSet<string> _ytDone = new();   // 세션 내 이미 받은 URL
    private int _ytActive;   // 진행 중 다운로드 수

    private void RestoreTrayText() { if (_ytActive <= 0) UpdateTrayCount(); }

    private void OnClipboardChanged(ClipContent content) => Dispatcher.Invoke(() =>
    {
        EnqueueClip(content);
        if (_settings.YoutubeDownload && content.Text is { } t && YoutubeDownloader.IsSupportedUrl(t))
            StartYoutubeDownload(t.Trim());
    });

    private void StartYoutubeDownload(string url)
    {
        if (!_ytDone.Add(url)) return;   // 같은 링크 재복사 시 재다운로드 안 함
        var targetDir = _settings.ResolvedYoutubeDir;
        // 조각/병합 파일이 다운로드 폴더 감시에 걸리지 않게 임시 폴더에서 작업
        var tempDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snatch", "yt-tmp", Guid.NewGuid().ToString("N"));

        _tray?.ShowBalloonTip(3000, "영상 다운로드 시작 (H264)", url, WinForms.ToolTipIcon.Info);
        _ytActive++;
        YoutubeDownloader.Download(url, tempDir,
            tempPath => Dispatcher.Invoke(() => { _ytActive--; FinishYoutube(tempPath, targetDir, tempDir); RestoreTrayText(); }),
            msg => Dispatcher.Invoke(() =>
            {
                _ytActive--;
                _ytDone.Remove(url);   // 실패는 재시도 허용
                TryDeleteDir(tempDir);
                RestoreTrayText();
                _tray?.ShowBalloonTip(4000, "영상 다운로드 실패", msg, WinForms.ToolTipIcon.Error);
            }),
            pct => Dispatcher.Invoke(() => { if (_tray != null && _ytActive > 0) _tray.Text = $"Snatch · ⬇ {pct}%{(_ytActive > 1 ? $" ×{_ytActive}" : "")}"; }));
    }

    private void FinishYoutube(string tempPath, string targetDir, string tempDir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(targetDir);
            var stamp = DateTime.Now.ToString("yyMMdd-HHmmss");
            var dest = UniquePath(System.IO.Path.Combine(targetDir, $"{stamp}_{System.IO.Path.GetFileName(tempPath)}"));
            _watcher?.Ignore(dest);            // 이 파일은 내가 토스트 띄우니 감시기는 무시
            System.IO.File.Move(tempPath, dest);
            ShowToast(dest, capture: false);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { TryDeleteDir(tempDir); }
    }

    private static string UniquePath(string dest)
    {
        if (!System.IO.File.Exists(dest)) return dest;
        var dir = System.IO.Path.GetDirectoryName(dest)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(dest);
        var ext = System.IO.Path.GetExtension(dest);
        for (int i = 1; ; i++)
        {
            var c = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
            if (!System.IO.File.Exists(c)) return c;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
    }

    private void EnqueueClip(ClipContent c)
    {
        _queue.Insert(0, c);
        while (_queue.Count > MaxQueue) _queue.RemoveAt(_queue.Count - 1);
        _history.Add(c);
        SyncQueueUi();
    }

    private void UpdateTrayCount()
    {
        if (_tray != null) _tray.Text = _queue.Count > 0 ? $"Snatch · 큐 {_queue.Count}/{MaxQueue}" : "Snatch";
    }

    /// <summary>Update tray tooltip + the persistent cursor chip stack to match the queue.</summary>
    private void SyncQueueUi()
    {
        UpdateTrayCount();
        try
        {
            if ((!_settings.ShowChips && !_settings.ShowPreview) || _queue.Count == 0)
            {
                _queueStrip?.CloseStrip();
                _queueStrip = null;
                return;
            }
            if (_queueStrip == null)
            {
                var strip = new QueueStrip();
                strip.Closed += (_, _) => { if (ReferenceEquals(_queueStrip, strip)) _queueStrip = null; };
                _queueStrip = strip;
                strip.ShowChips = _settings.ShowChips;
                strip.ShowPreview = _settings.ShowPreview;
                strip.NoTimeout = _settings.KeepUntilPaste;
                strip.OnEdit = () => Dispatcher.Invoke(EditLast);
                strip.SetItems(_queue.ToList());
                strip.Show();
            }
            else
            {
                _queueStrip.ShowChips = _settings.ShowChips;
                _queueStrip.ShowPreview = _settings.ShowPreview;
                _queueStrip.NoTimeout = _settings.KeepUntilPaste;
                _queueStrip.OnEdit = () => Dispatcher.Invoke(EditLast);
                _queueStrip.SetItems(_queue.ToList());
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private string _regionHk = "(없음)";
    private string _fullHk = "(없음)";
    private string _histHk = "(없음)";
    private string _plainHk = "(없음)";
    private string _ocrHk = "(없음)";

    private void SetupHotkeys()
    {
        // hidden host window just to own the global hotkeys
        _hkHost = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        _hotkeys = new Hotkeys(_hkHost);

        // (vk, ctrl, shift, alt, label) — first that registers (1409=already taken) wins
        var regionCands = new (uint vk, bool c, bool s, bool a, string label)[]
        {
            (0x34, true, true, false, "Ctrl+Shift+4"),
            (0x34, true, false, true, "Ctrl+Alt+4"),
            (0x35, true, true, false, "Ctrl+Shift+5"),
            (0x53, true, true, true,  "Ctrl+Shift+Alt+S"),
            (0x2C, false, true, false, "Shift+PrtSc"),
        };
        var fullCands = new (uint vk, bool c, bool s, bool a, string label)[]
        {
            (0x33, true, true, false, "Ctrl+Shift+3"),
            (0x33, true, false, true, "Ctrl+Alt+3"),
            (0x36, true, true, false, "Ctrl+Shift+6"),
            (0x46, true, true, true,  "Ctrl+Shift+Alt+F"),
            (0x2C, false, false, true, "Alt+PrtSc"),
        };

        _regionHk = TryRegister(regionCands, CaptureRegion) ?? "(없음)";
        var histCands = new (uint vk, bool c, bool s, bool a, string label)[]
        {
            (0x48, true, false, true, "Ctrl+Alt+H"),
            (0x48, true, true, false, "Ctrl+Shift+H"),
            (0x43, true, false, true, "Ctrl+Alt+C"),
        };

        var plainCands = new (uint vk, bool c, bool s, bool a, string label)[]
        {
            (0x56, true, false, true, "Ctrl+Alt+V"),
            (0x56, true, true, true,  "Ctrl+Shift+Alt+V"),
        };
        var ocrCands = new (uint vk, bool c, bool s, bool a, string label)[]
        {
            (0x54, true, false, true, "Ctrl+Alt+T"),
            (0x54, true, true, false, "Ctrl+Shift+T"),
        };

        _fullHk = TryRegister(fullCands, CaptureFull) ?? "(없음)";
        _histHk = TryRegister(histCands, ToggleHistory) ?? "(없음)";
        _plainHk = TryRegister(plainCands, PlainPaste) ?? "(없음)";
        _ocrHk = TryRegister(ocrCands, OcrLast) ?? "(없음)";
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snatch");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "hotkeys.log"), $"region={_regionHk}\nfull={_fullHk}\nhistory={_histHk}\nplainpaste={_plainHk}\nocr={_ocrHk}\n");
        }
        catch { }
    }

    private string? TryRegister((uint vk, bool c, bool s, bool a, string label)[] cands, Action onPress)
    {
        foreach (var k in cands)
            if (_hotkeys!.Register(k.vk, k.c, k.s, k.a, onPress))
                return k.label;
        return null;
    }

    private void OnFileReady(string path) => Dispatcher.Invoke(() => ShowToast(path, capture: false));

    private void ShowToast(string path, bool capture)
    {
        try
        {
            if (capture) Sound.PlayCapture(_settings); else Sound.Play(_settings);
            new ToastWindow(path, _settings).Show();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void CaptureRegion()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            // 1) 전체 가상 데스크탑(모든 모니터)을 즉시 얼린다 → 모니터 경계 넘어 선택 가능
            var vs = WinForms.SystemInformation.VirtualScreen; // 물리 px
            var windows = Win32.VisibleTopWindows();  // Space 창모드용 (얼리기 전 z순서)
            var frozen = Capture.GrabRegion(vs.X, vs.Y, vs.Width, vs.Height);
            int ox = vs.X, oy = vs.Y;
            try
            {
                // 2) 정지 이미지를 배경으로 오버레이 띄우고 영역 선택 (포커스 뺏겨도 무관)
                var overlay = new CaptureOverlay();  // SetTargetMonitor 안 함 → 전체 가상화면 덮음
                overlay.SetFrozen(Capture.ToBitmapSource(frozen));
                overlay.SetWindows(windows);
                var ok = overlay.ShowDialog();
                if (ok == true && overlay.PickedColor is { } hex)
                {
                    PickColor(hex);
                }
                else if (ok == true && overlay.Result is { } r && r.Width > 2 && r.Height > 2)
                {
                    // 3) 얼린 비트맵에서 잘라 저장 (라이브 화면 재캡처 아님)
                    var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
                    var path = Capture.SaveCrop(frozen, ox, oy, r.X, r.Y, r.Width, r.Height, _settings, stamp);
                    SaveAndNotify(path);
                }
            }
            finally { frozen.Dispose(); }
        }
        finally { _capturing = false; }
    }

    private void PickColor(string hex)
    {
        try
        {
            _clip?.Suppress();                 // our own write — preview shown explicitly below
            Clipboard.SetText(hex);
        }
        catch { /* clipboard may be locked */ }
        Sound.PlayCapture(_settings);
        EnqueueClip(new ClipContent { ColorHex = hex });
    }

    // ---- clipboard queue: panel + sequential paste ----
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private const byte VK_CONTROL = 0x11, VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    private void ToggleHistory()
    {
        if (_historyWin != null) { try { _historyWin.Close(); } catch { } _historyWin = null; return; }
        _prevFocus = GetForegroundWindow();   // 붙여넣을 대상 앱 기억
        var win = new HistoryWindow(_history, PasteHistory);
        win.Closed += (_, _) => { if (ReferenceEquals(_historyWin, win)) _historyWin = null; };
        _historyWin = win;
        win.Show();
        win.Activate();
    }

    private void PasteHistory(HistoryItem item)
    {
        var clip = ClipHistory.ToClip(item);
        try { _historyWin?.Close(); } catch { }
        _historyWin = null;

        try { _clip?.Suppress(); SetClipboard(clip); }
        catch { /* clipboard may be locked */ }

        if (_prevFocus != IntPtr.Zero) SetForegroundWindow(_prevFocus);

        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        t.Tick += (_, _) => { t.Stop(); SendCtrlV(); };
        t.Start();
    }

    private void ClearQueue()
    {
        _queue.Clear();
        SyncQueueUi();
    }

    // 마지막 캡처에서 텍스트 추출 (OCR, Ctrl+Alt+T)
    private async void OcrLast()
    {
        var path = _lastShotPath;
        if (path == null || !System.IO.File.Exists(path)) return;
        _tray?.ShowBalloonTip(2000, "OCR", "텍스트 추출 중…", WinForms.ToolTipIcon.Info);
        try
        {
            var text = await OcrService.RecognizeAsync(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                _tray?.ShowBalloonTip(3000, "OCR", "인식된 텍스트 없음", WinForms.ToolTipIcon.Warning);
                return;
            }
            _clip?.Suppress();
            Clipboard.SetText(text);
            EnqueueClip(new ClipContent { Text = text });
            _tray?.ShowBalloonTip(3000, "OCR — 클립보드 복사됨", text.Length > 80 ? text[..80] + "…" : text, WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex) { LogEx("OCR", ex); _tray?.ShowBalloonTip(3000, "OCR 실패", ex.Message, WinForms.ToolTipIcon.Error); }
    }

    // 서식 없는 텍스트로 붙여넣기 (Ctrl+Alt+V)
    private void PlainPaste()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var t = Clipboard.GetText();
            if (string.IsNullOrEmpty(t)) return;
            _clip?.Suppress();
            Clipboard.SetText(t);   // 유니코드 텍스트만 → RTF/HTML 서식 제거
        }
        catch { return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        timer.Tick += (_, _) => { timer.Stop(); SendCtrlV(); };
        timer.Start();
    }

    /// <summary>Chip clicked — put it on the clipboard and paste into the focused app.</summary>
    private void PasteFromQueue(ClipContent item)
    {
        try { _clip?.Suppress(); SetClipboard(item); }
        catch { /* clipboard may be locked */ }

        _queue.Remove(item);
        SyncQueueUi();

        // 클립보드 정착 후 대상 앱에 Ctrl+V 전송 (스트립은 no-activate라 포커스 유지)
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        t.Tick += (_, _) => { t.Stop(); SendCtrlV(); };
        t.Start();
    }

    private static void SetClipboard(ClipContent item)
    {
        if (item.IsColor && item.ColorHex is { } hex) { Clipboard.SetText(hex); return; }
        if (item.IsImage)
        {
            if (item.ImagePath is { } p && System.IO.File.Exists(p))
            {
                var full = new System.Windows.Media.Imaging.BitmapImage();
                full.BeginInit();
                full.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                full.UriSource = new Uri(p);
                full.EndInit();
                Clipboard.SetImage(full);
            }
            else if (item.Image is { } im) Clipboard.SetImage(im);
            return;
        }
        if (item.Text is { } tx) Clipboard.SetText(tx);
    }

    private static void SendCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void CaptureFull()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            var b = WinForms.Screen.FromPoint(WinForms.Cursor.Position).Bounds; // 마우스 있는 모니터(물리 px)
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
            SaveAndNotify(Capture.SaveRegion(b.X, b.Y, b.Width, b.Height, _settings, stamp));
        }
        finally { _capturing = false; }
    }

    private void CaptureWindow()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            // 핫키 누른 순간의 활성 창 영역(물리 px) — 오버레이 없이 바로 잡음(포커스 안 뺏김)
            var b = Win32.ActiveWindowBounds();
            if (b is not { } r) return;
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
            SaveAndNotify(Capture.SaveRegion(r.x, r.y, r.w, r.h, _settings, stamp));
        }
        finally { _capturing = false; }
    }

    private string? _lastShotPath;   // 마지막 캡처 스크린샷 (편집용)

    private void SaveAndNotify(string? path)
    {
        if (path == null) return;
        _lastShotPath = path;

        if (_settings.CopyToClipboard)
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                _clip?.Suppress();   // our clipboard write; we show the preview explicitly below
                Clipboard.SetImage(bi);
            }
            catch { /* clipboard may be locked */ }
        }

        // 캡처는 하단 토스트 대신 커서 추적 프리뷰만 (소리는 재생)
        Sound.PlayCapture(_settings);

        // 캡처 결과가 커서를 따라다니게 (클립보드 복사 여부와 무관)
        try
        {
            var thumb = new System.Windows.Media.Imaging.BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            thumb.DecodePixelWidth = 300;
            thumb.UriSource = new Uri(path);
            thumb.EndInit();
            thumb.Freeze();
            EnqueueClip(new ClipContent { Image = thumb, ImagePath = path });
        }
        catch { }
    }

    private void BuildTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Snatch",
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"영역 캡처  ({_regionHk})", null, (_, _) => Dispatcher.Invoke(CaptureRegion));
        menu.Items.Add($"전체 캡처  ({_fullHk})", null, (_, _) => Dispatcher.Invoke(CaptureFull));
        menu.Items.Add($"클립보드 히스토리  ({_histHk})", null, (_, _) => Dispatcher.Invoke(ToggleHistory));
        menu.Items.Add("마지막 캡처 편집", null, (_, _) => Dispatcher.Invoke(EditLast));
        menu.Items.Add($"마지막 캡처 OCR  ({_ocrHk})", null, (_, _) => Dispatcher.Invoke(OcrLast));
        menu.Items.Add($"서식없이 붙여넣기  ({_plainHk})", null, (_, _) => Dispatcher.Invoke(PlainPaste));
        menu.Items.Add("큐 비우기", null, (_, _) => Dispatcher.Invoke(ClearQueue));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        var chipItem = new WinForms.ToolStripMenuItem("미니 썸네일") { Checked = _settings.ShowChips, CheckOnClick = true };
        chipItem.CheckedChanged += (_, _) =>
        {
            _settings.ShowChips = chipItem.Checked;
            _settings.Save();
            Dispatcher.Invoke(SyncQueueUi);
        };
        menu.Items.Add(chipItem);

        var prevItem = new WinForms.ToolStripMenuItem("복사 미리보기") { Checked = _settings.ShowPreview, CheckOnClick = true };
        prevItem.CheckedChanged += (_, _) =>
        {
            _settings.ShowPreview = prevItem.Checked;
            _settings.Save();
            Dispatcher.Invoke(SyncQueueUi);
        };
        menu.Items.Add(prevItem);

        var keepItem = new WinForms.ToolStripMenuItem("붙여넣을 때까지 유지") { Checked = _settings.KeepUntilPaste, CheckOnClick = true };
        keepItem.CheckedChanged += (_, _) =>
        {
            _settings.KeepUntilPaste = keepItem.Checked;
            _settings.Save();
            Dispatcher.Invoke(SyncQueueUi);
        };
        menu.Items.Add(keepItem);

        var autoItem = new WinForms.ToolStripMenuItem("Windows 시작 시 자동실행") { Checked = AutoStart.IsEnabled(), CheckOnClick = true };
        autoItem.CheckedChanged += (_, _) => AutoStart.Set(autoItem.Checked);
        menu.Items.Add(autoItem);

        var ytItem = new WinForms.ToolStripMenuItem("유튜브 H264 다운로드") { Checked = _settings.YoutubeDownload, CheckOnClick = true };
        ytItem.CheckedChanged += (_, _) => { _settings.YoutubeDownload = ytItem.Checked; _settings.Save(); };
        menu.Items.Add(ytItem);
        menu.Items.Add("설정", null, (_, _) => OpenSettings());
        menu.Items.Add("다운로드 폴더 열기", null, (_, _) =>
        {
            try { Process.Start("explorer.exe", _settings.ResolvedDownloads); } catch { }
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenSettings();

        _tray.ShowBalloonTip(4000, "Snatch + 캡처",
            $"영역 {_regionHk} · 전체 {_fullHk} · 히스토리 {_histHk}", WinForms.ToolTipIcon.Info);
    }

    private static Icon LoadTrayIcon()
    {
        // Snatch 전용 아이콘: 둥근 파란 배경 + 흰 'S' (집게로 낚아챔 느낌)
        try
        {
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                var rect = new Rectangle(1, 1, 30, 30);
                int d = 12;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(rect,
                    Color.FromArgb(0x3D, 0xA9, 0xFC), Color.FromArgb(0x1E, 0x6F, 0xE0), 90f);
                g.FillPath(bg, path);
                using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("S", font, Brushes.White, new RectangleF(0, -1, 32, 32), sf);
            }
            IntPtr h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }
        catch { return SystemIcons.Application; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    private void OpenSettings()
    {
        if (_settingsWin != null) { _settingsWin.Activate(); return; }
        _settingsWin = new SettingsWindow(_settings);
        _settingsWin.Saved += () =>
        {
            // restart watcher in case downloads folder changed
            _watcher?.Start(_settings.ResolvedDownloads);
            ToastManager.Position = _settings.ToastPosition;
        };
        _settingsWin.Closed += (_, _) => _settingsWin = null;
        _settingsWin.Show();
        _settingsWin.Activate();
    }

    private void ExitApp()
    {
        _queueStrip?.CloseStrip();
        _clip?.Dispose();
        _hotkeys?.Dispose();
        _watcher?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }
}
