namespace PfsExplorerLite.Services.PfsNet;

/// <summary>与 ps2-server/PROTOCOL.md 对齐的常量。</summary>
internal static class PfsNetProtocol
{
    public const int CommandPort = 45061;
    public const int DataPort = 45062;
    public const uint Magic = 0x4E534650; // 'P''F''S''N' little-endian
    public const uint Version = 0x0001;
    public const int MaxControlPayload = 131072;
    public const int HeaderSize = 12;

    public const uint FeatFileOps = 1u << 0;
    public const uint FeatDataChannel = 1u << 1;
    public const uint FeatApaSector = 1u << 2;
    public const uint FeatApaMutate = 1u << 3;
    public const uint FeatStreamRing = 1u << 4;

    public const uint CmdHello = 0x0001;
    public const uint CmdPing = 0x0002;
    public const uint CmdMount = 0x0003;
    public const uint CmdUmount = 0x0004;
    public const uint CmdGetSession = 0x0005;
    public const uint CmdListPartitions = 0x0010;
    public const uint CmdGetApaFree = 0x0011;
    public const uint CmdGetPfsFree = 0x0012;
    public const uint CmdListDir = 0x0020;
    public const uint CmdMkdir = 0x0021;
    public const uint CmdRmdir = 0x0022;
    public const uint CmdRemove = 0x0023;
    public const uint CmdRename = 0x0024;
    public const uint CmdStat = 0x0025;
    public const uint CmdPutBegin = 0x0030;
    public const uint CmdPutEnd = 0x0031;
    public const uint CmdPutAbort = 0x0032;
    public const uint CmdGetBegin = 0x0033;
    public const uint CmdGetEnd = 0x0034;
    public const uint CmdGetAbort = 0x0035;
    public const uint CmdReadSectors = 0x0040;
    public const uint CmdApaCreatePfs = 0x0050;
    public const uint CmdApaRemove = 0x0051;
    public const uint CmdApaRename = 0x0052;
    public const uint CmdShutdown = 0x00FF;

    public const int Ok = 0;
    public const int ErrGeneric = -1;
    public const int ErrIo = -2;
    public const int ErrNoEnt = -3;
    public const int ErrExist = -4;
    public const int ErrNotEmpty = -5;
    public const int ErrNoSpc = -6;
    public const int ErrInval = -7;
    public const int ErrNotMounted = -8;
    public const int ErrBusy = -9;
    public const int ErrUnsupported = -10;
    public const int ErrVersion = -11;
    public const int ErrMagic = -12;
    public const int ErrDataConn = -13;

    public const string DevicePrefix = "pfsnet://";

    public static bool IsNetworkDevice(string? devicePath) =>
        !string.IsNullOrEmpty(devicePath)
        && devicePath.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase);

    public static string MakeDevicePath(string ipAddress) => DevicePrefix + ipAddress.Trim();

    public static string? TryGetHost(string devicePath)
    {
        if (!IsNetworkDevice(devicePath))
            return null;
        return devicePath[DevicePrefix.Length..].Trim();
    }

    public static string DescribeResult(int result) => result switch
    {
        Ok => "成功",
        ErrGeneric => "通用错误",
        ErrIo => "I/O 错误",
        ErrNoEnt => "不存在",
        ErrExist => "已存在",
        ErrNotEmpty => "目录非空",
        ErrNoSpc => "空间不足",
        ErrInval => "参数无效或被拒绝",
        ErrNotMounted => "未挂载",
        ErrBusy => "设备忙（请先卸载分区）",
        ErrUnsupported => "命令不受支持",
        ErrVersion => "协议版本不匹配",
        ErrMagic => "握手魔数不匹配",
        ErrDataConn => "数据通道失败",
        _ => $"错误 {result}"
    };

    public static string FormatCommandError(uint command, int result, string? partitionName = null)
    {
        if (command == CmdApaRemove
            && partitionName is { Length: >= 2 }
            && partitionName.StartsWith("__", StringComparison.Ordinal)
            && (result == ErrIo || result == ErrInval))
        {
            return $"无法删除分区“{partitionName}”：PS2 端 APA 驱动禁止删除以 __ 开头的分区（返回 {DescribeResult(result)}）。" +
                   "这与本地 pfsshell 不同；__.POPS 等需使用允许删除 __ 前缀的 ps2hdd.irx（APA_ALLOW_REMOVE_PARTITION_WITH_LEADING_UNDERSCORE）。";
        }

        return $"PfsNet 命令 0x{command:X4} 失败（{DescribeResult(result)}，result={result}）。";
    }
}

internal readonly record struct PfsNetPartitionInfo(string Name, bool IsSystem, bool IsPfs, ulong TotalBytes);

internal readonly record struct PfsNetDirEntry(string Name, bool IsDirectory, ulong Size, string Details);

internal readonly record struct PfsNetSpaceInfo(ulong TotalBytes, ulong FreeBytes, uint ZoneSizeBytes);
