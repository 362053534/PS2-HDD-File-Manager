using System.Buffers.Binary;
using System.Text;

namespace PfsExplorerLite.Services;

internal sealed record ApaPartitionTable(long TotalBytes, long FreeBytes, IReadOnlyList<ApaPartitionInfo> Partitions);

internal sealed record ApaPartitionInfo(
    string Name,
    uint StartSector,
    uint LengthSectors,
    ushort Type,
    ushort Flags,
    uint MainSector,
    uint Number,
    IReadOnlyList<ApaSubPartitionInfo> SubPartitions)
{
    public bool IsSubPartition => (Flags & 0x0001) != 0;
    public long TotalBytes => ((long)LengthSectors + SubPartitions.Sum(part => (long)part.LengthSectors)) * ApaPartitionTableReader.SectorSize;
}

internal sealed record ApaSubPartitionInfo(uint StartSector, uint LengthSectors);

/// <summary>按 LBA 提供原始扇区；网络与本地共用同一套 APA 校验逻辑。</summary>
internal interface IApaSectorSource
{
    long TotalBytes { get; }
    Task ReadSectorsAsync(ulong lba, int count, Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>Read-only APA layout reader used to preflight and verify destructive partition operations.</summary>
internal sealed class ApaPartitionTableReader
{
    internal const int SectorSize = 512;
    private const int HeaderBytes = SectorSize * 2;
    private const uint ApaMagic = 0x00415041;
    private const ushort ApaTypeFree = 0x0000;
    private const ulong ApaAllocationUnitSectors = 0x40000; // 128 MiB

    public Task<ApaPartitionTable> ReadAsync(string devicePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var stream = new FileStream(
                devicePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                HeaderBytes,
                FileOptions.RandomAccess);
            return Read(new StreamApaSectorSource(stream, ResolveStreamTotalBytes(stream, devicePath)), cancellationToken);
        }, cancellationToken);

    internal static long ResolveStreamTotalBytes(FileStream stream, string devicePath)
    {
        try
        {
            var len = stream.Length;
            if (len > 0)
                return len;
        }
        catch
        {
            // 块设备在某些系统上不支持 fstat 长度
        }

        try
        {
            var saved = stream.Position;
            var end = stream.Seek(0, SeekOrigin.End);
            stream.Seek(saved, SeekOrigin.Begin);
            if (end > 0)
                return end;
        }
        catch
        {
            // 忽略并走平台工具兜底
        }

        if (OperatingSystem.IsMacOS() && devicePath.StartsWith("/dev/", StringComparison.Ordinal) && File.Exists("/usr/sbin/diskutil"))
        {
            try
            {
                var info = ProcessRunner.RunAsync("/usr/sbin/diskutil", $"info \"{devicePath}\"").GetAwaiter().GetResult();
                var match = System.Text.RegularExpressions.Regex.Match(info.StandardOutput, @"\((\d+)\s+Bytes\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && long.TryParse(match.Groups[1].Value, out var bytes) && bytes > 0)
                    return bytes;
            }
            catch
            {
                // 忽略
            }
        }

        return 0;
    }

    public Task<ApaPartitionTable> ReadAsync(IApaSectorSource source, CancellationToken cancellationToken) =>
        Task.Run(() => Read(source, cancellationToken), cancellationToken);

    private static ApaPartitionTable Read(IApaSectorSource source, CancellationToken cancellationToken)
    {
        var totalBytes = source.TotalBytes;
        if (totalBytes < HeaderBytes)
            throw new InvalidDataException("磁盘容量不足，无法读取 APA 分区表。");
        var totalSectors = (ulong)totalBytes / SectorSize;
        // PS2 APA 以最后 LBA 作为上限，并要求分区结束位置不越过它；
        // 因此刚好延伸到文件末尾的最后一个 128 MiB 单元不可分配。
        var allocatableEndSector = totalSectors > 0 ? totalSectors - 1 : 0;

        var buffer = new byte[HeaderBytes];
        var visited = new HashSet<uint>();
        var partitions = new List<ApaPartitionInfo>();
        long freeBytes = 0;
        ulong lastAllocatedEndSector = 0;
        uint sector = 0;

        for (var index = 0; index < 4096 && visited.Add(sector); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked((long)sector * SectorSize);
            if (offset < 0 || offset > totalBytes - HeaderBytes)
                throw new InvalidDataException("APA 分区表包含无效的扇区地址。");

            source.ReadSectorsAsync(sector, 2, buffer, cancellationToken).GetAwaiter().GetResult();

            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4)) != ApaMagic)
                throw new InvalidDataException("未读取到有效的 APA 分区表。");
            if (!HasValidChecksum(buffer))
                throw new InvalidDataException("APA 分区表校验和无效，已拒绝写入。");

            var next = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
            var name = ReadAsciiZ(buffer.AsSpan(16, 32));
            var start = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64, 4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(68, 4));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(72, 2));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(74, 2));
            var subCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(76, 4));
            var main = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(88, 4));
            var number = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(92, 4));

            if (subCount > 64)
                throw new InvalidDataException("APA 分区表包含无效的子分区数量。");
            if (start != sector || length == 0)
                throw new InvalidDataException("APA 分区表包含无效的分区范围。");

            var subPartitions = new List<ApaSubPartitionInfo>((int)subCount);
            for (var subIndex = 0; subIndex < subCount; subIndex++)
            {
                var subOffset = 512 + subIndex * 8;
                var subStart = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(subOffset, 4));
                var subLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(subOffset + 4, 4));
                if (subStart == 0 || subLength == 0)
                    throw new InvalidDataException("APA 分区表包含无效的子分区范围。");
                subPartitions.Add(new ApaSubPartitionInfo(subStart, subLength));
            }

            var partition = new ApaPartitionInfo(name, start, length, type, flags, main, number, subPartitions);
            partitions.Add(partition);
            lastAllocatedEndSector = Math.Max(lastAllocatedEndSector, (ulong)start + length);
            if (type == ApaTypeFree && !partition.IsSubPartition)
            {
                var extentEnd = Math.Min((ulong)start + length, allocatableEndSector);
                var extentSectors = extentEnd > start ? extentEnd - start : 0;
                var allocatableSectors = extentSectors / ApaAllocationUnitSectors * ApaAllocationUnitSectors;
                freeBytes = checked(freeBytes + (long)allocatableSectors * SectorSize);
            }

            if (next == 0)
            {
                if (lastAllocatedEndSector > totalSectors)
                    throw new InvalidDataException("APA 分区范围超出磁盘容量，已拒绝写入。");
                // APA 链经常不为磁盘末尾的未分配区域保留一个 free header。
                var tailSectors = allocatableEndSector > lastAllocatedEndSector
                    ? allocatableEndSector - lastAllocatedEndSector
                    : 0;
                var allocatableTailSectors = tailSectors / ApaAllocationUnitSectors * ApaAllocationUnitSectors;
                freeBytes = checked(freeBytes + (long)allocatableTailSectors * SectorSize);
                return new ApaPartitionTable(totalBytes, Math.Min(freeBytes, totalBytes), partitions);
            }
            sector = next;
        }

        throw new InvalidDataException("APA 分区表链无效或过长，已拒绝写入。");
    }

    private static string ReadAsciiZ(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? value : value[..length]);
    }

    private static bool HasValidChecksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (var offset = 4; offset < HeaderBytes; offset += 4)
            sum += BinaryPrimitives.ReadUInt32LittleEndian(header[offset..]);
        return sum == BinaryPrimitives.ReadUInt32LittleEndian(header);
    }

    private sealed class StreamApaSectorSource(FileStream stream, long totalBytes) : IApaSectorSource
    {
        public long TotalBytes => totalBytes;

        public Task ReadSectorsAsync(ulong lba, int count, Memory<byte> destination, CancellationToken cancellationToken)
        {
            var bytes = checked(count * SectorSize);
            if (destination.Length < bytes)
                throw new ArgumentException("目标缓冲区过小。", nameof(destination));
            stream.Position = checked((long)lba * SectorSize);
            stream.ReadExactly(destination.Span[..bytes]);
            return Task.CompletedTask;
        }
    }
}

