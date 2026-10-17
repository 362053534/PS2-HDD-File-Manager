using System.Buffers.Binary;
using System.Numerics;

namespace PfsExplorerLite.Services;

internal sealed record PfsSpace(long TotalBytes, long FreeBytes, uint ZoneSizeBytes);

/// <summary>Read-only PFS bitmap scanner. Equivalent to the PFS driver's PDIOC_ZONEFREE calculation.</summary>
internal sealed class PfsSpaceReader
{
    private const uint PfsSuperMagic = 0x50465300; // "PFS\0", stored little-endian.
    private const int SectorSize = ApaPartitionTableReader.SectorSize;
    private const uint SuperSector = 8192;
    private const uint BitmapZonesPerChunk = 8192;
    private const uint MetadataBlockSectors = 2; // PFS metadata blocks are 1024 bytes.
    private const uint MainBitmapBlockOffset = 0x2000 >> 1;

    public Task<PfsSpace> ReadAsync(string devicePath, ApaPartitionInfo partition, CancellationToken cancellationToken) =>
        Task.Run(() => Read(devicePath, partition, cancellationToken), cancellationToken);

    private static PfsSpace Read(string devicePath, ApaPartitionInfo partition, CancellationToken cancellationToken)
    {
        if (partition.IsSubPartition || partition.Type != 0x0100)
            throw new InvalidDataException("选择的 APA 分区不是 PFS 主分区。");

        using var stream = new FileStream(
            devicePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024,
            FileOptions.RandomAccess);

        var diskTotalBytes = ApaPartitionTableReader.ResolveStreamTotalBytes(stream, devicePath);
        var super = ReadAt(stream, diskTotalBytes, ToByteOffset(partition.StartSector + SuperSector), 512);
        if (BinaryPrimitives.ReadUInt32LittleEndian(super.AsSpan(0, 4)) != PfsSuperMagic)
            throw new InvalidDataException("未读取到有效的 PFS 超级块。");

        var zoneSize = BinaryPrimitives.ReadUInt32LittleEndian(super.AsSpan(16, 4));
        var numSubs = BinaryPrimitives.ReadUInt32LittleEndian(super.AsSpan(20, 4));
        if (zoneSize < 2048 || !IsPowerOfTwo(zoneSize) || zoneSize % SectorSize != 0 || zoneSize % 1024 != 0)
            throw new InvalidDataException("PFS 分区使用了不支持的区块大小。");
        if (numSubs != partition.SubPartitions.Count)
            throw new InvalidDataException("PFS 与 APA 的子分区数量不一致，已拒绝显示不可靠的空间数据。");

        var sectorScale = BitOperations.Log2(zoneSize / SectorSize);
        var inodeScale = BitOperations.Log2(zoneSize / 1024);
        var partitionSectors = new List<uint> { partition.LengthSectors };
        partitionSectors.AddRange(partition.SubPartitions.Select(sub => sub.LengthSectors));
        var partitionStarts = new List<uint> { partition.StartSector };
        partitionStarts.AddRange(partition.SubPartitions.Select(sub => sub.StartSector));

        long freeZones = 0;
        for (var sub = 0; sub < partitionSectors.Count; sub++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zones = partitionSectors[sub] >> sectorScale;
            if ((zones << sectorScale) != partitionSectors[sub])
                throw new InvalidDataException("APA 分区容量不是 PFS 区块大小的整数倍。");

            var chunkCount = (zones + BitmapZonesPerChunk - 1) / BitmapZonesPerChunk;
            for (uint chunk = 0; chunk < chunkCount; chunk++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zonesInChunk = Math.Min(BitmapZonesPerChunk, zones - chunk * BitmapZonesPerChunk);
                var bytesInChunk = checked((int)(zonesInChunk / 8));
                if (bytesInChunk == 0 || zonesInChunk % 8 != 0)
                    throw new InvalidDataException("PFS 位图长度无效。");

                var bitmapBlock = checked((1u << inodeScale) + chunk + (sub == 0 ? MainBitmapBlockOffset : 0));
                var bitmapSector = checked(partitionStarts[sub] + bitmapBlock * MetadataBlockSectors);
                var bitmap = ReadAt(stream, diskTotalBytes, ToByteOffset(bitmapSector), 1024);
                for (var index = 0; index < bytesInChunk; index++)
                    freeZones += 8 - BitOperations.PopCount((uint)bitmap[index]);
            }
        }

        var freeBytes = checked(freeZones * (long)zoneSize);
        return new PfsSpace(partition.TotalBytes, Math.Min(freeBytes, partition.TotalBytes), zoneSize);
    }

    private static byte[] ReadAt(FileStream stream, long totalBytes, long offset, int length)
    {
        if (offset < 0 || offset > totalBytes - length)
            throw new InvalidDataException("PFS 位图位置超出磁盘范围。");
        var data = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(data);
        return data;
    }

    private static long ToByteOffset(uint sector) => checked((long)sector * SectorSize);
    private static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
}
