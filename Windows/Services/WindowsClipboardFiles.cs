using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace PfsExplorerLite.Services;

/// <summary>
/// 从资源管理器剪贴板读取文件列表。
/// 不能只用 <see cref="Clipboard.GetFileDropList"/>：它以 autoConvert 取 FileDrop，
/// 失败时会落到只含第一个文件的 FileNameW，表现为只粘贴 1/1。
/// </summary>
internal static class WindowsClipboardFiles
{
    private const string ShellIdListFormat = "Shell IDList Array";
    private const uint SigdnFileSysPath = 0x80058000;

    public static bool ContainsImportableItems()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null)
                return false;
            if (data.GetDataPresent(ShellIdListFormat, autoConvert: false))
                return true;
            if (data.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
                return true;
            return Clipboard.ContainsFileDropList();
        }
        catch
        {
            return false;
        }
    }

    public static string[] GetPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();

        void AddRange(IEnumerable<string>? items)
        {
            if (items is null)
                return;
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item))
                    continue;
                if (!File.Exists(item) && !Directory.Exists(item))
                    continue;
                if (seen.Add(item))
                    paths.Add(item);
            }
        }

        AddRange(TryGetFromShellItemArray());
        if (paths.Count > 1)
            return paths.ToArray();

        AddRange(TryGetFromCida());
        if (paths.Count > 1)
            return paths.ToArray();

        AddRange(TryGetFileDrop(autoConvert: false));
        if (paths.Count > 1)
            return paths.ToArray();

        // 最后才允许 autoConvert：可能把 FileNameW（单文件）当成 FileDrop。
        AddRange(TryGetFileDrop(autoConvert: true));
        return paths.ToArray();
    }

    private static IEnumerable<string>? TryGetFileDrop(bool autoConvert)
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetData(DataFormats.FileDrop, autoConvert) is string[] files)
                return files;
        }
        catch
        {
            /* 延迟渲染失败时忽略 */
        }

        return null;
    }

    private static IEnumerable<string>? TryGetFromCida()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(ShellIdListFormat, autoConvert: false))
                return null;
            var raw = data.GetData(ShellIdListFormat, autoConvert: false);
            var bytes = raw switch
            {
                MemoryStream stream => stream.ToArray(),
                byte[] array => array,
                _ => null
            };
            return bytes is null ? null : ParseCida(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string>? TryGetFromShellItemArray()
    {
        ComIDataObject? ole = null;
        try
        {
            if (OleGetClipboard(out ole) != 0 || ole is null)
                return null;
            var iid = typeof(IShellItemArray).GUID;
            if (SHCreateShellItemArrayFromDataObject(ole, ref iid, out var array) != 0 || array is null)
                return null;

            array.GetCount(out var count);
            if (count == 0)
                return null;

            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                array.GetItemAt(i, out var item);
                if (item is null)
                    continue;
                item.GetDisplayName(SigdnFileSysPath, out var pName);
                if (pName == IntPtr.Zero)
                    continue;
                try
                {
                    var path = Marshal.PtrToStringUni(pName);
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pName);
                }
            }

            return paths;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ole is not null && Marshal.IsComObject(ole))
                Marshal.ReleaseComObject(ole);
        }
    }

    internal static string[] ParseCida(byte[] data)
    {
        if (data.Length < 8)
            return [];

        var cidl = BitConverter.ToUInt32(data, 0);
        if (cidl == 0 || cidl > 1_000_000)
            return [];

        var offsetTableBytes = 4L * (cidl + 1);
        if (4 + offsetTableBytes > data.Length)
            return [];

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var basePtr = handle.AddrOfPinnedObject();
            var folderOff = BitConverter.ToUInt32(data, 4);
            if (folderOff >= data.Length)
                return [];

            var folderPidl = IntPtr.Add(basePtr, (int)folderOff);
            var paths = new List<string>((int)cidl);
            var buffer = new StringBuilder(32768);
            for (var i = 0; i < cidl; i++)
            {
                var off = BitConverter.ToUInt32(data, 4 + 4 * (i + 1));
                if (off >= data.Length)
                    continue;

                var child = IntPtr.Add(basePtr, (int)off);
                var absolute = ILCombine(folderPidl, child);
                if (absolute == IntPtr.Zero)
                    continue;
                try
                {
                    buffer.Clear();
                    if (!SHGetPathFromIDListEx(absolute, buffer, buffer.Capacity, 0)
                        && !SHGetPathFromIDListW(absolute, buffer))
                        continue;
                    var path = buffer.ToString();
                    if (path.Length > 0)
                        paths.Add(path);
                }
                finally
                {
                    ILFree(absolute);
                }
            }

            return paths.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard(out ComIDataObject ppDataObj);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromDataObject(
        ComIDataObject pdo, ref Guid riid, out IShellItemArray ppv);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListEx(IntPtr pidl, StringBuilder pszPath, int cchPath, uint uOpts);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }
}
