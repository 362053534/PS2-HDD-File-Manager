using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PfsExplorerLite.Services;

/// <summary>
/// 按 \\.\PhysicalDriveN 定位磁盘设备并请求系统安全弹出（USB 等可移除设备）。
/// </summary>
internal static class DiskSafeEject
{
    private static readonly Guid GuidDevInterfaceDisk = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint DnRemovable = 0x00004000;
    private const int CrSuccess = 0;

    public static bool TryParsePhysicalDriveNumber(string devicePath, out int driveNumber)
    {
        driveNumber = -1;
        if (string.IsNullOrWhiteSpace(devicePath))
            return false;

        var match = Regex.Match(devicePath.Trim(), @"^\\\\\.\\PhysicalDrive(?<n>\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        return int.TryParse(match.Groups["n"].Value, out driveNumber) && driveNumber >= 0;
    }

    public static void EjectPhysicalDrive(int driveNumber)
    {
        if (driveNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(driveNumber));

        var instanceId = FindDiskInstanceId(driveNumber)
            ?? throw new InvalidOperationException($"未找到 PhysicalDrive{driveNumber} 对应的系统磁盘设备。");

        var vetoName = new StringBuilder(260);
        var lastError = $"无法弹出 PhysicalDrive{driveNumber}。";

        foreach (var node in EnumerateEjectCandidates(instanceId))
        {
            vetoName.Clear();
            var status = CM_Request_Device_Eject(node, out var vetoType, vetoName, vetoName.Capacity, 0);
            if (status == CrSuccess)
                return;

            lastError = $"弹出 PhysicalDrive{driveNumber} 被拒绝（CfgMgr=0x{status:X8}, VetoType={vetoType}, Veto={vetoName}）。请确认没有其它程序占用该硬盘。";
        }

        throw new InvalidOperationException(lastError);
    }

    private static string? FindDiskInstanceId(int driveNumber)
    {
        var classGuid = GuidDevInterfaceDisk;
        var deviceInfoSet = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            throw new InvalidOperationException("无法枚举系统磁盘设备。");

        try
        {
            var interfaceData = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref classGuid, index, ref interfaceData); index++)
            {
                if (!TryGetInterfaceDetail(deviceInfoSet, ref interfaceData, out var devicePath, out var deviceInfoData))
                    continue;

                if (!TryGetStorageDeviceNumber(devicePath, out var number) || number.DeviceNumber != driveNumber)
                    continue;

                var instanceId = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfoData, instanceId, instanceId.Capacity, out _))
                    continue;

                return instanceId.ToString();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return null;
    }

    private static IEnumerable<uint> EnumerateEjectCandidates(string instanceId)
    {
        if (CM_Locate_DevNode(out var node, instanceId, 0) != CrSuccess)
            yield break;

        // 优先弹出带 DN_REMOVABLE 的父设备（典型 USB 硬盘盒），再回退到磁盘节点本身。
        var removableParents = new List<uint>();
        var current = node;
        for (var depth = 0; depth < 12; depth++)
        {
            if (CM_Get_DevNode_Status(out var status, out _, current, 0) == CrSuccess
                && (status & DnRemovable) != 0
                && current != node)
            {
                removableParents.Add(current);
            }

            if (CM_Get_Parent(out var parent, current, 0) != CrSuccess)
                break;
            current = parent;
        }

        foreach (var parent in removableParents)
            yield return parent;
        yield return node;
    }

    private static bool TryGetInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData interfaceData,
        out string devicePath,
        out SpDevinfoData deviceInfoData)
    {
        devicePath = string.Empty;
        deviceInfoData = new SpDevinfoData { cbSize = Marshal.SizeOf<SpDevinfoData>() };

        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
        if (requiredSize <= 0)
            return false;

        var buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, ref deviceInfoData))
                return false;

            devicePath = Marshal.PtrToStringUni(buffer + 4) ?? string.Empty;
            return devicePath.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetStorageDeviceNumber(string devicePath, out StorageDeviceNumber number)
    {
        number = default;
        var handle = CreateFile(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            handle = CreateFile(devicePath, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return false;
        }

        try
        {
            var size = Marshal.SizeOf<StorageDeviceNumber>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, buffer, size, out _, IntPtr.Zero))
                    return false;
                number = Marshal.PtrToStructure<StorageDeviceNumber>(buffer);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, int ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_Eject(
        uint dnDevInst,
        out int pVetoType,
        StringBuilder pszVetoName,
        int ulNameLength,
        int ulFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
