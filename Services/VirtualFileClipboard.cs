using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using FormsDataObject = System.Windows.Forms.IDataObject;

namespace PfsExplorerLite.Services;

internal sealed record DeferredPfsFile(string RelativePath, string RemoteDirectory, string Name, bool IsDirectory, long Size = 0);

/// <summary>
/// Windows Shell virtual-file clipboard source. The file list and contents are resolved only when
/// the paste target asks for them, so clicking Copy does not read from the PFS device.
/// </summary>
[ComVisible(true)]
internal sealed class VirtualFileClipboardDataObject : ComDataObject, FormsDataObject
{
    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_TYMED = unchecked((int)0x80040069);
    private const int DataSsameFormatEtc = 0x00040130;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const uint FdAttributes = 0x00000004;
    private const uint FdUnicode = 0x80000000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const int FileDescriptorSize = 592;
    private const int FileDescriptorAnsiSize = 332;
    private const int FileNameOffset = 72;

    private readonly Func<IReadOnlyList<DeferredPfsFile>> _filesFactory;
    private readonly Func<DeferredPfsFile, string> _materializeFile;
    private readonly Action<Exception> _errorReporter;
    private readonly object _gate = new();
    private IReadOnlyList<DeferredPfsFile>? _files;

    private static readonly short FileGroupDescriptorFormat = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
    private static readonly short FileGroupDescriptorAnsiFormat = unchecked((short)RegisterClipboardFormat("FileGroupDescriptor"));
    private static readonly short FileContentsFormat = unchecked((short)RegisterClipboardFormat("FileContents"));
    private static readonly short PreferredDropEffectFormat = unchecked((short)RegisterClipboardFormat("Preferred DropEffect"));

    public VirtualFileClipboardDataObject(
        Func<IReadOnlyList<DeferredPfsFile>> filesFactory,
        Func<DeferredPfsFile, string> materializeFile,
        Action<Exception> errorReporter)
    {
        _filesFactory = filesFactory;
        _materializeFile = materializeFile;
        _errorReporter = errorReporter;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        try
        {
            if (format.cfFormat is var descriptorFormat && (descriptorFormat == FileGroupDescriptorFormat || descriptorFormat == FileGroupDescriptorAnsiFormat))
            {
                if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0)
                    throw new COMException("Clipboard target does not accept file descriptors.", DV_E_TYMED);
                medium = CreateHGlobalMedium(BuildFileGroupDescriptor(GetFiles(), descriptorFormat == FileGroupDescriptorFormat));
                return;
            }

            if (format.cfFormat == FileContentsFormat)
            {
                var files = GetFiles();
                if (format.lindex < 0 || format.lindex >= files.Count)
                    throw new COMException("Invalid virtual file index.", DV_E_FORMATETC);
                if ((format.tymed & TYMED.TYMED_ISTREAM) == 0)
                    throw new COMException("Clipboard target does not accept file streams.", DV_E_TYMED);

                var file = files[format.lindex];
                if (file.IsDirectory)
                {
                    medium = CreateStreamMedium(ManagedFileStream.Empty());
                    return;
                }

                medium = CreateStreamMedium(new ManagedFileStream(() =>
                    new FileStream(_materializeFile(file), FileMode.Open, FileAccess.Read, FileShare.Read)));
                return;
            }

            if (format.cfFormat == PreferredDropEffectFormat)
            {
                if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0)
                    throw new COMException("Clipboard target does not accept drop effects.", DV_E_TYMED);
                // DROPEFFECT_COPY: tell Explorer this is a copy, never a move.
                medium = CreateHGlobalMedium(BitConverter.GetBytes(1u));
                return;
            }

