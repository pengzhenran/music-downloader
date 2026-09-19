using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MusicDownloader;

internal static class Program
{
    private static Config _config = Config.Load()!;
    private static readonly Dictionary<string, DownloadTask> Tasks = new();
    private static readonly object TaskLock = new();
    private static string _baseUrl = "";

    [STAThread]
    private static void Main()
    {
        ApplyBuiltInCookie();

        var port = FindFreePort(18923);
        _baseUrl = $"http://127.0.0.1:{port}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(_baseUrl);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            NativeHost.ShowError($"无法启动本地服务（端口 {port}）：{ex.Message}");
            return;
        }

        // 后台接收 HTTP 请求（界面线程跑 Win32 消息循环）
        _ = Task.Run(() => AcceptLoopAsync(listener));

        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "音乐下载器", "WebView2");

        // 内置账号版在窗口标题上明确标识，避免与公开版混淆
        var windowTitle = Secrets.BuiltInCookie.Length > 0
            ? "音乐下载器（已内置账号）"
            : "音乐下载器";

        NativeHost.Run(_baseUrl, userDataDir, windowTitle);
    }

    /// <summary>
    /// 首次运行时把编译期内置的 Cookie 写入用户配置，
    /// 这样「设置」界面里可见、可改。源码构建的版本此值为空，不产生任何影响。
    /// </summary>
    private static void ApplyBuiltInCookie()
    {
        if (Secrets.BuiltInCookie.Length == 0) return;
        if (_config.Cookie.Length > 0) return;
        _config.Cookie = Secrets.BuiltInCookie;
        _config.Save();
    }

    private static async Task AcceptLoopAsync(HttpListener listener)
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private static int FindFreePort(int start)
    {
        for (int p = start; p < start + 50; p++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { /* 端口被占用，试下一个 */ }
        }
        return start;
    }

    // ---------------- HTTP 处理 ----------------

    private static async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/api/", StringComparison.Ordinal))
                await HandleApiAsync(ctx, path);
            else
                await ServeStaticAsync(ctx, path);
        }
        catch (Exception ex)
        {
            try { await WriteJsonAsync(ctx, 500, $"{{\"error\":{Json.Esc(ex.Message)}}}"); }
            catch { /* 连接可能已断开 */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static async Task HandleApiAsync(HttpListenerContext ctx, string path)
    {
        var query = ctx.Request.QueryString;
        switch (path)
        {
            case "/api/settings":
                if (ctx.Request.HttpMethod == "POST")
                {
                    var body = await ReadBodyAsync(ctx);
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("cookie", out var ck)) _config.Cookie = ck.GetString() ?? "";
                    if (root.TryGetProperty("outputDir", out var od)) _config.OutputDir = od.GetString() ?? _config.OutputDir;
                    if (root.TryGetProperty("naming", out var nm)) _config.Naming = nm.GetString() ?? _config.Naming;
                    if (root.TryGetProperty("embedCover", out var ec)) _config.EmbedCover = ec.GetBoolean();
                    if (root.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.String) _config.Quality = q.GetString() ?? _config.Quality;
                    _config.Save();
                    await WriteJsonAsync(ctx, 200, "{\"ok\":true}");
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.Append('{');
                    sb.Append("\"cookie\":").Append(Json.Esc(_config.Cookie)).Append(',');
                    sb.Append("\"hasCookie\":").Append(_config.Cookie.Length > 0 ? "true" : "false").Append(',');
                    sb.Append("\"outputDir\":").Append(Json.Esc(_config.OutputDir)).Append(',');
                    sb.Append("\"naming\":").Append(Json.Esc(_config.Naming)).Append(',');
                    sb.Append("\"embedCover\":").Append(_config.EmbedCover ? "true" : "false").Append(',');
                    sb.Append("\"quality\":").Append(Json.Esc(_config.Quality));
                    sb.Append('}');
                    await WriteJsonAsync(ctx, 200, sb.ToString());
                }
                return;

            case "/api/browse":
            {
                // 弹出原生「选择文件夹」对话框。仅在用户点击浏览时触发。
                try
                {
                    var picked = FolderPicker.Pick("选择下载目录", _config.OutputDir);
                    if (string.IsNullOrWhiteSpace(picked))
                        await WriteJsonAsync(ctx, 200, "{\"cancelled\":true}");
                    else
                        await WriteJsonAsync(ctx, 200, $"{{\"path\":{Json.Esc(picked)}}}");
                }
                catch (Exception ex)
                {
                    await WriteJsonAsync(ctx, 500, $"{{\"error\":{Json.Esc("无法打开文件夹选择器：" + ex.Message)}}}");
                }
                return;
            }

            case "/api/search":
            {
                var kw = query["q"] ?? "";
                if (kw.Trim().Length == 0) { await WriteJsonAsync(ctx, 200, "{\"songs\":[]}"); return; }
                var client = new NeteaseClient(_config.Cookie);
                var hits = await client.SearchAsync(kw, 30, CancellationToken.None);
                var sb = new StringBuilder("{\"songs\":[");
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(SongJson(hits[i]));
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, 200, sb.ToString());
                return;
            }

            case "/api/album":
            {
                var idStr = query["id"] ?? "0";
                if (!long.TryParse(idStr, out var albumId)) { await WriteJsonAsync(ctx, 400, "{\"error\":\"bad id\"}"); return; }
                var client = new NeteaseClient(_config.Cookie);
                var (albumName, pic, songs) = await client.GetAlbumAsync(albumId, CancellationToken.None);
                var sb = new StringBuilder();
                sb.Append("{\"album\":").Append(Json.Esc(albumName));
                sb.Append(",\"pic\":").Append(Json.Esc(pic));
                sb.Append(",\"songs\":[");
                for (int i = 0; i < songs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(SongJson(songs[i]));
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, 200, sb.ToString());
                return;
            }

            case "/api/download":
            {
                var body = await ReadBodyAsync(ctx);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var ids = new List<long>();
                if (root.TryGetProperty("songIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Number) ids.Add(e.GetInt64());
                if (ids.Count == 0) { await WriteJsonAsync(ctx, 400, "{\"error\":\"没有选择歌曲\"}"); return; }

                var task = new DownloadTask { Id = Guid.NewGuid().ToString("N")[..8], Total = ids.Count };
                lock (TaskLock) Tasks[task.Id] = task;
                _ = Task.Run(() => RunDownloadAsync(task, ids));
                await WriteJsonAsync(ctx, 200, $"{{\"taskId\":{Json.Esc(task.Id)}}}");
                return;
            }

            case "/api/task":
            {
                var id = query["id"] ?? "";
                DownloadTask? t;
                lock (TaskLock) Tasks.TryGetValue(id, out t);
                if (t is null) { await WriteJsonAsync(ctx, 404, "{\"error\":\"task not found\"}"); return; }
                await WriteJsonAsync(ctx, 200, t.ToJson());
                return;
            }

            case "/api/open":
            {
                try
                {
                    Directory.CreateDirectory(_config.OutputDir);
                    Process.Start(new ProcessStartInfo(_config.OutputDir) { UseShellExecute = true });
                    await WriteJsonAsync(ctx, 200, "{\"ok\":true}");
                }
                catch (Exception ex) { await WriteJsonAsync(ctx, 500, $"{{\"error\":{Json.Esc(ex.Message)}}}"); }
                return;
            }

            case "/api/about":
            {
                // 版本与运行环境信息，供「关于」面板展示
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append("\"name\":").Append(Json.Esc("音乐下载器")).Append(',');
                sb.Append("\"version\":").Append(Json.Esc(version)).Append(',');
                sb.Append("\"runtime\":").Append(Json.Esc(".NET 8 · 独立窗口")).Append(',');
                sb.Append("\"hasBuiltInAccount\":").Append(Secrets.BuiltInCookie.Length > 0 ? "true" : "false").Append(',');
                sb.Append("\"repo\":").Append(Json.Esc("https://github.com/pengzhenran/music-downloader"));
                sb.Append('}');
                await WriteJsonAsync(ctx, 200, sb.ToString());
                return;
            }

            case "/api/check-cookie":
            {
                // 真实校验 Cookie：读取账号 profile，而非仅判断字符串非空
                var cookieToCheck = _config.Cookie;
                if (ctx.Request.HttpMethod == "POST")
                {
                    var raw = await ReadBodyAsync(ctx);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.TryGetProperty("cookie", out var ck) && ck.ValueKind == JsonValueKind.String)
                            cookieToCheck = ck.GetString() ?? "";
                    }
                }

                if (string.IsNullOrWhiteSpace(cookieToCheck))
                {
                    await WriteJsonAsync(ctx, 200,
                        "{\"valid\":false,\"reason\":\"empty\",\"isVip\":false,\"message\":\"尚未填写 Cookie\"}");
                    return;
                }

                try
                {
                    var client = new NeteaseClient(cookieToCheck);
                    var info = await client.GetAccountAsync(CancellationToken.None);
                    var isVip = info.VipType > 0;
                    var message = info.Valid
                        ? (isVip
                            ? $"账号有效：{info.Nickname}（VIP 已开通）"
                            : $"账号有效：{info.Nickname}（未开通 VIP，将无法获取无损）")
                        : "Cookie 无效或已过期，请重新获取";
                    var sb = new StringBuilder();
                    sb.Append('{');
                    sb.Append("\"valid\":").Append(info.Valid ? "true" : "false").Append(',');
                    sb.Append("\"isVip\":").Append(isVip ? "true" : "false").Append(',');
                    sb.Append("\"nickname\":").Append(Json.Esc(info.Nickname)).Append(',');
                    sb.Append("\"userId\":").Append(info.UserId).Append(',');
                    sb.Append("\"vipType\":").Append(info.VipType).Append(',');
                    sb.Append("\"reason\":").Append(Json.Esc(info.Valid ? "ok" : "invalid")).Append(',');
                    sb.Append("\"message\":").Append(Json.Esc(message));
                    sb.Append('}');
                    await WriteJsonAsync(ctx, 200, sb.ToString());
                }
                catch (Exception ex)
                {
                    await WriteJsonAsync(ctx, 200,
                        $"{{\"valid\":false,\"isVip\":false,\"reason\":\"error\",\"message\":{Json.Esc("检测失败：" + ex.Message)}}}");
                }
                return;
            }

            default:
                await WriteJsonAsync(ctx, 404, "{\"error\":\"not found\"}");
                return;
        }
    }

    private static string SongJson(SongHit s)
        => $"{{\"id\":{s.Id},\"name\":{Json.Esc(s.Name)},\"artists\":{Json.Esc(s.Artists)},"
         + $"\"album\":{Json.Esc(s.Album)},\"albumId\":{s.AlbumId},"
         + $"\"pic\":{Json.Esc(s.PicUrl)},\"duration\":{s.DurationMs}}}";

    private static async Task<string> ReadBodyAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    // ---------------- 静态资源（嵌入式） ----------------

    private static async Task ServeStaticAsync(HttpListenerContext ctx, string path)
    {
        if (path == "/" || path.Length == 0) path = "/index.html";
        var name = "MusicDownloader.wwwroot" + path.Replace('/', '.');
        var asm = Assembly.GetExecutingAssembly();
        await using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var mime = path.EndsWith(".html") ? "text/html; charset=utf-8"
                 : path.EndsWith(".css") ? "text/css; charset=utf-8"
                 : path.EndsWith(".js") ? "application/javascript; charset=utf-8"
                 : "application/octet-stream";
        ctx.Response.ContentType = mime;
        ctx.Response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(ctx.Response.OutputStream);
    }

    // ---------------- 下载流程 ----------------

    private static async Task RunDownloadAsync(DownloadTask task, List<long> songIds)
    {
        var client = new NeteaseClient(_config.Cookie);
        Directory.CreateDirectory(_config.OutputDir);

        foreach (var songId in songIds)
        {
            var item = new TaskItem { SongId = songId, Status = "准备中", Progress = 0 };
            lock (TaskLock) task.Items.Add(item);
            try
            {
                await DownloadOneAsync(client, item, songId);
                lock (TaskLock) { task.Completed++; item.Status = "完成"; item.Progress = 100; }
            }
            catch (Exception ex)
            {
                lock (TaskLock) { task.Failed++; item.Status = "失败"; item.Error = ex.Message; }
            }
            // 曲目之间留出间隔，降低被网易云限流的概率
            await Task.Delay(400);
        }
        lock (TaskLock) task.Status = "已完成";
    }

    private static async Task DownloadOneAsync(NeteaseClient client, TaskItem item, long songId)
    {
        var ct = CancellationToken.None;

        // 1) 详情
        var detail = await client.GetDetailAsync(songId, ct)
            ?? throw new InvalidOperationException("无法获取歌曲信息");
        lock (TaskLock) item.Title = detail.Name;

        // 2) 按音质偏好获取播放地址（无损优先时可降级为 MP3）
        lock (TaskLock) item.Status = "获取播放地址";
        var play = await client.GetPlayUrlAsync(songId, _config.Quality, ct);
        if (play is null)
            throw new InvalidOperationException(client.HasCookie
                ? "无法获取播放地址（可能需要 VIP，或该曲目受版权/地区限制）"
                : "需要先在设置中填写网易云 Cookie（VIP）才能下载");

        var isFlac = play.Type.Equals("flac", StringComparison.OrdinalIgnoreCase) || play.Level == "lossless";
        var ext = isFlac ? ".flac" : ".mp3";

        // 3) 组织文件名与目录
        var albumDir = Path.Combine(_config.OutputDir, Sanitize(string.IsNullOrWhiteSpace(detail.Album) ? "未知专辑" : detail.Album));
        Directory.CreateDirectory(albumDir);
        var baseName = _config.Naming == "artist-title"
            ? $"{Sanitize(detail.Artists)} - {Sanitize(detail.Name)}"
            : $"{(detail.TrackNo > 0 ? detail.TrackNo.ToString("00") : "00")}. {Sanitize(detail.Name)}";
        var audioPath = Path.Combine(albumDir, baseName + ext);
        var lrcPath = Path.Combine(albumDir, baseName + ".lrc");

        // 4) 下载音频
        lock (TaskLock)
        {
            item.Status = isFlac ? "下载中（无损）" : $"下载中（MP3 {play.Br / 1000}k）";
        }
        var progress = new Progress<long>(v => { lock (TaskLock) item.Progress = (int)Math.Min(100, v); });
        await using (var fs = new FileStream(audioPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await client.DownloadAsync(play.Url, fs, progress, ct);
        }

        // 5) 歌词
        lock (TaskLock) item.Status = "写入歌词";
        var lyric = await client.GetLyricAsync(songId, ct);
        if (!string.IsNullOrWhiteSpace(lyric))
            await File.WriteAllTextAsync(lrcPath, lyric, new UTF8Encoding(false), ct);

        // 6) 封面（一次/专辑）
        byte[]? coverBytes = null;
        if (!string.IsNullOrWhiteSpace(detail.PicUrl))
        {
            var coverPath = Path.Combine(albumDir, "cover.jpg");
            if (!File.Exists(coverPath))
            {
                try
                {
                    var coverUrl = detail.PicUrl.Contains('?') ? detail.PicUrl : detail.PicUrl + "?param=800y800";
                    await using var covFs = new FileStream(coverPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await client.DownloadAsync(coverUrl, covFs, null, ct);
                }
                catch { /* 封面失败不影响主流程 */ }
            }
            if (File.Exists(coverPath))
            {
                try { coverBytes = await File.ReadAllBytesAsync(coverPath, ct); } catch { }
            }
        }

        // 7) 写标签（按实际格式选择写入器）
        lock (TaskLock) item.Status = "写入标签";
        var tags = new Dictionary<string, string>
        {
            ["TITLE"] = detail.Name,
            ["ARTIST"] = detail.Artists,
            ["ALBUM"] = detail.Album,
            ["ALBUMARTIST"] = detail.Artists,
            ["DATE"] = detail.ReleaseYear,
            ["YEAR"] = detail.ReleaseYear,
        };
        if (detail.TrackNo > 0) tags["TRACKNUMBER"] = detail.TrackNo.ToString();

        try
        {
            if (isFlac)
                FlacTagger.WriteTags(audioPath, tags, _config.EmbedCover ? coverBytes : null);
            else
                Mp3Tagger.WriteTags(audioPath, tags, _config.EmbedCover ? coverBytes : null);
        }
        catch (Exception ex)
        {
            lock (TaskLock) item.Error = "标签写入失败：" + ex.Message;
        }

        lock (TaskLock)
        {
            item.Status = "完成";
            item.Progress = 100;
            item.Path = audioPath;
            item.Quality = isFlac ? "无损" : $"MP3 {play.Br / 1000}k";
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim();
    }
}
