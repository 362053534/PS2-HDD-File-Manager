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
        // 先关套接字，打断可能卡在读写上的持锁操作，再抢锁做完整本地清理
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }

        if (!_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            _mountedPartition = null;
            _features = 0;
            _stream = null;
            _client = null;
            _host = null;
            return;
        }

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

    /// <summary>Put：本机 listen 45062 → PUT_BEGIN → accept → 原始字节流 → PUT_END（T1）。</summary>
    public Task PutFileAsync(
        string remoteDirectory,
        string remoteFileName,
        string localFilePath,
        bool overwrite,
        CancellationToken cancellationToken,
        Func<long, Task>? progress = null) =>
        WithLockAsync(async ct =>
        {
            EnsureMounted();
            EnsureDataChannel();
            localFilePath = Path.GetFullPath(localFilePath);
            var totalBytes = (ulong)new FileInfo(localFilePath).Length;

            using var listener = CreateDataListener();
            listener.Start();
            var acceptTask = AcceptDataAsync(listener, ct);
            var accepted = false;
            long sent = 0;
            try
            {
                // 先挂起 accept，再发 PUT_BEGIN，缩小 PS2 回连与 PC accept 之间的空窗
                using var ms = new MemoryStream();
                using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    PfsNetCodec.WriteString(writer, NormalizePath(remoteDirectory));
                    PfsNetCodec.WriteString(writer, remoteFileName);
                    writer.Write(totalBytes);
                    writer.Write(overwrite ? 1u : 0u);
                }

                await SendSimpleAsync(PfsNetProtocol.CmdPutBegin, ms.ToArray(), ct).ConfigureAwait(false);

                using var dataClient = await acceptTask.ConfigureAwait(false);
                accepted = true;
                // 取消时主动关闭数据口，避免 WriteAsync 在部分平台上迟迟不响应 CT。
                using var cancelClose = ct.Register(() => { try { dataClient.Client.Close(); } catch { } });
                try
                {
                    dataClient.SendBufferSize = 512 * 1024;
                    dataClient.ReceiveBufferSize = 512 * 1024;
                    dataClient.NoDelay = true;
                }
                catch { /* 平台可能不支持，忽略 */ }
                await using var dataStream = dataClient.GetStream();
                await using var file = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 512 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[512 * 1024];
                while (sent < (long)totalBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(buffer.Length, (long)totalBytes - sent);
                    var n = await file.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (n <= 0)
                        throw new IOException("本地文件读取提前结束。");
                    await dataStream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    sent += n;
                    if (progress is not null)
                        await progress(sent).ConfigureAwait(false);
                }

                await dataStream.FlushAsync(ct).ConfigureAwait(false);
                dataClient.Close();
                await SendSimpleAsync(PfsNetProtocol.CmdPutEnd, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 取消且未发完：数据口已关；PS2 卡在 PUT_BEGIN 时无法应答 ABORT，勿同步空等。
                if (!(ct.IsCancellationRequested && sent < (long)totalBytes))
                {
                    try
                    {
                        using var abortCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await SendSimpleAsync(PfsNetProtocol.CmdPutAbort, ReadOnlyMemory<byte>.Empty, abortCts.Token);
                    }
                    catch
                    {
                        try { DisconnectUnlocked(); } catch { /* 尽力恢复 */ }
                    }
                }
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException("传输已取消。", ex, ct);
                throw;
            }
            finally
            {
                try { listener.Stop(); } catch { }
                if (!accepted)
                {
                    try { (await acceptTask).Dispose(); }
                    catch { /* BEGIN 失败或 listener 已停 */ }
                }
            }
        }, cancellationToken);

    /// <summary>Get：本机 listen 45062 → GET_BEGIN → accept → 收原始字节 → GET_END（落盘）。</summary>
    public Task GetFileAsync(
        string remoteDirectory,
        string remoteFileName,
        string localFilePath,
        CancellationToken cancellationToken,
        Func<long, Task>? progress = null) =>
        WithLockAsync(async ct =>
        {
            EnsureMounted();
            EnsureDataChannel();
            localFilePath = Path.GetFullPath(localFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            try
            {
                await using var file = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 512 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await ReceiveGetToStreamUnlockedAsync(remoteDirectory, remoteFileName, file, ct, progress);
            }
            catch
            {
                try { if (File.Exists(localFilePath)) File.Delete(localFilePath); } catch { }
                throw;
            }
        }, cancellationToken);

    /// <summary>Get：同 GetFileAsync，但写入任意 Stream（剪贴板 IStream 直通，无临时落盘）。</summary>
    public Task GetFileToStreamAsync(
        string remoteDirectory,
        string remoteFileName,
        Stream destination,
        CancellationToken cancellationToken,
        Func<long, Task>? progress = null) =>
        WithLockAsync(
            ct => ReceiveGetToStreamUnlockedAsync(remoteDirectory, remoteFileName, destination, ct, progress),
            cancellationToken);

    private async Task ReceiveGetToStreamUnlockedAsync(
        string remoteDirectory,
        string remoteFileName,
        Stream destination,
        CancellationToken ct,
        Func<long, Task>? progress)
    {
        using var listener = CreateDataListener();
        listener.Start();
        // 与 Put 对称：先挂起 accept，再发 GET_BEGIN；PS2 回 OK 后再回连。
        var acceptTask = AcceptDataAsync(listener, ct);
        var accepted = false;
        ulong totalBytes = 0;
        long received = 0;
        string peerInfo = "(未接受)";
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                PfsNetCodec.WriteString(writer, NormalizePath(remoteDirectory));
                PfsNetCodec.WriteString(writer, remoteFileName);
                writer.Write(0UL); // offset
                writer.Write(0UL); // length=0 → 整文件
            }

            var reply = await SendAsync(PfsNetProtocol.CmdGetBegin, ms.ToArray(), ct);
            if (reply.Length < 8)
                throw new InvalidOperationException("GET_BEGIN 响应缺少 total_bytes。");
            totalBytes = BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(0, 8));

            using var dataClient = await acceptTask;
            accepted = true;
            // 取消时主动关闭数据口，避免 ReadAsync 在部分平台上迟迟不响应 CT。
            using var cancelClose = ct.Register(() => { try { dataClient.Client.Close(); } catch { } });
            try
            {
                dataClient.ReceiveBufferSize = 512 * 1024;
                dataClient.SendBufferSize = 512 * 1024;
                dataClient.NoDelay = true;
            }
            catch { /* 平台可能不支持，忽略 */ }
            peerInfo = dataClient.Client.RemoteEndPoint?.ToString() ?? "(未知)";

            await using var dataStream = dataClient.GetStream();
            var buffer = new byte[512 * 1024];
            while (received < (long)totalBytes)
            {
                ct.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, (long)totalBytes - received);
                var n = await dataStream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    // 取消关闭套接字时 Read 会返回 0，优先按取消处理。
                    ct.ThrowIfCancellationRequested();
                    throw new IOException(
                        $"数据通道在文件传完前断开（已收 {received}/{totalBytes}，对端 {peerInfo}）。");
                }
                await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (progress is not null)
                    await progress(received).ConfigureAwait(false);
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            // 先 GET_END 再关数据口，让 PS2 侧有机会在 END 时再 close（避免过早 RST）
            await SendSimpleAsync(PfsNetProtocol.CmdGetEnd, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            dataClient.Close();
        }
        catch (Exception ex)
        {
            // 取消且未收满：数据口已关；PS2 卡在 GET_BEGIN 时无法应答 ABORT，勿同步空等至 30s。
            if (!(ct.IsCancellationRequested && received < (long)totalBytes))
            {
                try
                {
                    using var abortCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendSimpleAsync(PfsNetProtocol.CmdGetAbort, ReadOnlyMemory<byte>.Empty, abortCts.Token);
                }
                catch
                {
                    try { DisconnectUnlocked(); } catch { /* 尽力恢复 */ }
                }
            }
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("传输已取消。", ex, ct);
            throw;
        }
        finally
        {
            try { listener.Stop(); } catch { }
            if (!accepted)
            {
                try { (await acceptTask).Dispose(); }
                catch { /* BEGIN 失败或 listener 已停 */ }
            }
        }
    }

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

    private void EnsureDataChannel()
    {
        if ((_features & PfsNetProtocol.FeatDataChannel) == 0)
            throw new InvalidOperationException("PS2 未声明 FEAT_DATA_CHANNEL，无法 Put/Get。请更新并运行新版 PfsNetServer.elf。");
    }

    private static TcpListener CreateDataListener()
    {
        var listener = new TcpListener(IPAddress.Any, PfsNetProtocol.DataPort);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        return listener;
    }

    private static async Task<TcpClient> AcceptDataAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await listener.AcceptTcpClientAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待 PS2 回连数据口 45062 超时。请检查 PC 防火墙是否放行入站 TCP 45062。");
        }
    }

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
