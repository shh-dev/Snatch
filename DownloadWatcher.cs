using System.IO;

namespace Snatch;

/// <summary>
/// Watches the downloads folder and raises <see cref="FileReady"/> once per
/// completed download. Browsers write a temp file (.crdownload/.part/...) and
/// rename it to the final name on completion; direct-created final files are
/// also handled, with de-dup so each file notifies only once.
/// </summary>
public class DownloadWatcher : IDisposable
{
    private static readonly string[] TempExts =
        { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

    private FileSystemWatcher? _fsw;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; private set; } = "";

    /// <summary>Raised (on a thread-pool thread) with the full path of a completed download.</summary>
    public event Action<string>? FileReady;

    public void Start(string folder)
    {
        Stop();
        if (!Directory.Exists(folder)) return;
        Folder = folder;
        _fsw = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _fsw.Renamed += OnRenamed;
        _fsw.Created += OnCreated;
    }

    /// <summary>Pre-register a path so the watcher won't raise FileReady for it (we notify ourselves).</summary>
    public void Ignore(string path)
    {
        lock (_lock) { _recent[path] = DateTime.UtcNow; }
    }

    public void Stop()
    {
        if (_fsw != null)
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Renamed -= OnRenamed;
            _fsw.Created -= OnCreated;
            _fsw.Dispose();
            _fsw = null;
        }
    }

    private static bool IsTemp(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return TempExts.Contains(ext);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // final-name file appearing via rename = download finished
        if (IsTemp(e.FullPath)) return;
        Consider(e.FullPath);
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        // ignore temp placeholders; a non-temp created file = direct/small download or copy
        if (IsTemp(e.FullPath)) return;
        Consider(e.FullPath);
    }

    private void Consider(string path)
    {
        lock (_lock)
        {
            // de-dup within 5s window
            var now = DateTime.UtcNow;
            foreach (var k in _recent.Where(kv => (now - kv.Value).TotalSeconds > 5).Select(kv => kv.Key).ToList())
                _recent.Remove(k);
            if (_recent.ContainsKey(path)) return;
            _recent[path] = now;
        }

        // wait until file exists and is no longer locked (write finished)
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 40; i++) // up to ~10s
                {
                    if (File.Exists(path) && !IsLocked(path)) { FileReady?.Invoke(path); return; }
                    await Task.Delay(250);
                }
            }
            catch { /* ignore */ }
        });
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public void Dispose() => Stop();
}
