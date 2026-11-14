using PfsExplorerLite.Services;

internal static class AllocationChecks
{
    public static void Run(Action<bool, string> expect)
    {
        const long MiB = 1048576, Unit = 0x40000;
        ApaPartitionInfo Part(uint start, ushort type = 1) => new("p" + start, start, (uint)Unit, type, 0, 0, 0, []);
        ApaPartitionTable Empty(uint sectors) => new((long)sectors * 512, ((long)sectors - 1 - Unit) / Unit * Unit * 512, [Part(0)]);
        void Reject(Action action, string claim)
        {
            try { action(); expect(false, claim); }
            catch (InvalidOperationException) { expect(true, claim); }
        }
        var disk = Empty(0x747059c0);
        var plan = ApaPartitionPlanner.Analyze(disk, 953728 * MiB);
        expect(!plan.Complete && plan.AllocatedBytes == 953344 * MiB && plan.SegmentCount == 65, "1TB log: reject 953728M at 65 segments with 384M left");
        expect(ApaPartitionPlanner.Maximum(disk) == 953344 * MiB, "default capacity is feasible 953344M, not total free bytes");
        expect(ApaPartitionPlanner.Analyze(disk, 953344 * MiB).Complete, "931G fits exactly with 64 subpartitions");
        Reject(() => ApaPartitionPlanner.RequireComplete(disk, 953728 * MiB), "creation guard rejects unsatisfied allocation");
        var large = Empty(0xe8e0b380);
        var twoTB = ApaPartitionPlanner.Analyze(large, large.FreeBytes);
        expect(twoTB.MaxSegmentBytes == 32768 * MiB && twoTB.AllocatedBytes > 1800L * 1024 * MiB, "2TB disk uses 32GiB segments and permits more than 1.8TiB");
        expect(ApaPartitionPlanner.Analyze(large, twoTB.AllocatedBytes).Complete, "2TB suggested capacity can be allocated exactly");
        var partitions = new List<ApaPartitionInfo> { Part(0) };
        for (uint i = 1; i <= 132; i++) partitions.Add(Part(i * (uint)Unit, (ushort)(i % 2 == 1 ? 0 : 0x100)));
        var fragmented = new ApaPartitionTable((133 * Unit + 1) * 512, 66 * 128 * MiB, partitions);
        var frag = ApaPartitionPlanner.Analyze(fragmented, fragmented.FreeBytes);
        expect(!frag.Complete && frag.AllocatedBytes == 65 * 128 * MiB && frag.SegmentCount == 65, "66 small free extents do not fit into 65 slots");
        expect(ApaPartitionPlanner.Analyze(fragmented, frag.AllocatedBytes).Complete, "fragmented layout suggestion replays successfully");
        expect(ApaPartitionPlanner.Analyze(Empty(0x200000), 128 * MiB).SegmentCount == 1, "small image can create a single 128M partition");
        expect(disk.Partitions.Count == 1 && disk.Partitions[0].StartSector == 0, "planning does not alter input layout");
        Reject(() => ApaPartitionPlanner.Analyze(disk, 0), "zero size rejected");
        Reject(() => ApaPartitionPlanner.Analyze(disk, 129 * MiB), "unaligned size rejected");
        Reject(() => ApaPartitionPlanner.Analyze(disk with { FreeBytes = 128 * MiB }, 256 * MiB), "insufficient total space rejected");
        Reject(() => ApaPartitionPlanner.Analyze(disk with { Partitions = [Part(0), Part(1)] }, 128 * MiB), "overlapping or unaligned APA blocks rejected");
        Reject(() => ApaPartitionPlanner.Analyze(disk with { TotalBytes = ((long)uint.MaxValue + 1) * 512 }, 128 * MiB), "unsupported LBA overflow rejected before mutation");
    }
}
