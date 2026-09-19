using System.Buffers.Binary;
using System.Text;

namespace MusicDownloader;

/// <summary>
/// 极简 FLAC 元数据写入器：在保留 STREAMINFO 与其他块的前提下，
/// 重写 VORBIS_COMMENT（标签）与 PICTURE（内嵌封面）块。
/// 不依赖任何第三方库。
/// </summary>
public static class FlacTagger
{
    private const int BlockStreamInfo = 0;
    private const int BlockPadding = 1;
    private const int BlockVorbisComment = 4;
    private const int BlockPicture = 6;

    /// <summary>写入标签与可选封面（原地替换，原子写）。</summary>
    public static void WriteTags(string path, IReadOnlyDictionary<string, string> tags, byte[]? cover = null)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8 || bytes[0] != (byte)'f' || bytes[1] != (byte)'L' || bytes[2] != (byte)'a' || bytes[3] != (byte)'C')
            throw new InvalidDataException("不是有效的 FLAC 文件");

        int pos = 4;
        var kept = new List<(int Type, byte[] Data)>();
        long audioStart = -1;

        while (true)
        {
            if (pos + 4 > bytes.Length) throw new InvalidDataException("FLAC 元数据块截断");
            bool last = (bytes[pos] & 0x80) != 0;
            int type = bytes[pos] & 0x7F;
            int len = (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3];
            if (pos + 4 + len > bytes.Length) throw new InvalidDataException("FLAC 元数据块长度越界");
            var data = new byte[len];
            Array.Copy(bytes, pos + 4, data, 0, len);
            pos += 4 + len;

            // 丢弃旧的标签/封面/填充块，其余保留（含 STREAMINFO）
            if (type != BlockVorbisComment && type != BlockPicture && type != BlockPadding)
                kept.Add((type, data));

            if (last) { audioStart = pos; break; }
        }

        var blocks = new List<(int Type, byte[] Data)>(kept);
        blocks.Add((BlockVorbisComment, BuildVorbisComment(tags)));
        if (cover is { Length: > 0 })
        {
            var (w, h) = ImageSize.Detect(cover);
            blocks.Add((BlockPicture, BuildPicture(cover, w, h)));
        }

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, 4); // "fLaC"
            for (int i = 0; i < blocks.Count; i++)
            {
                bool last = i == blocks.Count - 1;
                fs.WriteByte((byte)((last ? 0x80 : 0x00) | blocks[i].Type));
                int len = blocks[i].Data.Length;
                fs.WriteByte((byte)(len >> 16));
                fs.WriteByte((byte)(len >> 8));
                fs.WriteByte((byte)len);
                fs.Write(blocks[i].Data, 0, len);
            }
            fs.Write(bytes, (int)audioStart, bytes.Length - (int)audioStart);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>构建 VORBIS_COMMENT 块数据（键值全部小端长度前缀）。</summary>
    private static byte[] BuildVorbisComment(IReadOnlyDictionary<string, string> tags)
    {
        const string vendor = "MusicDownloader";
        using var ms = new MemoryStream();
        var vendorBytes = Encoding.UTF8.GetBytes(vendor);
        WriteLe32(ms, vendorBytes.Length);
        ms.Write(vendorBytes);

        var entries = new List<byte[]>();
        foreach (var kv in tags)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            entries.Add(Encoding.UTF8.GetBytes($"{kv.Key.ToUpperInvariant()}={kv.Value}"));
        }
        WriteLe32(ms, entries.Count);
        foreach (var e in entries)
        {
            WriteLe32(ms, e.Length);
            ms.Write(e);
        }
        return ms.ToArray();
    }

    /// <summary>构建 PICTURE 块数据（大端字段）。</summary>
    private static byte[] BuildPicture(byte[] image, int width, int height)
    {
        // 通过数据特征判断 MIME，避免依赖文件名
        var mime = ImageSize.DetectMime(image);
        using var ms = new MemoryStream();
        WriteBe32(ms, 3); // picture type 3 = front cover
        var mimeBytes = Encoding.ASCII.GetBytes(mime);
        WriteBe32(ms, mimeBytes.Length);
        ms.Write(mimeBytes);
        var desc = Encoding.UTF8.GetBytes("Cover");
        WriteBe32(ms, desc.Length);
        ms.Write(desc);
        WriteBe32(ms, width);
        WriteBe32(ms, height);
        WriteBe32(ms, 24);   // color depth（展示用途）
        WriteBe32(ms, 0);    // colors used
        WriteBe32(ms, image.Length);
        ms.Write(image);
        return ms.ToArray();
    }

    private static void WriteLe32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteBe32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        s.Write(b);
    }
}

/// <summary>从图片字节流中探测格式与像素尺寸（支持 JPEG/PNG/GIF/WebP）。</summary>
public static class ImageSize
{
    public static string DetectMime(byte[] d)
    {
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8) return "image/jpeg";
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length > 3 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return "image/gif";
        if (d.Length > 12 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return "image/webp";
        return "image/jpeg";
    }

    /// <summary>返回 (宽, 高)；无法解析时回退 (0,0)。</summary>
    public static (int Width, int Height) Detect(byte[] d)
    {
        var mime = DetectMime(d);
        try
        {
            return mime switch
            {
                "image/png" => PngSize(d),
                "image/jpeg" => JpegSize(d),
                "image/gif" => GifSize(d),
                _ => (0, 0),
            };
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (int, int) PngSize(byte[] d)
    {
        // IHDR: 8 字节签名 + 4 长度 + 4 类型，随后宽高各 4 字节大端
        int w = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(16, 4));
        int h = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(20, 4));
        return (w, h);
    }

    private static (int, int) GifSize(byte[] d)
        => (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6, 2)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8, 2)));

    private static (int, int) JpegSize(byte[] d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte marker = d[i + 1];
            // SOF0..SOF15（排除 DHT=0xC4, JPG=0xC8, DAC=0xCC）含尺寸信息
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                int h = (d[i + 5] << 8) | d[i + 6];
                int w = (d[i + 7] << 8) | d[i + 8];
                return (w, h);
            }
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            int segLen = (d[i + 2] << 8) | d[i + 3];
            i += 2 + segLen;
        }
        return (0, 0);
    }
}
