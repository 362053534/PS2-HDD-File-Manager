import Foundation

// Opt-in integration check. Only initializes a new UUID sparse image; accepts no device path.
@main struct LargePartitionProbe {
    static func main() {
        do { try run() }
        catch { FileHandle.standardError.write(Data("FAIL large image: \(error.localizedDescription)\n".utf8)); exit(1) }
    }
    static func run() throws {
        guard CommandLine.arguments.count == 2 else { throw APAError.message("usage: LargePartitionProbe <canonical pfsshell>") }
        let tool = URL(fileURLWithPath:CommandLine.arguments[1])
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("apa-large-create-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:root) }
        let image = root.appendingPathComponent("fixture.img")
        FileManager.default.createFile(atPath:image.path,contents:nil)
        let handle = try FileHandle(forWritingTo:image)
        try handle.truncate(atOffset:UInt64(0x747059c0)*512); try handle.close()
        _ = try NativeProcess.run(tool,[],input:"device \"\(image.path)\"\ninitialize yes\nexit\n")
        // initialize creates system volumes in this fork. Reconstruct the user's
        // MBR-only chain inside our own temporary image; preserve the valid MBR metadata.
        let fixture = try FileHandle(forUpdating:image)
        var mbr = try fixture.read(upToCount:1024)!
        func put32(_ offset: Int, _ value: UInt32) {
            var w = Wire(); w.put(value); mbr.replaceSubrange(offset..<offset+4,with:w.bytes)
        }
        put32(8,0); put32(12,0)
        var checksum: UInt32 = 0
        for offset in stride(from:4,to:1024,by:4) {
            var word = Wire(mbr.subdata(in:offset..<offset+4)); checksum &+= try word.get(UInt32.self)
        }
        put32(0,checksum)
        try fixture.seek(toOffset:0); try fixture.write(contentsOf:mbr)
        try fixture.truncate(atOffset:UInt64(0x747059c0)*512); try fixture.close()
        let storage = try LocalStorage(device:image,tool:tool), token = Cancellation()
        let before = try storage.layout(token)
        func require(_ condition: Bool, _ message: String) throws {
            guard condition else { throw APAError.message(message) }; print("PASS large image: \(message)")
        }
        try require(before.free == 953728*1048576,"exact logged 953728M free layout reproduced")
        let modified = try image.resourceValues(forKeys:[.contentModificationDateKey]).contentModificationDate
        var rejected = false
        do { try PartitionOperations.apply(0x50,values:["+OPL","953728M"],storage:storage,cancel:token) }
        catch { rejected = error.localizedDescription.contains("953344M") }
        let afterDate = try image.resourceValues(forKeys:[.contentModificationDateKey]).contentModificationDate
        try require(rejected && afterDate == modified,"953728M rejected by client before any image modification; suggests 953344M")
        var backendRejected = false
        do { try storage.mutate(0x50,["+OPL","953728M"],token) }
        catch {
            let text = error.localizedDescription
            backendRejected = text.contains("refusing to format") && !text.contains("pfs: Format:")
        }
        let after = try storage.layout(token)
        try require(backendRejected && after.partition("+OPL") == nil && after.free == before.free,"direct pfsshell shortfall refuses formatting and removes only its new incomplete partition")
        try PartitionOperations.apply(0x50,values:["+OPL","953344M"],storage:storage,cancel:token)
        let complete = try storage.layout(token).partition("+OPL")
        try require(complete?.size == 953344*1048576 && complete?.subs.count == 64,"953344M actually creates, formats and mounts with exactly 64 subpartitions")
        try PartitionOperations.apply(0x51,values:["+OPL"],storage:storage,cancel:token)
        try require(try storage.layout(token).partition("+OPL") == nil,"test partition removed from disposable image")
    }
}
