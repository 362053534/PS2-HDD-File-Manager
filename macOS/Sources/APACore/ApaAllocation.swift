import Foundation

/// Read-only replay of 362053534/ps2sdk's buddy allocator (apaGetPartitionMax,
/// hddAddPartitionHere, apaInsertPartition). No sectors are written here.
public enum ApaAllocation {
    private static let unit: UInt64 = 0x40000
    public struct Plan {
        public let requested: UInt64
        public let allocated: UInt64
        public let segments: Int
        public let maxSegment: UInt64
        public var complete: Bool { requested == allocated }
        public var rejection: String {
            "当前 APA 布局无法完整创建 \(requested / 1048576)M：一个主分区最多带 64 个子分区，本次最多分配 \(allocated / 1048576)M（\(segments) 段）。请使用 \(allocated / 1048576)M 或更小的可行容量。未执行任何写入。"
        }
    }
    private struct Block { var start: UInt64; var size: UInt64 }
    public static func plan(_ layout: Layout, bytes: UInt64) throws -> Plan {
        guard bytes > 0, bytes % (unit*512) == 0 else { throw APAError.message("容量必须为 128 MiB 的正整数倍") }
        let sectors = layout.total / 512
        guard sectors > unit, sectors <= UInt64(UInt32.max), bytes <= layout.free else { throw APAError.message("APA 未分配空间不足或磁盘超出 32 位 LBA 范围。未执行任何写入。") }
        // The canonical fork enables the <40GB compatibility workaround.
        let scaled = max(sectors,0x04ffffff) >> 6
        var maximum: UInt64 = 1
        while maximum < scaled { maximum <<= 1 }
        maximum = min(maximum,0x4000000) // Both creation backends support up to 32GiB per segment.
        var holes = [Block](), tail: UInt64 = 0
        for part in layout.partitions.sorted(by: { $0.start < $1.start }) {
            let start = UInt64(part.start), size = UInt64(part.length)
            guard size > 0, size & (size-1) == 0, start % size == 0, start >= tail, start+size <= sectors else {
                throw APAError.message("APA 块大小、对齐或范围无效，已拒绝创建。未执行任何写入。")
            }
            if part.type == 0 && start > 0 { holes.append(Block(start:start,size:size)) }
            tail = start+size
        }
        guard tail >= unit else { throw APAError.message("APA 缺少有效的 MBR 分区，已拒绝创建。") }
        func take(_ size: UInt64) throws -> Bool {
            let candidate = holes.indices.filter { holes[$0].size >= size }.min {
                holes[$0].size == holes[$1].size ? holes[$0].start < holes[$1].start : holes[$0].size < holes[$1].size
            }
            if let index = candidate {
                var block = holes.remove(at:index)
                while block.size > size { block.size >>= 1; holes.append(Block(start:block.start+block.size,size:block.size)) }
                return true
            }
            let start = (tail+size-1)/size*size
            // Keep the reader's conservative final-LBA boundary, including on exact-sized images.
            guard start+size <= sectors-1 else { return false }
            while tail < start {
                var gap = size >> 1
                while gap >= unit && tail % gap != 0 { gap >>= 1 }
                guard gap >= unit else { throw APAError.message("APA 尾部无法安全对齐，已拒绝创建。") }
                holes.append(Block(start:tail,size:gap)); tail += gap
            }
            tail = start+size; return true
        }
        var left = bytes/512, size = maximum, segments = 0
        while left > 0 && size >= unit && segments < 65 {
            if try size <= left && take(size) { left -= size; segments += 1 }
            else { size >>= 1 }
        }
        return Plan(requested:bytes,allocated:bytes-left*512,segments:segments,maxSegment:maximum*512)
    }
    public static func maximum(_ layout: Layout) throws -> UInt64 {
        layout.free == 0 ? 0 : try plan(layout,bytes:layout.free).allocated
    }
    public static func requireComplete(_ layout: Layout, bytes: UInt64) throws {
        let result = try plan(layout,bytes:bytes)
        guard result.complete else { throw APAError.message(result.rejection) }
    }
}
