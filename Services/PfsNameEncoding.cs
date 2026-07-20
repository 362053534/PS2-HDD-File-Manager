using System.Text;

namespace PfsExplorerLite.Services;

/// <summary>
/// pfsshell 文件名按原始字节处理：通信用 Latin-1 保真，面向用户时再按系统 ANSI（中文 Windows 为 GBK）解码。
/// 这样既能正确往返正常中文名，也能删除/重命名历史上错误编码写入的乱码名。
/// </summary>
internal static class PfsNameEncoding
{
    // ISO-8859-1：字节与 char 一一对应，避免 ls/rm 往返时把乱码名解坏。
    private static readonly Encoding WireEncoding = Encoding.Latin1;
    private static readonly Encoding TextEncoding = Encoding.Default;

    public static Encoding Wire => WireEncoding;

    public static string ToShell(string unicodeName)
    {
        if (string.IsNullOrEmpty(unicodeName) || IsAscii(unicodeName))
            return unicodeName;
        return WireEncoding.GetString(TextEncoding.GetBytes(unicodeName));
    }

    public static string ToDisplay(string shellName)
    {
        if (string.IsNullOrEmpty(shellName) || IsAscii(shellName))
            return shellName;
        return TextEncoding.GetString(WireEncoding.GetBytes(shellName));
    }

    public static string ToDisplayPath(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
            return remotePath;
        var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts.Select(ToDisplay));
    }

    private static bool IsAscii(string value) => value.All(static c => c <= 0x7F);
}
