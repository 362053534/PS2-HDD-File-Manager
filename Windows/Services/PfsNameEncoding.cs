using System.Globalization;
using System.Text;

namespace PfsExplorerLite.Services;

/// <summary>
/// PFS 文件名与 Windows 本地路径均按 UTF-8 字节经 Latin-1 保真传输（pfsshell）；
/// PfsNet 协议侧已是 UTF-8 解码后的 Unicode；PFS 目录项按 UTF-8 原始字节存放。
/// 参考本地 PS2SDK smbman：盘上/本地路径为 UTF-8 字节，边界处再转协议编码（SMB 为 UTF-16）。
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

    /// <summary>
    /// 显示名：pfsshell 线名（Latin-1 保真的 UTF-8 字节）→ Unicode；
    /// 若已是 Unicode（如 PfsNet 已 UTF-8 解码的字符串），原样返回。
    /// </summary>
    public static string ToDisplay(string shellName)
    {
        if (string.IsNullOrEmpty(shellName) || IsAscii(shellName))
            return shellName;
        // 含 >0xFF 码点说明已是解码后的 Unicode；再走 Latin-1 会把中文替换成 '?'。
        if (shellName.Any(static c => c > 0xFF))
            return shellName;
        return Utf8Encoding.GetString(WireEncoding.GetBytes(shellName));
    }

    public static string ToDisplayPath(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
            return remotePath;

        return "/" + string.Join('/', SplitRemotePath(remotePath).Select(ToDisplay));
    }

    /// <summary>
    /// 按 '/' 拆 PFS 路径。不可使用 <see cref="StringSplitOptions.TrimEntries"/>：
    /// UTF-8 经 Latin-1 保真后，末字节 0xA0（如「传」）会被当成 NBSP 剥掉，导致 cd 找不到目录。
    /// </summary>
    public static IReadOnlyList<string> SplitRemotePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return Array.Empty<string>();

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(TrimAscii)
            .Where(static part => part.Length > 0)
            .ToArray();
    }

    /// <summary>只去掉 ASCII 空白，保留 Latin-1 中的 0xA0 等 UTF-8 续字节。</summary>
    public static string TrimAscii(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var start = 0;
        var end = value.Length - 1;
        while (start <= end && IsAsciiSpace(value[start]))
            start++;
        while (end >= start && IsAsciiSpace(value[end]))
            end--;
        return start == 0 && end == value.Length - 1 ? value : value[start..(end + 1)];
    }

    private static bool IsAsciiSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

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
