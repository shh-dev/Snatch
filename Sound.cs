using System.IO;
using System.Media;

namespace Snatch;

public static class Sound
{
    private static SoundPlayer? _dl;     // 다운로드 알림용
    private static string? _dlLoaded;
    private static SoundPlayer? _cap;    // 캡처용
    private static string? _capLoaded;

    /// <summary>다운로드 알림음: 커스텀 wav 또는 기본 SystemSounds.Asterisk.</summary>
    public static void Play(Settings s)
    {
        if (!s.PlaySound) return;
        try
        {
            if (Valid(s.SoundFile))
            {
                if (_dlLoaded != s.SoundFile) { _dl?.Dispose(); _dl = new SoundPlayer(s.SoundFile); _dl.LoadAsync(); _dlLoaded = s.SoundFile; }
                _dl?.Play();
            }
            else SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    /// <summary>캡처음: 커스텀 wav 또는 기본 셔터음(합성).</summary>
    public static void PlayCapture(Settings s)
    {
        if (!s.PlaySound) return;
        try
        {
            var file = Valid(s.CaptureSoundFile) ? s.CaptureSoundFile! : EnsureShutter();
            if (file == null) { SystemSounds.Hand.Play(); return; }
            if (_capLoaded != file) { _cap?.Dispose(); _cap = new SoundPlayer(file); _cap.LoadAsync(); _capLoaded = file; }
            _cap?.Play();
        }
        catch { try { SystemSounds.Hand.Play(); } catch { } }
    }

    private static bool Valid(string? p) => !string.IsNullOrWhiteSpace(p) && File.Exists(p);

    // ---- 셔터음 합성 (최초 1회 %APPDATA%\Snatch\shutter.wav 생성) ----
    private static string? EnsureShutter()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snatch");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "shutter.wav");
            if (!File.Exists(path)) WriteShutterWav(path);
            return path;
        }
        catch { return null; }
    }

    private static void WriteShutterWav(string path)
    {
        const int sr = 44100;
        // 카메라 셔터: 두 번의 짧은 클릭(여닫힘)
        var samples = new List<short>();
        AppendClick(samples, sr, 0.028, 0.9);   // 첫 클릭(열림)
        AppendSilence(samples, sr, 0.045);
        AppendClick(samples, sr, 0.050, 0.7);   // 두번째(닫힘) — 약간 길고 부드럽게

        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);
        int dataLen = samples.Count * 2;
        // WAV 헤더 (PCM 16bit mono)
        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + dataLen);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(dataLen);
        foreach (var s in samples) bw.Write(s);
    }

    private static readonly Random _rng = new();

    private static void AppendClick(List<short> buf, int sr, double sec, double amp)
    {
        int n = (int)(sr * sec);
        for (int i = 0; i < n; i++)
        {
            double env = Math.Exp(-12.0 * i / n);          // 빠른 감쇠
            double noise = _rng.NextDouble() * 2 - 1;       // 화이트노이즈
            double v = noise * env * amp * 0.8;
            buf.Add((short)(Math.Clamp(v, -1, 1) * short.MaxValue));
        }
    }

    private static void AppendSilence(List<short> buf, int sr, double sec)
    {
        int n = (int)(sr * sec);
        for (int i = 0; i < n; i++) buf.Add(0);
    }
}
