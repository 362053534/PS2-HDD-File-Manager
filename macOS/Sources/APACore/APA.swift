import Foundation

public struct Partition {
    public let name: String
    public let start: UInt32
    public let length: UInt32
    public let type: UInt16
    public let flags: UInt16
    public let main: UInt32
    public let number: UInt32
    public let subs: [(UInt32, UInt32)]
    public var size: UInt64 { (UInt64(length) + subs.reduce(0) { $0 + UInt64($1.1) }) * 512 }
    public var isMain: Bool { flags & 1 == 0 }
}
public struct Layout {
    public let total: UInt64
    public let free: UInt64
    public let partitions: [Partition]
    public var browsable: [Entry] {
        partitions.filter { $0.isMain && $0.type == 0x100 }.map { Entry($0.name, directory: true, size: $0.size) }
    }
    public func partition(_ name: String) -> Partition? { partitions.first { $0.isMain && $0.name == name } }
    public static func read(total: UInt64, cancel: Cancellation, sectors: (UInt64, Int) throws -> Data) throws -> Layout {
        guard total >= 1024 else { throw APAError.message("无效硬盘容量") }
        var visited = Set<UInt32>(); var result = [Partition](); var sector: UInt32 = 0
        var free: UInt64 = 0; var lastEnd: UInt64 = 0
        let limit = total / 512
        func u32(_ data: Data, _ at: Int) throws -> UInt32 { var w = Wire(data.subdata(in: at..<at+4)); return try w.get() }
        func u16(_ data: Data, _ at: Int) throws -> UInt16 { var w = Wire(data.subdata(in: at..<at+2)); return try w.get() }
        for _ in 0..<4096 {
            try cancel.check()
            guard visited.insert(sector).inserted, UInt64(sector)+2 <= limit else { throw APAError.message("APA 链循环或越界，已拒绝写入") }
            let b = try sectors(UInt64(sector), 2)
            guard b.count == 1024, try u32(b,4) == 0x00415041 else { throw APAError.message("未检测到有效 APA 分区表") }
            var sum: UInt32 = 0
            for i in stride(from: 4, to: 1024, by: 4) { sum &+= try u32(b,i) }
            guard sum == (try u32(b,0)) else { throw APAError.message("APA 校验和错误，已拒绝写入") }
            let start = try u32(b,64), length = try u32(b,68), count = try u32(b,76)
            guard start == sector, length > 0, UInt64(start)+UInt64(length) <= limit, count <= 64 else { throw APAError.message("APA 范围或子分区数量无效") }
            let nameData = b.subdata(in: 16..<48).prefix { $0 != 0 }
            guard let name = String(data: nameData, encoding: .utf8) else { throw APAError.message("分区名不是 UTF-8") }
            var subs = [(UInt32,UInt32)]()
            for i in 0..<Int(count) {
                let s = try u32(b,512+i*8), n = try u32(b,516+i*8)
                guard s > 0, n > 0, UInt64(s)+UInt64(n) <= limit else { throw APAError.message("APA 子分区越界") }
                subs.append((s,n))
            }
            let p = Partition(name:name,start:start,length:length,type:try u16(b,72),flags:try u16(b,74),main:try u32(b,88),number:try u32(b,92),subs:subs)
            result.append(p)
            let end = UInt64(start)+UInt64(length); lastEnd = max(lastEnd,end)
            if p.isMain && p.type == 0 { free += (min(end,limit-1)-UInt64(start))/0x40000*0x40000*512 }
            let next = try u32(b,8)
            if next == 0 {
                let sorted = result.sorted { $0.start < $1.start }
                for (a,b) in zip(sorted,sorted.dropFirst()) {
                    guard UInt64(a.start)+UInt64(a.length) <= UInt64(b.start) else { throw APAError.message("APA 分区重叠") }
                }
                for parent in result where parent.isMain {
                    for (i,sub) in parent.subs.enumerated() {
                        guard result.contains(where: { !$0.isMain && $0.start == sub.0 && $0.length == sub.1 && $0.main == parent.start && $0.number == UInt32(i+1) }) else { throw APAError.message("APA 子分区引用不一致") }
                    }
                }
                free += (limit-1 > lastEnd ? limit-1-lastEnd : 0)/0x40000*0x40000*512
                return Layout(total:total,free:min(free,total),partitions:result)
            }
            sector = next
        }
        throw APAError.message("APA 分区链过长")
    }
}

public protocol Storage: AnyObject {
    func layout(_ cancel: Cancellation) throws -> Layout
    func mount(_ name: String, _ cancel: Cancellation) throws
    func unmount(_ cancel: Cancellation) throws
    func list(_ path: String, _ cancel: Cancellation) throws -> [Entry]
    func space(_ cancel: Cancellation) throws -> Space
    func mutate(_ command: UInt32, _ values: [String], _ cancel: Cancellation) throws
    func upload(_ local: URL, parent: String, overwrite: Bool, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws
    func download(_ entry: Entry, parent: String, to: URL, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws
    func disconnect()
}

public enum PartitionOperations {
    public static func apply(_ command: UInt32, values: [String], storage: Storage, cancel: Cancellation) throws {
        guard let name = values.first else { throw APAError.message("缺少分区名") }
        try Names.partition(name)
        let before = try storage.layout(cancel)
        var expected: UInt64 = 0
        if command == 0x50 {
            guard values.count == 2 else { throw APAError.message("缺少分区容量") }
            guard !before.partitions.contains(where: { $0.isMain && $0.name.caseInsensitiveCompare(name) == .orderedSame }) else {
                throw APAError.message("分区“\(name)”已存在，已取消创建。")
            }
            expected = try Names.size(values[1])
            guard expected <= before.free else { throw APAError.message("APA 未分配空间不足") }
            try ApaAllocation.requireComplete(before,bytes:expected)
        } else {
            guard let old = before.partition(name), old.type == 0x100 else { throw APAError.message("只能修改 PFS 主分区") }
            if command == 0x52 {
                guard values.count == 2 else { throw APAError.message("缺少新名称") }
                try Names.partition(values[1])
                guard before.partition(values[1]) == nil else { throw APAError.message("新分区名已存在") }
            }
        }
        try storage.unmount(cancel)
        try storage.mutate(command,values,cancel)
        // 变更已经提交后，即使用户点取消，也必须完成只读复核。
        let verify = Cancellation()
        let after = try storage.layout(verify)
        var valid = false
        switch command {
        case 0x50: valid = after.partition(name).map { $0.type == 0x100 && $0.size == expected } ?? false
        case 0x51: valid = after.partition(name) == nil && after.free > before.free
        case 0x52: valid = after.partition(name) == nil && after.partition(values[1])?.size == before.partition(name)?.size
        default: break
        }
        guard valid else { throw APAError.message("分区变更后的 APA 表与预期不符。请停止写入并保留硬盘现状。") }
        if command == 0x50 { try storage.mount(name,verify); _ = try storage.list("/",verify); try storage.unmount(verify) }
    }
}
