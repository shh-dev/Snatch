using Microsoft.Win32;

namespace Snatch;

/// <summary>Windows 로그인 시 자동 실행 (HKCU Run 레지스트리).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Snatch";

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                var exe = ExePath;
                if (!string.IsNullOrWhiteSpace(exe)) k.SetValue(Name, $"\"{exe}\"");
            }
            else k.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { /* ignore */ }
    }
}
