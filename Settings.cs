using System.IO;
using System.Text.Json;

namespace Snatch;

public class Favorite
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public class Snippet
{
    public string Trigger { get; set; } = "";
    public string Text { get; set; } = "";
}

public class Settings
{
    public bool TextExpand { get; set; } = true;          // 단축어 자동 바꾸기
    public List<Snippet> Snippets { get; set; } = new();
    public string? DownloadsFolder { get; set; }
    public List<Favorite> Favorites { get; set; } = new();
    public int AutoHideSeconds { get; set; } = 12;
    public bool PlaySound { get; set; } = true;
    public string? SoundFile { get; set; }   // null = 시스템 알림음(다운로드)
    public string? CaptureSoundFile { get; set; } // null = 기본 셔터음(캡처)
    public string? ScreenshotFolder { get; set; } // null = 사진\Screenshots
    public bool CopyToClipboard { get; set; } = true;
    public string ToastPosition { get; set; } = "Right"; // Right | Left | Center | Mouse
    public bool ClipboardFollow { get; set; } = true;    // (구버전 호환용, 미사용)
    public int ClipboardFollowMs { get; set; } = 2200;   // (구버전 호환용, 미사용)
    public bool ShowChips { get; set; } = true;          // 커서 옆 미니 썸네일 칩 박스
    public bool ShowPreview { get; set; } = true;        // 캡처/복사 내용 미리보기 박스
    public bool KeepUntilPaste { get; set; } = true;     // 자동 숨김 끄고 붙여넣기(Ctrl+V)까지 유지
    public bool YoutubeDownload { get; set; } = true;    // 유튜브 링크 복사 시 H264 자동 다운로드
    public string? YoutubeDir { get; set; }              // null = 다운로드 폴더

    public string ResolvedYoutubeDir =>
        !string.IsNullOrWhiteSpace(YoutubeDir) && Directory.Exists(YoutubeDir) ? YoutubeDir! : ResolvedDownloads;
    public bool CaptureCursor { get; set; } = true;      // 캡처에 마우스 포인터 그려넣기(활성창/전체)
    public string? AnnotColor { get; set; }              // 편집기 마지막 색 (#RRGGBB)
    public bool PenAutoShape { get; set; } = true;       // 펜으로 그린 원·화살표·직선 자동 정리
    public bool ShowMiniCard { get; set; } = true;       // 캡처 후 오른쪽 아래 미니 창
    public string DefaultTool { get; set; } = "arrow";  // 편집창 열 때 바로 선택될 도구

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snatch");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public string ResolvedDownloads =>
        !string.IsNullOrWhiteSpace(DownloadsFolder) && Directory.Exists(DownloadsFolder)
            ? DownloadsFolder!
            : Win32.GetDownloadsFolder();

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s != null) return s;
            }
        }
        catch { /* fall through to defaults */ }

        // first run defaults
        var def = new Settings();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        def.Favorites.Add(new Favorite { Name = "바탕화면", Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) });
        def.Favorites.Add(new Favorite { Name = "문서", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) });
        def.Save();
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* ignore persist errors */ }
    }
}