            throw new COMException("Unsupported clipboard format.", DV_E_FORMATETC);
        }
        catch (COMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _errorReporter(ex);
            throw new COMException(ex.Message, unchecked((int)0x80004005));
        }
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Not implemented.", E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == FileGroupDescriptorFormat || format.cfFormat == FileGroupDescriptorAnsiFormat)
            return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_TYMED;
        if (format.cfFormat == FileContentsFormat)
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? S_OK : DV_E_TYMED;
        if (format.cfFormat == PreferredDropEffectFormat)
            return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_TYMED;
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DataSsameFormatEtc;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new COMException("Read-only clipboard source.", E_NOTIMPL);

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("Not implemented.", E_NOTIMPL);
        return new FormatEnumerator(
        [
            new FORMATETC { cfFormat = FileGroupDescriptorFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new FORMATETC { cfFormat = FileGroupDescriptorAnsiFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new FORMATETC { cfFormat = FileContentsFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM },
            new FORMATETC { cfFormat = PreferredDropEffectFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL }
        ]);
    }

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return E_NOTIMPL;
    }

    public void DUnadvise(int connection) => throw new COMException("Not implemented.", E_NOTIMPL);
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return E_NOTIMPL;
    }

    object? FormsDataObject.GetData(string format, bool autoConvert)
    {
        try
        {
            if (format.Equals("FileGroupDescriptorW", StringComparison.Ordinal))
                return new MemoryStream(BuildFileGroupDescriptor(GetFiles(), true), writable: false);
            if (format.Equals("FileGroupDescriptor", StringComparison.Ordinal))
                return new MemoryStream(BuildFileGroupDescriptor(GetFiles(), false), writable: false);
            if (format.Equals("Preferred DropEffect", StringComparison.Ordinal))
                return new MemoryStream(BitConverter.GetBytes(1u), writable: false);
            if (format.Equals("FileContents", StringComparison.Ordinal))
            {
                var file = GetFiles().FirstOrDefault(item => !item.IsDirectory)
                    ?? throw new InvalidOperationException("没有可导出的 PFS 文件。");
                return new DeferredReadStream(() => new FileStream(_materializeFile(file), FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            return null;
        }
        catch (Exception ex)
        {
            _errorReporter(ex);
            return null;
        }
    }

    object? FormsDataObject.GetData(string format) => ((FormsDataObject)this).GetData(format, true);
    object? FormsDataObject.GetData(Type format) => ((FormsDataObject)this).GetData(format.FullName ?? string.Empty, true);
    bool FormsDataObject.GetDataPresent(string format, bool autoConvert) => ((FormsDataObject)this).GetFormats(autoConvert).Contains(format, StringComparer.Ordinal);
    bool FormsDataObject.GetDataPresent(string format) => ((FormsDataObject)this).GetDataPresent(format, true);
    bool FormsDataObject.GetDataPresent(Type format) => ((FormsDataObject)this).GetDataPresent(format.FullName ?? string.Empty, true);
    string[] FormsDataObject.GetFormats(bool autoConvert) => ["FileGroupDescriptorW", "FileGroupDescriptor", "FileContents", "Preferred DropEffect"];
    string[] FormsDataObject.GetFormats() => ((FormsDataObject)this).GetFormats(true);
    void FormsDataObject.SetData(string format, bool autoConvert, object? data) { }
    void FormsDataObject.SetData(string format, object? data) { }
    void FormsDataObject.SetData(Type format, object? data) { }
    void FormsDataObject.SetData(object? data) { }

    private IReadOnlyList<DeferredPfsFile> GetFiles()
    {
        lock (_gate)
            return _files ??= _filesFactory();
    }

    internal static byte[] BuildFileGroupDescriptor(IReadOnlyList<DeferredPfsFile> files, bool unicode)
    {
        var descriptorSize = unicode ? FileDescriptorSize : FileDescriptorAnsiSize;
        var nameCapacity = unicode ? 520 : 260;
        var data = new byte[sizeof(uint) + files.Count * descriptorSize];
        BitConverter.GetBytes((uint)files.Count).CopyTo(data, 0);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var offset = sizeof(uint) + index * descriptorSize;
            // FileGroupDescriptorW 需带 FD_UNICODE，否则部分 Shell 目标会忽略该描述符。
            var flags = FdAttributes | (unicode ? FdUnicode : 0u);
            BitConverter.GetBytes(flags).CopyTo(data, offset);
            BitConverter.GetBytes(file.IsDirectory ? FileAttributeDirectory : FileAttributeNormal).CopyTo(data, offset + 36);
            var name = file.RelativePath.Replace('/', '\\').TrimStart('\\');
            var fileName = name[..Math.Min(name.Length, 259)] + "\0";
            var nameBytes = (unicode ? Encoding.Unicode : Encoding.Default).GetBytes(fileName);
            Array.Copy(nameBytes, 0, data, offset + FileNameOffset, Math.Min(nameBytes.Length, nameCapacity));
        }
        return data;
    }

    private static STGMEDIUM CreateHGlobalMedium(byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) { GlobalFree(handle); throw new OutOfMemoryException(); }
        try { Marshal.Copy(data, 0, pointer, data.Length); }
        finally { GlobalUnlock(handle); }
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = IntPtr.Zero };
    }

    private static STGMEDIUM CreateStreamMedium(IStream stream) => new()
    {
        tymed = TYMED.TYMED_ISTREAM,
        unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)),
        pUnkForRelease = IntPtr.Zero
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}

internal static class VirtualFileClipboard
{
    private static VirtualFileClipboardDataObject? _owner;
    private static DataObject? _standardOwner;

    public static void Set(VirtualFileClipboardDataObject dataObject)
    {
        // 与单文件路径一致：走 WinForms 重试逻辑；copy:false 保留延迟渲染，粘贴前不落地文件。
        Clipboard.SetDataObject(dataObject, copy: false);
        _owner = dataObject;
        _standardOwner = null;
    }

