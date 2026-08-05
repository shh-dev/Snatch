using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Snatch;

/// <summary>Downloads a YouTube URL as an H.264 (avc1) MP4 via yt-dlp + ffmpeg.</summary>
public static class YoutubeDownloader
{
    private static readonly Regex YtRe = new(
        @"^https?://(www\.|m\.|music\.)?(youtube\.com/(watch\?v=|shorts/|live/|embed/)|youtu\.be/)[\w\-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsYouTubeUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Contains('\n') || t.Contains(' ')) return false;   // 링크 하나만
        return YtRe.IsMatch(t);
    }

    /// <summary>Runs yt-dlp async into <paramref name="dir"/> (use a temp/scratch dir — yt-dlp writes fragment
    /// files there too). onDone(finalFilePath) on success. Callbacks fire on a worker thread.</summary>
    public static void Download(string url, string dir, Action<string> onDone, Action<string> onFail)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo
            {
                FileName = ResolveYtDlp(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // H264(avc1) 영상 + m4a 오디오 우선, 안되면 mp4, 최후엔 최고화질
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("bv*[vcodec^=avc1]+ba[acodec^=mp4a]/b[ext=mp4][vcodec^=avc1]/bv*+ba/b");
            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add("mp4");
            psi.ArgumentList.Add("-P");
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("%(title)s [%(id)s].%(ext)s");
            psi.ArgumentList.Add("--no-simulate");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("after_move:filepath");
            var ff = ResolveFfmpegDir();
            if (ff != null) { psi.ArgumentList.Add("--ffmpeg-location"); psi.ArgumentList.Add(ff); }
            psi.ArgumentList.Add(url);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var outLines = new List<string>();
            var err = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) outLines.Add(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
            proc.Exited += (_, _) =>
            {
                try
                {
                    if (proc.ExitCode == 0)
                    {
                        var path = outLines.LastOrDefault(l => l.Contains(Path.DirectorySeparatorChar) && File.Exists(l.Trim()));
                        if (path != null) onDone(path.Trim());
                        else onFail("파일 경로 확인 실패");
                    }
                    else
                    {
                        var msg = err.ToString().Trim();
                        onFail(msg.Length > 0 ? msg[^Math.Min(msg.Length, 200)..] : $"yt-dlp 종료 코드 {proc.ExitCode}");
                    }
                }
                catch (Exception ex) { onFail(ex.Message); }
                finally { proc.Dispose(); }
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex) { onFail(ex.Message); }
    }

    private static string ResolveYtDlp()
    {
        var cands = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python312\Scripts\yt-dlp.exe"),
        };
        foreach (var c in cands) if (File.Exists(c)) return c;
        return "yt-dlp.exe";   // PATH fallback
    }

    private static string? ResolveFfmpegDir()
    {
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
            if (Directory.Exists(packages))
            {
                foreach (var d in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*"))
                {
                    var bin = Directory.EnumerateDirectories(d, "bin", SearchOption.AllDirectories).FirstOrDefault();
                    if (bin != null && File.Exists(Path.Combine(bin, "ffmpeg.exe"))) return bin;
                }
            }
        }
        catch { }
        return null;   // rely on PATH
    }
}
