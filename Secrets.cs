using System.Text;

namespace MusicDownloader;

/// <summary>
/// 编译期内置密钥。
///
/// 仓库中的此实现**刻意留空**，因此公开源码不含任何凭证。
/// 制作「内置账号」的分发版时运行 <c>build-friend.ps1</c>：它从本地
/// cookie 文件生成被 .gitignore 排除的 <c>Secrets.Local.cs</c>，
/// 并由 csproj 定义 <c>LOCAL_SECRETS</c> 后编译进 exe。
///
/// Cookie 以 XOR + Base64 轻度混淆存放（提高直接提取门槛；这**不是加密**，
/// 能运行该 exe 的人理论上仍可还原出凭证，请勿用主账号制作分发版）。
/// </summary>
internal static partial class Secrets
{
    /// <summary>与构建脚本保持一致的混淆密钥。</summary>
    private const string ObfuscationKey = "MusicDL-2026-obfuscate";

    /// <summary>内置的网易云 Cookie（未内置时为空串）。</summary>
    public static string BuiltInCookie
    {
        get
        {
#if LOCAL_SECRETS
            return Decode(BuiltInCookieEncoded);
#else
            return "";
#endif
        }
    }

#if LOCAL_SECRETS
    private static string Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return "";
        var data = Convert.FromBase64String(encoded);
        var key = Encoding.UTF8.GetBytes(ObfuscationKey);
        for (int i = 0; i < data.Length; i++) data[i] ^= key[i % key.Length];
        return Encoding.UTF8.GetString(data);
    }
#endif
}
