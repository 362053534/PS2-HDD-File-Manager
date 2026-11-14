namespace PfsExplorerLite.Services;

internal sealed record ApaCreationPlan(long RequestedBytes, long AllocatedBytes, int SegmentCount, long MaxSegmentBytes)
{
    public bool Complete => RequestedBytes == AllocatedBytes;
    public string ErrorMessage => $"当前 APA 布局无法完整创建 {RequestedBytes / 1048576}M：一个主分区最多带 64 个子分区，本次最多分配 {AllocatedBytes / 1048576}M（{SegmentCount} 段）。请使用 {AllocatedBytes / 1048576}M 或更小的可行容量。未执行任何写入。";
}

// Read-only replay of 362053534/ps2sdk (9b972d16): apaGetPartitionMax,
// hddAddPartitionHere and apaInsertPartition. Never changes the partition table.
internal static class ApaPartitionPlanner
{
    private const long Unit = 0x40000;
    private readonly record struct Block(long Start, long Size);

    public static ApaCreationPlan Analyze(ApaPartitionTable layout, long requestedBytes)
    {
        if (requestedBytes <= 0 || requestedBytes % (Unit * 512) != 0)
            throw new InvalidOperationException("容量必须为 128 MiB 的正整数倍。");
        var sectors = layout.TotalBytes / 512;
        if (sectors <= Unit || sectors > uint.MaxValue || requestedBytes > layout.FreeBytes)
            throw new InvalidOperationException("APA 未分配空间不足或磁盘超出 32 位 LBA 范围。未执行任何写入。");
        var scaled = Math.Max(sectors, 0x04ffffff) >> 6;
        long maximum = 1;
        while (maximum < scaled) maximum <<= 1;
        maximum = Math.Min(maximum, 0x4000000); // Creation backends support 32GiB segments.
        var holes = new List<Block>();
        long tail = 0;
        foreach (var partition in layout.Partitions.OrderBy(p => p.StartSector))
        {
            long start = partition.StartSector, size = partition.LengthSectors;
            if (size == 0 || (size & (size - 1)) != 0 || start % size != 0 || start < tail || start + size > sectors)
                throw new InvalidOperationException("APA 块大小、对齐或范围无效，已拒绝创建。未执行任何写入。");
            if (partition.Type == 0 && start > 0) holes.Add(new Block(start, size));
            tail = start + size;
        }
        if (tail < Unit) throw new InvalidOperationException("APA 缺少有效的 MBR 分区，已拒绝创建。");
        bool Take(long size)
        {
            var index = -1;
            for (var i = 0; i < holes.Count; i++)
                if (holes[i].Size >= size && (index < 0 || holes[i].Size < holes[index].Size ||
                    holes[i].Size == holes[index].Size && holes[i].Start < holes[index].Start)) index = i;
            if (index >= 0)
            {
                var block = holes[index]; holes.RemoveAt(index);
                while (block.Size > size)
                {
                    block = block with { Size = block.Size / 2 };
                    holes.Add(new Block(block.Start + block.Size, block.Size));
                }
                return true;
            }
            var start = (tail + size - 1) / size * size;
            if (start + size > sectors - 1) return false;
            while (tail < start)
            {
                var gap = size / 2;
                while (gap >= Unit && tail % gap != 0) gap /= 2;
                if (gap < Unit) throw new InvalidOperationException("APA 尾部无法安全对齐，已拒绝创建。");
                holes.Add(new Block(tail, gap)); tail += gap;
            }
            tail = start + size;
            return true;
        }
        var left = requestedBytes / 512;
        var chunk = maximum;
        var segments = 0;
        while (left > 0 && chunk >= Unit && segments < 65)
        {
            if (chunk <= left && Take(chunk)) { left -= chunk; segments++; }
            else chunk /= 2;
        }
        return new ApaCreationPlan(requestedBytes, requestedBytes - left * 512, segments, maximum * 512);
    }

    public static long Maximum(ApaPartitionTable layout) => layout.FreeBytes == 0 ? 0 : Analyze(layout, layout.FreeBytes).AllocatedBytes;
    public static void RequireComplete(ApaPartitionTable layout, long bytes)
    {
        var plan = Analyze(layout, bytes);
        if (!plan.Complete) throw new InvalidOperationException(plan.ErrorMessage);
    }
}
