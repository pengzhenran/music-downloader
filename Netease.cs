using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicDownloader;

/// <summary>一首歌曲的摘要信息。</summary>
public sealed record SongHit(long Id, string Name, string Artists, string Album, long AlbumId, string PicUrl, long DurationMs);

/// <summary>歌曲详情（用于写入标签）。</summary>
public sealed record SongDetail(long Id, string Name, string Artists, string Album, long AlbumId, string PicUrl, long DurationMs, int TrackNo, string ReleaseYear);

/// <summary>
/// 网易云音乐 API 客户端。实现了 weapi 加密协议（AES-128-CBC 双重加密 + 裸 RSA），
/// 从而在有登录 Cookie 时可以获取无损音质播放地址。
/// </summary>
public sealed class NeteaseClient
{
    private const string Nonce = "0CoJUm6Qyw8W8jud";
    private const string PubKey = "010001";
    private const string Modulus =
        "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";

    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("0102030405060708");

    private readonly HttpClient _http;
    private readonly string _cookie;

    public NeteaseClient(string? cookie)
    {
        _cookie = cookie ?? string.Empty;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
    }

    public bool HasCookie => _cookie.Length > 0;

    // ---------- weapi 加密 ----------

    private static string AesEncryptBase64(string text, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var data = Encoding.UTF8.GetBytes(text);
        var cipher = enc.TransformFinalBlock(data, 0, data.Length);
        return Convert.ToBase64String(cipher);
    }

    private static string RsaEncryptSecret(string secret)
    {
        // 网易云协议：将 secret 字节反转后当作大端无符号整数，做 m^e mod n（无填充）
        var reversed = Encoding.UTF8.GetBytes(secret);
        Array.Reverse(reversed);
        var m = new BigInteger(reversed, isUnsigned: true, isBigEndian: true);
        var e = BigInteger.Parse(PubKey, System.Globalization.NumberStyles.HexNumber);
        var n = BigInteger.Parse(Modulus, System.Globalization.NumberStyles.HexNumber);
        var c = BigInteger.ModPow(m, e, n);
        return c.ToString("x").PadLeft(256, '0');
    }

