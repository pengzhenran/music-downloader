using System.Text;

namespace MusicDownloader;

/// <summary>
/// trim 安全的 JSON 输出辅助。PublishTrimmed 会裁剪反射元数据，
/// 因此不使用 JsonSerializer.Serialize(object)，改为手工构建与转义。
/// </summary>
public static class Json
{
    /// <summary>把字符串转成合法的 JSON 字符串字面量（含引号）。</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string Bool(bool v) => v ? "true" : "false";
}
