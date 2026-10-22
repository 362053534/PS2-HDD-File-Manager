import Foundation
import Darwin

final class CoreTests: CheckSuite {
    func testUTF8RoundTripAndTruncatedStrings() throws {
        let name = "RPG-真说侍魂-武士道列传\u{00a0}"
        var w = Wire(); try w.putString(name); w.put(UInt64(6_442_450_944))
        var r = Wire(w.bytes); XCTAssertEqual(try r.string(),name); XCTAssertEqual(try r.get(UInt64.self),6_442_450_944); try r.finish()
        var short = Wire(w.bytes.dropLast(9)); XCTAssertThrowsError(try short.string())
        var bad = Wire(Data([1,0,0xff])); XCTAssertThrowsError(try bad.string())
    }
    func testPathsCannotEscapeDestinationOrInjectShellCommands() throws {
        for name in ["..","a/b","a\\b","a\nb","a\0b"] { XCTAssertThrowsError(try Names.component(name)) }
        XCTAssertThrowsError(try LocalStorage.quote("a\"\nrm x"))
        XCTAssertEqual(try Names.path("/DVD","游戏.iso"),"/DVD/游戏.iso")
        XCTAssertEqual(try Names.size("1G"),1_073_741_824)
        XCTAssertThrowsError(try Names.size("129M"))
        XCTAssertThrowsError(try Names.partition("__mbr"))
    }
    func testPfsshellListingPreservesUnicodeWhitespaceAndLargeSize() throws {
        let text = "> -rw-rw-rw- 6442450944 2026-10-21 12:30 游戏 列传\u{00a0}\n> drwxrwxrwx 512 2026-10-21 12:30 DVD/\n"
        let e = try LocalStorage.parseListing(text)
        XCTAssertEqual(e.count,2); XCTAssertEqual(e[0].name,"游戏 列传\u{00a0}"); XCTAssertEqual(e[0].size,6_442_450_944); XCTAssertTrue(e[1].directory)
    }
    func header(start: UInt32 = 0, next: UInt32 = 0, length: UInt32 = 0x40000) -> Data {
        var d = Data(repeating:0,count:1024)
        func put(_ offset: Int,_ value: UInt32) { var w = Wire(); w.put(value); d.replaceSubrange(offset..<offset+4,with:w.bytes) }
        put(4,0x00415041); put(8,next); put(64,start); put(68,length)
        var sum: UInt32 = 0
        for i in stride(from:4,to:1024,by:4) { var r = Wire(d.subdata(in:i..<i+4)); sum &+= try! r.get(UInt32.self) }; put(0,sum)
        return d
    }
    func testAPAChecksumCycleAndBoundsAreRejected() throws {
        let total: UInt64 = 1024*1024*1024
        let valid = header()
        XCTAssertGreaterThan(try Layout.read(total:total,cancel:Cancellation()) { _,_ in valid }.free,0)
        var corrupt = valid; corrupt[20] ^= 1
        XCTAssertThrowsError(try Layout.read(total:total,cancel:Cancellation()) { _,_ in corrupt })
        XCTAssertThrowsError(try Layout.read(total:1024,cancel:Cancellation()) { _,_ in valid })
        XCTAssertThrowsError(try Layout.read(total:total,cancel:Cancellation()) { lba,_ in self.header(start:UInt32(lba),next:0x40000) })
    }
    func request(_ socket: TCPSocket) throws -> (UInt32,Data) {
        var w = Wire(try socket.read(12,cancel:nil)); let cmd = try w.get(UInt32.self), length = try w.get(UInt32.self); _ = try w.get(Int32.self)
        return (cmd,try socket.read(Int(length),cancel:nil))
    }
    func reply(_ socket: TCPSocket, _ payload: Data = Data(), fragment: Bool = false) throws {
        var h = Wire(); h.put(UInt32(1)); h.put(UInt32(payload.count)); h.put(Int32(0))
        let data = h.bytes+payload
        if fragment { for b in data { try socket.write(Data([b]),cancel:nil) } }
        else { try socket.write(data,cancel:nil) }
    }
    func server(_ body: @escaping (TCPSocket) throws -> Void) throws -> (UInt16,CheckExpectation) {
        let listener = try TCPSocket(fd:socket(AF_INET,SOCK_STREAM,0)); listener.set(SOL_SOCKET,SO_REUSEADDR,1)
        var a = sockaddr_in(); a.sin_len = UInt8(MemoryLayout.size(ofValue:a)); a.sin_family = sa_family_t(AF_INET); a.sin_addr.s_addr = inet_addr("127.0.0.1")
        let r = withUnsafePointer(to:&a) { $0.withMemoryRebound(to:sockaddr.self,capacity:1) { bind(listener.fd,$0,socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        XCTAssertEqual(r,0); XCTAssertEqual(listen(listener.fd,4),0)
        var length = socklen_t(MemoryLayout.size(ofValue:a))
        _ = withUnsafeMutablePointer(to:&a) { $0.withMemoryRebound(to:sockaddr.self,capacity:1) { getsockname(listener.fd,$0,&length) } }
        let done = expectation(description:"模拟 PS2 完成")
        DispatchQueue.global().async {
            defer { listener.close(); done.fulfill() }
            do {
                let peer = try listener.accept(peer:"127.0.0.1",cancel:nil); defer { peer.close() }
                let (cmd,payload) = try self.request(peer); XCTAssertEqual(cmd,1); XCTAssertEqual(payload.count,8)
                var hello = Wire(); hello.put(UInt32(0x4E534650)); hello.put(UInt32(1)); hello.put(UInt32(31))
                try self.reply(peer,hello.bytes,fragment:true)
                try body(peer)
            } catch { XCTFail("模拟服务端：\(error)") }
        }
        return (UInt16(bigEndian:a.sin_port),done)
    }
    func testFragmentedControlAndMalformedDirectoryCount() throws {
        let (port,done) = try server { peer in
            XCTAssertEqual(try self.request(peer).0,0x20)
            var r = Wire(); r.put(UInt32(1)); try r.putString("游戏.iso"); r.put(UInt8(0)); r.put(UInt64(6_442_450_944)); try r.putString("file")
            try self.reply(peer,r.bytes,fragment:true)
            XCTAssertEqual(try self.request(peer).0,0x20)
            var bad = Wire(); bad.put(UInt32.max); try self.reply(peer,bad.bytes)
        }
        let client = try NetworkStorage(host:"127.0.0.1",port:port,cancel:Cancellation())
        let items = try client.list("/DVD",Cancellation()); XCTAssertEqual(items.first?.size,6_442_450_944)
        XCTAssertThrowsError(try client.list("/DVD",Cancellation()))
        client.disconnect(); wait(for:[done],timeout:10)
    }
    func testUploadDownloadBinaryIntegrityAndCommitOrdering() throws {
        let bytes = Data((0..<1_100_003).map { UInt8(truncatingIfNeeded:$0*37) })
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:false); defer { try? FileManager.default.removeItem(at:folder) }
        let input = folder.appendingPathComponent("游戏.iso"), output = folder.appendingPathComponent("download.iso"); try bytes.write(to:input)
        let (port,done) = try server { peer in
            let (cmd,payload) = try self.request(peer); XCTAssertEqual(cmd,0x30)
            var begin = Wire(payload); XCTAssertEqual(try begin.string(),"/DVD"); XCTAssertEqual(try begin.string(),"游戏.iso"); XCTAssertEqual(try begin.get(UInt64.self),UInt64(bytes.count)); XCTAssertEqual(try begin.get(UInt32.self),0)
            try self.reply(peer)
            let data = try TCPSocket.connect("127.0.0.1",port:45062,cancel:nil)
            XCTAssertEqual(try data.read(bytes.count,cancel:nil),bytes); data.close()
            XCTAssertEqual(try self.request(peer).0,0x31); try self.reply(peer)
            XCTAssertEqual(try self.request(peer).0,0x33)
            var size = Wire(); size.put(UInt64(bytes.count)); try self.reply(peer,size.bytes)
            let download = try TCPSocket.connect("127.0.0.1",port:45062,cancel:nil)
            try download.write(bytes,cancel:nil); download.close()
            XCTAssertEqual(try self.request(peer).0,0x34); try self.reply(peer)
        }
        let client = try NetworkStorage(host:"127.0.0.1",port:port,cancel:Cancellation())
        try client.upload(input,parent:"/DVD",overwrite:false,cancel:Cancellation()) { _,_ in }
        try client.download(Entry("游戏.iso",directory:false,size:UInt64(bytes.count)),parent:"/DVD",to:output,cancel:Cancellation()) { _,_ in }
        XCTAssertEqual(try Data(contentsOf:output),bytes)
        client.disconnect(); wait(for:[done],timeout:10)
    }
    func testCancelledUploadResetsDataBeforeAbortAndKeepsControlUsable() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data(repeating:17,count:2*1024*1024).write(to:file); defer { try? FileManager.default.removeItem(at:file) }
        let (port,done) = try server { peer in
            XCTAssertEqual(try self.request(peer).0,0x30); try self.reply(peer)
            let data = try TCPSocket.connect("127.0.0.1",port:45062,cancel:nil)
            XCTAssertThrowsError(try data.read(2*1024*1024,cancel:nil)); data.close()
            XCTAssertEqual(try self.request(peer).0,0x32); try self.reply(peer)
            XCTAssertEqual(try self.request(peer).0,4); try self.reply(peer)
        }
        let client = try NetworkStorage(host:"127.0.0.1",port:port,cancel:Cancellation()); let cancel = Cancellation()
        XCTAssertThrowsError(try client.upload(file,parent:"/",overwrite:false,cancel:cancel) { _,_ in cancel.cancel() })
        try client.unmount(Cancellation()); client.disconnect(); wait(for:[done],timeout:10)
    }
    func testFailedDownloadPreservesExistingDestination() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let original = Data("existing data".utf8); try original.write(to:file); defer { try? FileManager.default.removeItem(at:file) }
        let (port,done) = try server { peer in
            XCTAssertEqual(try self.request(peer).0,0x33)
            var size = Wire(); size.put(UInt64(1024)); try self.reply(peer,size.bytes)
            let data = try TCPSocket.connect("127.0.0.1",port:45062,cancel:nil); try data.write(Data([1,2]),cancel:nil); data.close()
            XCTAssertEqual(try self.request(peer).0,0x35); try self.reply(peer)
        }
        let client = try NetworkStorage(host:"127.0.0.1",port:port,cancel:Cancellation())
        XCTAssertThrowsError(try client.download(Entry("x",directory:false,size:1024),parent:"/",to:file,cancel:Cancellation()) { _,_ in })
        XCTAssertEqual(try Data(contentsOf:file),original); client.disconnect(); wait(for:[done],timeout:10)
    }
    func testResumeUsesServerOffsetAfterDataReset() throws {
        var bytes = Data((0..<8*1024*1024).map { UInt8(truncatingIfNeeded:$0*31) })
        for i in stride(from:0,to:bytes.count,by:4096) {
            var w = Wire(); w.put(UInt64(i)); bytes.replaceSubrange(i..<i+8,with:w.bytes)
        }
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try bytes.write(to:file); defer { try? FileManager.default.removeItem(at:file) }
        let offset = 65536
        let (port,done) = try server { peer in
            XCTAssertEqual(try self.request(peer).0,0x30); try self.reply(peer)
            let data = try TCPSocket.connect("127.0.0.1",port:45062,cancel:nil)
            XCTAssertEqual(try data.read(offset,cancel:nil),bytes.prefix(offset)); data.close(reset:true)
            XCTAssertEqual(try self.request(peer).0,2); try self.reply(peer)
            let (cmd,payload) = try self.request(peer); XCTAssertEqual(cmd,0x30)
            var w = Wire(payload); _ = try w.string(); _ = try w.string(); _ = try w.get(UInt64.self)
            XCTAssertEqual(try w.get(UInt32.self),2)
            var position = Wire(); position.put(UInt64(offset)); try self.reply(peer,position.bytes)
            let resumed = try TCPSocket.connect("127.0.0.1",port:45062,cancel:nil)
            XCTAssertEqual(try resumed.read(bytes.count-offset,cancel:nil),Data(bytes.dropFirst(offset))); resumed.close()
            XCTAssertEqual(try self.request(peer).0,0x31); try self.reply(peer)
        }
        let client = try NetworkStorage(host:"127.0.0.1",port:port,cancel:Cancellation())
        try client.upload(file,parent:"/",overwrite:false,cancel:Cancellation()) { _,_ in }
        client.disconnect(); wait(for:[done],timeout:10)
    }
    func testLocalImageEndToEnd() throws {
        guard let executable = ProcessInfo.processInfo.environment["PFSSHELL_TEST_BIN"] else {
            throw CheckSkip.unavailable("set PFSSHELL_TEST_BIN")
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("apa-native-test-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:dir) }
        let image = dir.appendingPathComponent("test.img")
        XCTAssertTrue(FileManager.default.createFile(atPath:image.path,contents:nil))
        let f = try FileHandle(forWritingTo:image); try f.truncate(atOffset:1024*1024*1024); try f.close()
        let tool = URL(fileURLWithPath:executable)
        _ = try NativeProcess.run(tool,[],input:"device \"\(image.path)\"\ninitialize yes\nexit\n")
        let local = try LocalStorage(device:image,tool:tool); let c = Cancellation()
        try PartitionOperations.apply(0x50,values:["+Native","128M"],storage:local,cancel:c)
        try local.mount("+Native",c)
        let source = dir.appendingPathComponent("中文 列传.iso"), output = dir.appendingPathComponent("out.iso")
        let bytes = Data((0..<1_100_001).map { UInt8(truncatingIfNeeded:$0*17) }); try bytes.write(to:source)
        try local.upload(source,parent:"/",overwrite:false,cancel:c) { _,_ in }
        let entry = try XCTUnwrap(local.list("/",c).first { $0.name == source.lastPathComponent })
        XCTAssertEqual(entry.size,UInt64(bytes.count))
        try local.download(entry,parent:"/",to:output,cancel:c) { _,_ in }
        XCTAssertEqual(try Data(contentsOf:output),bytes)
        let changed = Data([1,3,5,7]); try changed.write(to:source)
        try local.upload(source,parent:"/",overwrite:true,cancel:c) { _,_ in }
        let replacement = try XCTUnwrap(local.list("/",c).first { $0.name == source.lastPathComponent })
        XCTAssertEqual(replacement.size,4)
        XCTAssertTrue(try local.space(c).free > 0)
        try local.mutate(0x21,["/","目录"],c)
        try local.mutate(0x24,["/",source.lastPathComponent,"renamed.iso"],c)
        try local.mutate(0x23,["/","renamed.iso"],c)
        try local.mutate(0x22,["/","目录"],c)
        try PartitionOperations.apply(0x52,values:["+Native","+Renamed"],storage:local,cancel:c)
        try PartitionOperations.apply(0x51,values:["+Renamed"],storage:local,cancel:c)
        XCTAssertTrue(try local.layout(c).partition("+Renamed") == nil)
    }

}
