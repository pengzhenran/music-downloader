using System.Text;
using System.Text.Json;

namespace MusicDownloader;

/// <summary>用户配置，持久化在 %APPDATA%\音乐下载器\config.json。</summary>
public sealed class Config
{
    public string Cookie { get; set; } = "";
    public string OutputDir { get; set; } = DefaultOutputDir();
    public string Naming { get; set; } = "track-title"; // track-title | artist-title
    public bool EmbedCover { get; set; } = true;
    /// <summary>音质偏好：lossless | lossless-fallback | mp3-320 | mp3-192</summary>
    public string Quality { get; set; } = "lossless-fallback";

    private static string DefaultOutputDir()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, "音乐下载");
    }

    private static string ConfigPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "音乐下载器");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static Config Load()
    {
        try
        {
            var path = ConfigPath();
            if (!File.Exists(path)) return new Config();
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            var cfg = new Config();
            if (root.TryGetProperty("cookie", out var c) && c.ValueKind == JsonValueKind.String) cfg.Cookie = c.GetString() ?? "";
            if (root.TryGetProperty("outputDir", out var o) && o.ValueKind == JsonValueKind.String && (o.GetString() ?? "").Length > 0) cfg.OutputDir = o.GetString()!;
            if (root.TryGetProperty("naming", out var n) && n.ValueKind == JsonValueKind.String) cfg.Naming = n.GetString() ?? "track-title";
            if (root.TryGetProperty("embedCover", out var e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)) cfg.EmbedCover = e.GetBoolean();
            if (root.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.String) cfg.Quality = q.GetString() ?? "lossless-fallback";
            return cfg;
        }
        catch
        {
            return new Config();
        }
    }

    public void Save()
    {
        try
        {
            var path = ConfigPath();
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"cookie\":").Append(Json.Esc(Cookie)).Append(',');
            sb.Append("\"outputDir\":").Append(Json.Esc(OutputDir)).Append(',');
            sb.Append("\"naming\":").Append(Json.Esc(Naming)).Append(',');
            sb.Append("\"embedCover\":").Append(EmbedCover ? "true" : "false").Append(',');
            sb.Append("\"quality\":").Append(Json.Esc(Quality));
            sb.Append('}');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* 配置写入失败不致命 */ }
    }
}

/// <summary>一个下载任务（可包含多首歌）。</summary>
public sealed class DownloadTask
{
    public string Id { get; init; } = "";
    public string Status { get; set; } = "进行中";
    public int Total { get; init; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public List<TaskItem> Items { get; } = new();

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"id\":").Append(Json.Esc(Id)).Append(',');
        sb.Append("\"status\":").Append(Json.Esc(Status)).Append(',');
        sb.Append("\"total\":").Append(Total).Append(',');
        sb.Append("\"completed\":").Append(Completed).Append(',');
        sb.Append("\"failed\":").Append(Failed).Append(',');
        sb.Append("\"items\":[");
        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Items[i].ToJson());
        }
        sb.Append("]}");
        return sb.ToString();
    }
}

/// <summary>任务中的单首歌。</summary>
public sealed class TaskItem
{
    public long SongId { get; init; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int Progress { get; set; }
    public string Error { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>实际下载到的音质（无损 / MP3 320k 等）。</summary>
    public string Quality { get; set; } = "";

    public string ToJson()
        => $"{{\"songId\":{SongId},\"title\":{Json.Esc(Title)},\"status\":{Json.Esc(Status)},"
         + $"\"progress\":{Progress},\"error\":{Json.Esc(Error)},\"path\":{Json.Esc(Path)},"
         + $"\"quality\":{Json.Esc(Quality)}}}";
}
