using System.Buffers.Binary;
using System.Text;

namespace MusicDownloader;

/// <summary>
/// 极简 ID3v2.3 标签写入器：在文件头部写入/替换 ID3v2 标签，
/// 支持文本帧与 APIC 内嵌封面，中文使用 UTF-16（带 BOM）以获得最佳兼容性。
/// 不依赖任何第三方库。
/// </summary>
public static class Mp3Tagger
{
    // 文本帧编码：0=ISO-8859-1, 1=UTF-16(带BOM), 3=UTF-8
    private const byte EncodingUtf16 = 1;

    /// <summary>写入标签与可选封面（原地替换，原子写）。</summary>
    public static void WriteTags(string path, IReadOnlyDictionary<string, string> tags, byte[]? cover = null)
    {
        var bytes = File.ReadAllBytes(path);
        var audioStart = SkipExistingId3v2(bytes);

        var frames = new List<byte[]>();
        AddTextFrame(frames, "TIT2", Get(tags, "TITLE"));
        AddTextFrame(frames, "TPE1", Get(tags, "ARTIST"));
        AddTextFrame(frames, "TALB", Get(tags, "ALBUM"));
        AddTextFrame(frames, "TPE2", Get(tags, "ALBUMARTIST"));
        AddTextFrame(frames, "TRCK", Get(tags, "TRACKNUMBER"));
        AddTextFrame(frames, "TCON", Get(tags, "GENRE"));
        var year = Get(tags, "DATE");
        if (year.Length == 0) year = Get(tags, "YEAR");
        AddTextFrame(frames, "TYER", year);
        if (cover is { Length: > 0 })
            frames.Add(BuildApic(cover));

        var body = new MemoryStream();
        foreach (var f in frames) body.Write(f, 0, f.Length);
        var bodyBytes = body.ToArray();

        var header = new byte[10];
        header[0] = (byte)'I';
        header[1] = (byte)'D';
        header[2] = (byte)'3';
        header[3] = 3;      // 主版本 2.3
        header[4] = 0;      // 修订号
        header[5] = 0;      // flags
        WriteSyncSafe(header.AsSpan(6, 4), bodyBytes.Length);

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(header, 0, header.Length);
            fs.Write(bodyBytes, 0, bodyBytes.Length);
            fs.Write(bytes, audioStart, bytes.Length - audioStart);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>返回音频数据起始偏移（跳过已有的 ID3v2 标签）。</summary>
    private static int SkipExistingId3v2(byte[] d)
    {
        if (d.Length < 10) return 0;
        if (d[0] != (byte)'I' || d[1] != (byte)'D' || d[2] != (byte)'3') return 0;
        int size = ReadSyncSafe(d.AsSpan(6, 4));
        var total = 10 + size;
        // 若存在 footer（flags bit4），再加 10 字节
        if ((d[5] & 0x10) != 0) total += 10;
        return total < d.Length ? total : 0;
    }

    private static string Get(IReadOnlyDictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var v) ? v ?? "" : "";

    private static void AddTextFrame(List<byte[]> frames, string id, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var payload = new List<byte> { EncodingUtf16 };
        payload.AddRange(Encoding.Unicode.GetPreamble()); // UTF-16 LE BOM
        payload.AddRange(Encoding.Unicode.GetBytes(text));
        frames.Add(BuildFrame(id, payload.ToArray()));
    }

    private static byte[] BuildApic(byte[] image)
    {
        var mime = Encoding.ASCII.GetBytes(ImageSize.DetectMime(image));
        var payload = new List<byte> { 0 }; // ISO-8859-1 用于 mime/描述
        payload.AddRange(mime);
        payload.Add(0);                     // mime 结束符
        payload.Add(3);                     // picture type: front cover
        payload.Add(0);                     // 描述结束符（空描述）
        payload.AddRange(image);
        return BuildFrame("APIC", payload.ToArray());
    }

    private static byte[] BuildFrame(string id, byte[] payload)
    {
        var frame = new byte[10 + payload.Length];
        for (int i = 0; i < 4; i++) frame[i] = (byte)id[i];
        // v2.3 帧长度为大端普通整数（非 syncsafe）
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), payload.Length);
        frame[8] = 0; // flags
        frame[9] = 0;
        Array.Copy(payload, 0, frame, 10, payload.Length);
        return frame;
    }

    private static void WriteSyncSafe(Span<byte> dst, int value)
    {
        dst[0] = (byte)((value >> 21) & 0x7F);
        dst[1] = (byte)((value >> 14) & 0x7F);
        dst[2] = (byte)((value >> 7) & 0x7F);
        dst[3] = (byte)(value & 0x7F);
    }

    private static int ReadSyncSafe(ReadOnlySpan<byte> src)
        => ((src[0] & 0x7F) << 21) | ((src[1] & 0x7F) << 14) | ((src[2] & 0x7F) << 7) | (src[3] & 0x7F);
}
