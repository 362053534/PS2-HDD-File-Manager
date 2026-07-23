using System.Globalization;
using System.Text;

namespace PfsExplorerLite.Services;

/// <summary>
/// PFS 文件名与 Windows 本地路径均按 UTF-8 字节经 Latin-1 保真传输；
/// PFS 目录项按 UTF-8 原始字节存放（不再使用 GBK）。
/// </summary>
internal static class PfsNameEncoding
{
    // PFS dentry pLen 为 u8（最长 255）；为 pfs0:/dir/ 前缀留余量。
    public const int MaxPfsFileNameBytes = 240;

    private static readonly Encoding WireEncoding = Encoding.Latin1;
    private static readonly Encoding Utf8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static Encoding Wire => WireEncoding;

    /// <summary>Unicode → shell 线（UTF-8 字节）。PFS 名与本地路径共用。</summary>
    public static string ToShell(string unicodeName)
    {
        if (string.IsNullOrEmpty(unicodeName) || IsAscii(unicodeName))
            return unicodeName;
        return WireEncoding.GetString(Utf8Encoding.GetBytes(unicodeName));
    }

    /// <summary>与 <see cref="ToShell"/> 相同（本地路径亦为 UTF-8）。</summary>
    public static string ToLocalShell(string unicodePath) => ToShell(unicodePath);

    /// <summary>
    /// 生成写入 PFS 的 Unicode 文件名：去掉路径非法字符与控制符，保留可 UTF-8 编码的内容，并限制字节长度。
    /// </summary>
    public static string ToPfsFileName(string unicodeName)
    {
        if (string.IsNullOrEmpty(unicodeName))
            return unicodeName;

        var cleaned = new StringBuilder(unicodeName.Length);
        foreach (var c in unicodeName.Normalize(NormalizationForm.FormC))
        {
            if (c == '\0' || char.IsControl(c))
                continue;
            if (c is '*' or '"' or '<' or '>' or '|' or '/' or '\\')
            {
                cleaned.Append('_');
                continue;
            }
            // Windows 文件名不允许 '?'；':' 改全角以免与盘符语义混淆
            if (c == '?')
            {
                cleaned.Append('_');
                continue;
            }
            if (c == ':')
            {
                cleaned.Append('：');
                continue;
            }
            cleaned.Append(c);
        }

        var text = cleaned.ToString();
        if (text.Length == 0)
            text = "_";
        return TruncateUtf8(text, MaxPfsFileNameBytes);
    }

    public static string ToDisplay(string shellName)
    {
        if (string.IsNullOrEmpty(shellName) || IsAscii(shellName))
            return shellName;
        return Utf8Encoding.GetString(WireEncoding.GetBytes(shellName));
    }

    public static string ToDisplayPath(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
            return remotePath;

        var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts.Select(ToDisplay));
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        if (Utf8Encoding.GetByteCount(text) <= maxBytes)
            return text;

        var ext = Path.GetExtension(text);
        var stem = Path.GetFileNameWithoutExtension(text);
        if (string.IsNullOrEmpty(stem))
            stem = "_";
        var extBytes = Utf8Encoding.GetByteCount(ext);
        var budget = Math.Max(1, maxBytes - extBytes);
        while (stem.Length > 1 && Utf8Encoding.GetByteCount(stem) > budget)
            stem = stem[..^1];
        while (stem.Length > 0 && Utf8Encoding.GetByteCount(stem + ext) > maxBytes)
            stem = stem[..^1];
        return (stem.Length == 0 ? "_" : stem) + ext;
    }

    private static bool IsAscii(string value) => value.All(static c => c <= 0x7F);
}
