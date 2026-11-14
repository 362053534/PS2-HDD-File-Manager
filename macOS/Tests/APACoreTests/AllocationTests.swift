import Foundation

// No disk access: captures whether the real creation entry point reaches a write.
private final class AllocationProbeStorage: Storage {
    var writes = 0
    var existingName: String?
    func layout(_ c: Cancellation) throws -> Layout {
        Layout(total:UInt64(0x747059c0)*512,free:953728*1024*1024,partitions:[
            Partition(name:"__mbr",start:0,length:0x40000,type:1,flags:0,main:0,number:0,subs:[])
        ] + (existingName.map { [Partition(name:$0,start:0x40000,length:0x40000,type:0x100,flags:0,main:0,number:0,subs:[])] } ?? []))
    }
    func mutate(_ command: UInt32, _ values: [String], _ c: Cancellation) throws {
        writes += 1; throw APAError.message("creation reached a write despite exceeding the APA segment budget")
    }
    func mount(_ name: String, _ c: Cancellation) throws {}
    func unmount(_ c: Cancellation) throws {}
    func list(_ path: String, _ c: Cancellation) throws -> [Entry] { [] }
    func space(_ c: Cancellation) throws -> Space { Space(total:0,free:0) }
    func upload(_ local: URL, parent: String, overwrite: Bool, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws {}
    func download(_ entry: Entry, parent: String, to: URL, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws {}
    func disconnect() {}
}
extension CoreTests {
    func testCreateRejects953728MiBBeforeAnyWrite() throws {
        let storage = AllocationProbeStorage()
        XCTAssertThrowsError(try PartitionOperations.apply(0x50,values:["+OPL","953728M"],storage:storage,cancel:Cancellation()))
        XCTAssertEqual(storage.writes,0)
    }
}

extension CoreTests {
    func testAllocationGeometryAndSlotLimits() throws {
        let mib: UInt64 = 1048576, unit: UInt64 = 0x40000
        func part(_ start: UInt32, _ type: UInt16 = 1) -> Partition {
            Partition(name:"p\(start)",start:start,length:UInt32(unit),type:type,flags:0,main:0,number:0,subs:[])
        }
        func empty(_ sectors: UInt32) -> Layout {
            Layout(total:UInt64(sectors)*512,free:(UInt64(sectors)-1-unit)/unit*unit*512,partitions:[part(0)])
        }
        let disk = empty(0x747059c0)
        let plan = try ApaAllocation.plan(disk,bytes:953728*mib)
        XCTAssertTrue(!plan.complete); XCTAssertEqual(plan.allocated,953344*mib); XCTAssertEqual(plan.segments,65)
        XCTAssertEqual(try ApaAllocation.maximum(disk),953344*mib)
        XCTAssertTrue(try ApaAllocation.plan(disk,bytes:953344*mib).complete)
        let large = empty(0xe8e0b380), twoTB = try ApaAllocation.plan(empty(0xe8e0b380),bytes:empty(0xe8e0b380).free)
        XCTAssertEqual(twoTB.maxSegment,32768*mib); XCTAssertGreaterThan(twoTB.allocated,1800*1024*mib)
        XCTAssertTrue(try ApaAllocation.plan(large,bytes:twoTB.allocated).complete)
        var parts = [part(0)]
        for i in 1...132 { parts.append(part(UInt32(UInt64(i)*unit),i % 2 == 1 ? 0:0x100)) }
        let fragmented = Layout(total:(133*unit+1)*512,free:66*128*mib,partitions:parts)
        let frag = try ApaAllocation.plan(fragmented,bytes:fragmented.free)
        XCTAssertTrue(!frag.complete); XCTAssertEqual(frag.allocated,65*128*mib); XCTAssertEqual(frag.segments,65)
        XCTAssertTrue(try ApaAllocation.plan(fragmented,bytes:frag.allocated).complete)
        XCTAssertEqual(try ApaAllocation.plan(empty(0x200000),bytes:128*mib).segments,1)
        XCTAssertEqual(disk.partitions.count,1)
        XCTAssertThrowsError(try ApaAllocation.plan(disk,bytes:0))
        XCTAssertThrowsError(try ApaAllocation.plan(disk,bytes:129*mib))
        XCTAssertThrowsError(try ApaAllocation.plan(Layout(total:disk.total,free:128*mib,partitions:disk.partitions),bytes:256*mib))
        XCTAssertThrowsError(try ApaAllocation.plan(Layout(total:disk.total,free:disk.free,partitions:[part(0),part(1)]),bytes:128*mib))
        XCTAssertThrowsError(try ApaAllocation.plan(Layout(total:(UInt64(UInt32.max)+1)*512,free:disk.free,partitions:disk.partitions),bytes:128*mib))
    }
}


extension CoreTests {
    func testCreateRejectsExistingPartitionBeforeAnyWrite() throws {
        for existing in ["+OPL", "+opl"] {
            let storage = AllocationProbeStorage()
            storage.existingName = existing
            do {
                try PartitionOperations.apply(0x50,values:["+OPL","128M"],storage:storage,cancel:Cancellation())
                XCTFail("existing partition must prevent creation")
            } catch {
                XCTAssertEqual(error.localizedDescription,"分区“+OPL”已存在，已取消创建。")
            }
            XCTAssertEqual(storage.writes,0)
        }
    }
}
