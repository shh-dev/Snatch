using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Snatch;

public class HistoryItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "text";   // text | image | color
    public string? Text { get; set; }
    public string? ColorHex { get; set; }
    public string? File { get; set; }            // image path
    public bool Owned { get; set; }              // true = we wrote this file, delete it with the item
    public DateTime Time { get; set; }
}

/// <summary>Persistent clipboard history: %APPDATA%\Snatch\history (index.json + image files).</summary>
public class ClipHistory
{
    public const int Max = 500;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snatch", "history");
    private static string IndexPath => Path.Combine(Dir, "index.json");

    /// <summary>Newest first.</summary>
    public List<HistoryItem> Items { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var json = File.ReadAllText(IndexPath);
            Items = JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? new();
            // drop entries whose image vanished
            Items.RemoveAll(i => i.Kind == "image" && (i.File == null || !File.Exists(i.File)));
        }
        catch { Items = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { /* ignore persist errors */ }
    }

    public HistoryItem? Add(ClipContent c)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var item = new HistoryItem { Id = Guid.NewGuid().ToString("N"), Time = DateTime.Now };

            if (c.IsColor && c.ColorHex is { } hex)
            {
                item.Kind = "color"; item.ColorHex = hex; item.Text = hex;
            }
            else if (c.IsImage && c.Image is { } img)
            {
                item.Kind = "image";
                if (c.ImagePath is { } p && File.Exists(p))
                {
                    item.File = p; item.Owned = false;      // capture file already on disk — just point at it
                }
                else
                {
                    var dest = Path.Combine(Dir, item.Id + ".png");
                    SavePng(img, dest);
                    item.File = dest; item.Owned = true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(c.Text))
            {
                item.Kind = "text"; item.Text = c.Text;
            }
            else return null;

            // 같은 내용이 이미 맨 위면 중복 추가 안 함
            if (Items.Count > 0 && SameAsTop(Items[0], item)) return Items[0];

            Items.Insert(0, item);
            Trim();
            Save();
            return item;
        }
        catch { return null; }
    }

    private static bool SameAsTop(HistoryItem top, HistoryItem now) =>
        top.Kind == now.Kind &&
        top.Kind switch
        {
            "text" => top.Text == now.Text,
            "color" => top.ColorHex == now.ColorHex,
            "image" => top.File != null && top.File == now.File,
            _ => false,
        };

    private void Trim()
    {
        while (Items.Count > Max)
        {
            var old = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            DeleteFile(old);
        }
    }

    public void Remove(HistoryItem item)
    {
        Items.Remove(item);
        DeleteFile(item);
        Save();
    }

    public void Clear()
    {
        foreach (var i in Items) DeleteFile(i);
        Items.Clear();
        Save();
    }

    private static void DeleteFile(HistoryItem i)
    {
        if (!i.Owned || i.File == null) return;
        try { if (File.Exists(i.File)) File.Delete(i.File); } catch { }
    }

    private static void SavePng(BitmapSource src, string dest)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }

    /// <summary>Small thumbnail for the list, or null.</summary>
    public static BitmapSource? Thumb(HistoryItem i, int px = 96)
    {
        if (i.Kind != "image" || i.File == null || !File.Exists(i.File)) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = px;
            bi.UriSource = new Uri(i.File);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Full-fidelity content for putting back on the clipboard.</summary>
    public static ClipContent ToClip(HistoryItem i)
    {
        if (i.Kind == "color") return new ClipContent { ColorHex = i.ColorHex };
        if (i.Kind == "image" && i.File != null && File.Exists(i.File))
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(i.File);
            bi.EndInit();
            bi.Freeze();
            return new ClipContent { Image = bi, ImagePath = i.File };
        }
        return new ClipContent { Text = i.Text };
    }
}