    private static Dictionary<string, string> Weapi(string jsonText)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(); // 16 个十六进制字符
        var first = AesEncryptBase64(jsonText, Nonce);
        var second = AesEncryptBase64(first, secret);
        return new Dictionary<string, string>
        {
            ["params"] = second,
            ["encSecKey"] = RsaEncryptSecret(secret),
        };
    }

    // ---------- 请求辅助 ----------

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
        => await WithRetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (_cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", _cookie);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidDataException("服务端返回空响应（可能被限流）");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }, ct);

    private async Task<JsonElement> PostWeapiAsync(string url, string jsonPayload, CancellationToken ct)
        => await WithRetryAsync(async () =>
        {
            var form = Weapi(jsonPayload);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            };
            if (_cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", _cookie);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidDataException("服务端返回空响应（可能被限流）");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }, ct);

    /// <summary>带退避重试的请求执行（网易云对高频请求会返回空响应）。</summary>
    private static async Task<JsonElement> WithRetryAsync(Func<Task<JsonElement>> action, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 3) await Task.Delay(500 * (attempt + 1), ct);
            }
        }
        throw last ?? new InvalidOperationException("请求失败");
    }

    private static string JoinArtists(JsonElement song)
    {
        if (!song.TryGetProperty("artists", out var artists) && !song.TryGetProperty("ar", out artists))
            return string.Empty;
        var names = new List<string>();
        foreach (var a in artists.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var n) && n.GetString() is { } s) names.Add(s);
        }
        return string.Join("、", names);
    }

    private static string ReadString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long ReadLong(JsonElement el, string prop, params string[] alt)
    {
        foreach (var p in new[] { prop }.Concat(alt))
        {
            if (el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        }
        return 0;
    }

    // ---------- 公开 API ----------

    /// <summary>按关键词搜索歌曲。</summary>
    public async Task<List<SongHit>> SearchAsync(string keyword, int limit, CancellationToken ct)
    {
        var url = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(keyword)}&type=1&limit={limit}&offset=0";
        var json = await GetJsonAsync(url, ct);
        var list = new List<SongHit>();
        if (!json.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return list;
        if (!result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return list;

        foreach (var s in songs.EnumerateArray())
        {
            var id = ReadLong(s, "id");
            var name = ReadString(s, "name");
            var albumName = "";
            var albumId = 0L;
            var pic = "";
            if (s.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
            {
                albumName = ReadString(al, "name");
                albumId = ReadLong(al, "id");
                pic = ReadString(al, "picUrl");
            }
            var dur = ReadLong(s, "duration", "dt");
            list.Add(new SongHit(id, name, JoinArtists(s), albumName, albumId, pic, dur));
        }
        return list;
    }

    /// <summary>获取歌曲详情（含专辑曲序与发行年份）。</summary>
    public async Task<SongDetail?> GetDetailAsync(long songId, CancellationToken ct)
    {
        var url = $"https://music.163.com/api/song/detail?ids=%5B{songId}%5D";
        var json = await GetJsonAsync(url, ct);
        if (!json.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in songs.EnumerateArray())
        {
            var albumName = "";
            var albumId = 0L;
            var pic = "";
            var year = "";
            var trackNo = 0;
            if (s.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
            {
                albumName = ReadString(al, "name");
                albumId = ReadLong(al, "id");
                pic = ReadString(al, "picUrl");
                var publish = ReadLong(al, "publishTime");
                if (publish > 0)
                    year = DateTimeOffset.FromUnixTimeMilliseconds(publish).Year.ToString();
            }
            // 曲序：详情接口的 no 字段（专辑内序号）
            var no = ReadLong(s, "no");
            if (no > 0) trackNo = (int)no;
            return new SongDetail(
                ReadLong(s, "id"),
                ReadString(s, "name"),
                JoinArtists(s),
                albumName,
                albumId,
                pic,
                ReadLong(s, "duration", "dt"),
                trackNo,
                year);
        }
        return null;
    }

    /// <summary>获取专辑全部曲目。</summary>
    public async Task<(string AlbumName, string PicUrl, List<SongHit> Songs)> GetAlbumAsync(long albumId, CancellationToken ct)
    {
        var url = $"https://music.163.com/api/album/{albumId}";
        var json = await GetJsonAsync(url, ct);
        var songs = new List<SongHit>();
        var albumName = "";
        var pic = "";
        if (json.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = ReadString(album, "name");
            pic = ReadString(album, "picUrl");
            if (album.TryGetProperty("songs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.EnumerateArray())
                {
                    songs.Add(new SongHit(
                        ReadLong(s, "id"),
                        ReadString(s, "name"),
                        JoinArtists(s),
                        albumName,
                        albumId,
                        pic,
                        ReadLong(s, "duration", "dt")));
                }
            }
        }
        return (albumName, pic, songs);
    }

    /// <summary>获取歌词（LRC 文本）。</summary>
    public async Task<string> GetLyricAsync(long songId, CancellationToken ct)
    {
        var url = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
        var json = await GetJsonAsync(url, ct);
        if (json.TryGetProperty("lrc", out var lrc) && lrc.ValueKind == JsonValueKind.Object)
            return ReadString(lrc, "lyric");
        return string.Empty;
    }

    /// <summary>一次成功解析到的播放地址。</summary>
    public sealed record PlayUrl(string Url, long Size, string Type, long Br, string Level);

    /// <summary>
    /// 获取播放地址。preference 取值：
    /// <c>lossless</c>=仅无损；<c>lossless-fallback</c>=无损优先，失败降级 MP3；
    /// <c>mp3-320</c>=直接 320k MP3；<c>mp3-192</c>=直接 192k MP3。
    /// </summary>
    public async Task<PlayUrl?> GetPlayUrlAsync(long songId, string preference, CancellationToken ct)
    {
        var ladder = preference switch
        {
            "lossless" => new[] { ("lossless", "flac") },
            "mp3-320" => new[] { ("exhigh", "mp3") },
            "mp3-192" => new[] { ("higher", "mp3") },
            _ => new[] { ("lossless", "flac"), ("exhigh", "mp3"), ("higher", "mp3") },
        };

        foreach (var (level, encodeType) in ladder)
        {
            try
            {
                var json = await PostWeapiAsync(
                    "https://music.163.com/weapi/song/enhance/player/url/v1?csrf_token=",
                    $"{{\"ids\":[{songId}],\"level\":\"{level}\",\"encodeType\":\"{encodeType}\"}}",
                    ct);

                if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var d in data.EnumerateArray())
                {
                    var code = ReadLong(d, "code");
                    var url = ReadString(d, "url");
                    var type = ReadString(d, "type");
                    if (code == 200 && url.Length > 0)
                        return new PlayUrl(url, ReadLong(d, "size"), type.Length > 0 ? type : encodeType, ReadLong(d, "br"), level);
                }
            }
            catch
            {
                // 该音质档位失败，继续尝试下一档
            }
            await Task.Delay(250, ct);
        }
        return null;
    }

    /// <summary>下载二进制内容（图片/音频）。</summary>
    public async Task DownloadAsync(string url, Stream dest, IProgress<long>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (_cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", _cookie);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            progress?.Report(total > 0 ? read * 100 / total : read);
        }
    }

    /// <summary>下载文本内容。</summary>
    public async Task<string> DownloadStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (_cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", _cookie);
        using var resp = await _http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