    public static DataObject CreateSingleFileDataObject(DeferredPfsFile file, Func<string> materializeFile)
    {
        var files = new[] { file };
        var dataObject = new DataObject();
        dataObject.SetData("FileGroupDescriptorW", autoConvert: false, new MemoryStream(VirtualFileClipboardDataObject.BuildFileGroupDescriptor(files, unicode: true), writable: false));
        dataObject.SetData("FileGroupDescriptor", autoConvert: false, new MemoryStream(VirtualFileClipboardDataObject.BuildFileGroupDescriptor(files, unicode: false), writable: false));
        dataObject.SetData(
            "FileContents",
            autoConvert: false,
            new DeferredReadStream(() => new FileStream(materializeFile(), FileMode.Open, FileAccess.Read, FileShare.Read)));
        dataObject.SetData("Preferred DropEffect", autoConvert: false, new MemoryStream(BitConverter.GetBytes(1u), writable: false));
        return dataObject;
    }

    public static void SetOwnedDataObject(DataObject dataObject)
    {
        Clipboard.SetDataObject(dataObject, copy: false);
        _standardOwner = dataObject;
        _owner = null;
    }

    public static void SetSingleFile(DeferredPfsFile file, Func<string> materializeFile) =>
        SetOwnedDataObject(CreateSingleFileDataObject(file, materializeFile));
}

internal sealed class DeferredReadStream : Stream
{
    private readonly Func<Stream> _factory;
    private Task<Stream>? _factoryTask;
    private Stream? _inner;
    public DeferredReadStream(Func<Stream> factory) => _factory = factory;
    private Stream Inner => _inner ??= WaitForFactory();

    private Stream WaitForFactory()
    {
        var task = _factoryTask ??= Task.Run(_factory);
        // OLE 有时会在主 STA 线程读取虚拟流。同步等待期间继续派发窗口消息，
        // 让主窗体能重绘 PFS 导出进度，而不是整窗显示“未响应”。
        if (Application.MessageLoop)
        {
            while (!task.IsCompleted)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }
        return task.GetAwaiter().GetResult();
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => Inner.Length;
    public override long Position { get => Inner.Position; set => Inner.Position = value; }
    public override void Flush() => Inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner?.Dispose();
        base.Dispose(disposing);
    }
}

[ComVisible(true)]
internal sealed class ManagedFileStream : IStream
{
    private readonly Stream _stream;
    public ManagedFileStream(string path) => _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private ManagedFileStream(Stream stream) => _stream = stream;
    internal ManagedFileStream(Func<Stream> factory) => _stream = new DeferredReadStream(factory);
    public static ManagedFileStream Empty() => new(new MemoryStream());

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        var read = _stream.Read(pv, 0, cb);
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
    }
    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
    public void Seek(long move, int origin, IntPtr newPosition)
    {
        var position = _stream.Seek(move, (SeekOrigin)origin);
        if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
    }
    public void SetSize(long libNewSize) => throw new NotSupportedException();
    public void CopyTo(IStream destination, long cb, IntPtr read, IntPtr written)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (total < cb)
        {
            var count = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, cb - total));
            if (count == 0) break;
            destination.Write(buffer, count, IntPtr.Zero);
            total += count;
        }
        if (read != IntPtr.Zero) Marshal.WriteInt64(read, total);
        if (written != IntPtr.Zero) Marshal.WriteInt64(written, total);
    }
    public void Commit(int flags) { }
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long offset, long cb, int lockType) => throw new NotSupportedException();
    public void UnlockRegion(long offset, long cb, int lockType) => throw new NotSupportedException();
    public void Stat(out STATSTG stat, int statFlag) => stat = new STATSTG { type = 2, cbSize = _stream.Length };
    public void Clone(out IStream stream)
    {
        if (_stream is not FileStream fileStream) throw new NotSupportedException();
        stream = new ManagedFileStream(fileStream.Name);
    }
}

[ComVisible(true)]
internal sealed class FormatEnumerator : IEnumFORMATETC
{
    private readonly FORMATETC[] _formats;
    private int _index;
    public FormatEnumerator(FORMATETC[] formats) => _formats = formats;
    public int Next(int count, FORMATETC[] formats, int[] fetched)
    {
        // 资源管理器常以 Next(1, &fmt, NULL) 探测；pceltFetched 为 null 时不得解引用。
        if (formats is null) return unchecked((int)0x80004003);
        if (count != 1 && fetched is null) return unchecked((int)0x80004003);

        var copied = 0;
        while (copied < count && _index < _formats.Length) formats[copied++] = _formats[_index++];
        if (fetched is { Length: > 0 }) fetched[0] = copied;
        return copied == count ? 0 : 1;
    }
    public int Skip(int count) { _index = Math.Min(_formats.Length, _index + count); return _index < _formats.Length ? 0 : 1; }
    public int Reset() { _index = 0; return 0; }
    public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(_formats) { _index = _index };
}
