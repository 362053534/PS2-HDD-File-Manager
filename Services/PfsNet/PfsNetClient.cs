namespace PfsExplorerLite.Services.PfsNet;

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>PfsNet 控制通道客户端（P1 浏览 + 后续命令入口）。</summary>
internal sealed class PfsNetClient : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _host;
    private string? _mountedPartition;
    private uint _features;

    public bool IsConnected => _client is { Connected: true };
    public string? Host => _host;
    public string? MountedPartition => _mountedPartition;
    public uint Features => _features;
    public bool SupportsApaMutate => (_features & PfsNetProtocol.FeatApaMutate) != 0;

    public async Task ConnectAsync(string ipAddress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DisconnectUnlocked();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Parse(ipAddress.Trim()), PfsNetProtocol.CommandPort, cancellationToken);
                _client = client;
                _stream = client.GetStream();
                _host = ipAddress.Trim();
                await HelloUnlockedAsync(cancellationToken);
            }
            catch
            {
                client.Dispose();
                _client = null;
                _stream = null;
                _host = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Disconnect()
    {
        _gate.Wait();
        try { DisconnectUnlocked(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }

    public Task PingAsync(CancellationToken cancellationToken) =>
        WithLockAsync(ct => SendSimpleAsync(PfsNetProtocol.CmdPing, ReadOnlyMemory<byte>.Empty, ct), cancellationToken);

    public async Task MountAsync(string partition, CancellationToken cancellationToken)
    {
        await WithLockAsync(async ct =>
        {
            EnsureConnected();
            if (_mountedPartition is not null
                && !string.Equals(_mountedPartition, partition, StringComparison.Ordinal))
            {
                await SendSimpleAsync(PfsNetProtocol.CmdUmount, ReadOnlyMemory<byte>.Empty, ct);
                _mountedPartition = null;
            }

            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                PfsNetCodec.WriteString(writer, partition);
            await SendSimpleAsync(PfsNetProtocol.CmdMount, ms.ToArray(), ct);
            _mountedPartition = partition;
        }, cancellationToken);
    }

    public async Task UmountAsync(CancellationToken cancellationToken)
    {
        await WithLockAsync(async ct =>
        {
            if (!IsConnected || _mountedPartition is null)
            {
                _mountedPartition = null;
                return;
            }

            await SendSimpleAsync(PfsNetProtocol.CmdUmount, ReadOnlyMemory<byte>.Empty, ct);
            _mountedPartition = null;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PfsNetPartitionInfo>> ListPartitionsAsync(bool includeSystem, CancellationToken cancellationToken) =>
        WithLockAsync(async ct =>
        {
            EnsureConnected();
            var req = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(req, includeSystem ? 1u : 0u);
            var payload = await SendAsync(PfsNetProtocol.CmdListPartitions, req, ct);
            using var reader = new BinaryReader(new MemoryStream(payload));
            var count = reader.ReadUInt32();
            var list = new List<PfsNetPartitionInfo>((int)count);
            for (var i = 0; i < count; i++)
            {
                var name = PfsNetCodec.ReadString(reader);
                var flags = reader.ReadByte();
                var total = reader.ReadUInt64();
                list.Add(new PfsNetPartitionInfo(
                    name,
                    IsSystem: (flags & 1) != 0,
                    IsPfs: (flags & 2) != 0,
                    TotalBytes: total));
            }

            return (IReadOnlyList<PfsNetPartitionInfo>)list;
        }, cancellationToken);

    public Task<IReadOnlyList<PfsNetDirEntry>> ListDirAsync(string path, CancellationToken cancellationToken) =>
        WithLockAsync(async ct =>
        {
            EnsureConnected();
            if (_mountedPartition is null)
                throw new InvalidOperationException("尚未挂载 PFS 分区。");

            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                PfsNetCodec.WriteString(writer, NormalizePath(path));
            var payload = await SendAsync(PfsNetProtocol.CmdListDir, ms.ToArray(), ct);
            using var reader = new BinaryReader(new MemoryStream(payload));
            var count = reader.ReadUInt32();
            var list = new List<PfsNetDirEntry>((int)count);
            for (var i = 0; i < count; i++)
            {
                var name = PfsNetCodec.ReadString(reader);
                var isDir = reader.ReadByte() != 0;
                var size = reader.ReadUInt64();
                var details = PfsNetCodec.ReadString(reader);
                list.Add(new PfsNetDirEntry(name, isDir, size, details));
            }

            return (IReadOnlyList<PfsNetDirEntry>)list;
        }, cancellationToken);

    public Task<PfsNetSpaceInfo> GetApaFreeAsync(CancellationToken cancellationToken) =>
        WithLockAsync(async ct =>
        {
            var payload = await SendAsync(PfsNetProtocol.CmdGetApaFree, ReadOnlyMemory<byte>.Empty, ct);
            if (payload.Length < 16)
                throw new InvalidOperationException("GET_APA_FREE 载荷过短。");
            return new PfsNetSpaceInfo(
                BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8, 8)),
                0);
        }, cancellationToken);

    public Task<PfsNetSpaceInfo> GetPfsFreeAsync(CancellationToken cancellationToken) =>
        WithLockAsync(async ct =>
        {
            var payload = await SendAsync(PfsNetProtocol.CmdGetPfsFree, ReadOnlyMemory<byte>.Empty, ct);
            if (payload.Length < 20)
                throw new InvalidOperationException("GET_PFS_FREE 载荷过短。");
            return new PfsNetSpaceInfo(
                BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16, 4)));
        }, cancellationToken);

    public Task MkdirAsync(string parentPath, string name, CancellationToken cancellationToken) =>
        PathNameCommandAsync(PfsNetProtocol.CmdMkdir, parentPath, name, cancellationToken);

    public Task RmdirAsync(string parentPath, string name, CancellationToken cancellationToken) =>
        PathNameCommandAsync(PfsNetProtocol.CmdRmdir, parentPath, name, cancellationToken);

    public Task RemoveAsync(string parentPath, string name, CancellationToken cancellationToken) =>
        PathNameCommandAsync(PfsNetProtocol.CmdRemove, parentPath, name, cancellationToken);

    public Task RenameAsync(string parentPath, string oldName, string newName, CancellationToken cancellationToken) =>
        WithLockAsync(async ct =>
        {
            EnsureMounted();
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                PfsNetCodec.WriteString(writer, NormalizePath(parentPath));
                PfsNetCodec.WriteString(writer, oldName);
                PfsNetCodec.WriteString(writer, newName);
            }

            await SendSimpleAsync(PfsNetProtocol.CmdRename, ms.ToArray(), ct);
        }, cancellationToken);

    private Task PathNameCommandAsync(uint command, string parentPath, string name, CancellationToken cancellationToken) =>
        WithLockAsync(async ct =>
        {
            EnsureMounted();
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                PfsNetCodec.WriteString(writer, NormalizePath(parentPath));
                PfsNetCodec.WriteString(writer, name);
            }

            await SendSimpleAsync(command, ms.ToArray(), ct);
        }, cancellationToken);

    private async Task HelloUnlockedAsync(CancellationToken cancellationToken)
    {
        var req = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0, 4), PfsNetProtocol.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4, 4), PfsNetProtocol.Version);
        var payload = await SendAsync(PfsNetProtocol.CmdHello, req, cancellationToken);
        if (payload.Length < 12)
            throw new InvalidOperationException("握手响应过短。");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
        _features = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
        if (magic != PfsNetProtocol.Magic)
            throw new InvalidOperationException("握手魔数不匹配（可能误连了 HDLGameInstaller）。");
        if (version != PfsNetProtocol.Version)
            throw new InvalidOperationException($"协议版本不匹配：服务器 0x{version:X}，客户端 0x{PfsNetProtocol.Version:X}。");
    }

    private async Task SendSimpleAsync(uint command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        _ = await SendAsync(command, payload, cancellationToken);
    }

    private async Task<byte[]> SendAsync(uint command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (payload.Length > PfsNetProtocol.MaxControlPayload)
            throw new InvalidOperationException("控制载荷过大。");

        var header = new byte[PfsNetProtocol.HeaderSize];
        PfsNetCodec.WriteHeader(header, command, (uint)payload.Length);
        await PfsNetCodec.WriteExactAsync(_stream!, header, cancellationToken);
        if (!payload.IsEmpty)
            await PfsNetCodec.WriteExactAsync(_stream!, payload, cancellationToken);

        var respHeader = new byte[PfsNetProtocol.HeaderSize];
        await PfsNetCodec.ReadExactAsync(_stream!, respHeader, cancellationToken);
        PfsNetCodec.ReadHeader(respHeader, out _, out var respLen, out var result);
        if (respLen > PfsNetProtocol.MaxControlPayload)
            throw new InvalidOperationException("响应载荷过大。");

        var body = respLen == 0 ? Array.Empty<byte>() : new byte[respLen];
        if (respLen > 0)
            await PfsNetCodec.ReadExactAsync(_stream!, body, cancellationToken);

        if (result != PfsNetProtocol.Ok)
            throw new InvalidOperationException($"PfsNet 命令 0x{command:X4} 失败（result={result}）。");
        return body;
    }

    private async Task<T> WithLockAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await action(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task WithLockAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await action(cancellationToken); }
        finally { _gate.Release(); }
    }

    private void EnsureConnected()
    {
        if (_stream is null || _client is not { Connected: true })
            throw new InvalidOperationException("尚未连接 PfsNet。请先填写 PS2 IP 并连接。");
    }

    private void EnsureMounted()
    {
        EnsureConnected();
        if (_mountedPartition is null)
            throw new InvalidOperationException("尚未挂载 PFS 分区。");
    }

    private void DisconnectUnlocked()
    {
        _mountedPartition = null;
        _features = 0;
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
        _host = null;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
            path = "/" + path;
        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}
