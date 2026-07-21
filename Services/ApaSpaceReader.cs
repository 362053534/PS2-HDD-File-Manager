using System.Buffers.Binary;

namespace PfsExplorerLite.Services;

internal sealed record ApaDiskSpace(long TotalBytes, long FreeBytes);

/// <summary>Reads only the APA header chain to report raw disk capacity and unallocated APA space.</summary>
internal sealed class ApaSpaceReader
{
    private const uint ApaMagic = 0x00415041; // "APA\0", stored little-endian.
    private const int SectorSize = 512;
    private const int HeaderBytes = SectorSize * 2;
    private const ushort ApaTypeFree = 0x0000;
    private const ushort ApaFlagSub = 0x0001;
    private const ulong ApaAllocationUnitSectors = 0x40000; // 128 MiB

    public Task<ApaDiskSpace> ReadAsync(string devicePath, CancellationToken cancellationToken) =>
        Task.Run(() => Read(devicePath, cancellationToken), cancellationToken);

    private static ApaDiskSpace Read(string devicePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            devicePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            HeaderBytes,
            FileOptions.RandomAccess);

        var totalBytes = stream.Length;
        if (totalBytes < HeaderBytes)
            throw new InvalidDataException("磁盘容量不足，无法读取 APA 分区表。");
        var totalSectors = (ulong)totalBytes / SectorSize;
        var allocatableEndSector = totalSectors > 0 ? totalSectors - 1 : 0;

        var buffer = new byte[HeaderBytes];
        var visited = new HashSet<uint>();
        uint sector = 0;
        long freeBytes = 0;
        ulong lastAllocatedEndSector = 0;

        // APA headers form a linked list. The limit prevents a corrupted header from looping forever.
        for (var index = 0; index < 4096 && visited.Add(sector); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked((long)sector * SectorSize);
            if (offset < 0 || offset > totalBytes - HeaderBytes)
                throw new InvalidDataException("APA 分区表包含无效的扇区地址。");

            stream.Position = offset;
            stream.ReadExactly(buffer);

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4));
            if (magic != ApaMagic)
                throw new InvalidDataException("未读取到有效的 APA 分区表。");

            var next = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
            var lengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(68, 4));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(72, 2));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(74, 2));

            lastAllocatedEndSector = Math.Max(lastAllocatedEndSector, (ulong)sector + lengthSectors);

            if (type == ApaTypeFree && (flags & ApaFlagSub) == 0)
            {
                var extentEnd = Math.Min((ulong)sector + lengthSectors, allocatableEndSector);
                var extentSectors = extentEnd > sector ? extentEnd - sector : 0;
                var allocatableSectors = extentSectors / ApaAllocationUnitSectors * ApaAllocationUnitSectors;
                freeBytes = checked(freeBytes + (long)allocatableSectors * SectorSize);
            }

            if (next == 0)
            {
                if (lastAllocatedEndSector > totalSectors)
                    throw new InvalidDataException("APA 分区范围超出磁盘容量。");
                var tailSectors = allocatableEndSector > lastAllocatedEndSector
                    ? allocatableEndSector - lastAllocatedEndSector
                    : 0;
                var allocatableTailSectors = tailSectors / ApaAllocationUnitSectors * ApaAllocationUnitSectors;
                freeBytes = checked(freeBytes + (long)allocatableTailSectors * SectorSize);
                break;
            }
            sector = next;
        }

        return new ApaDiskSpace(totalBytes, Math.Min(freeBytes, totalBytes));
    }
}
