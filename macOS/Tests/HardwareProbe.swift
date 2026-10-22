import Foundation
import CryptoKit

// 手动实机回归入口；默认只读。显式 --transfer 才在 +OPL 新建 UUID 测试目录。
@main struct HardwareProbe {
    static func main() {
        do { try run() } catch { print("FAIL \(error.localizedDescription)"); exit(1) }
    }
    static func run() throws {
        guard CommandLine.arguments.count >= 2 else { throw APAError.message("用法：HardwareProbe <PS2 IPv4> [--transfer]") }
        let c = Cancellation(), client = try NetworkStorage(host:CommandLine.arguments[1],cancel:Cancellation())
        defer { client.disconnect() }
        let layout = try client.layout(c)
        print("APA validated: \(layout.browsable.map(\.name)), free=\(layout.free)")
        try client.mount("+OPL",c)
        print("+OPL root entries: \(try client.list("/",c).count)")
        guard CommandLine.arguments.contains("--transfer") else { try client.unmount(c); return }
        let remote = "apanative_"+UUID().uuidString.replacingOccurrences(of:"-",with:"").prefix(12)
        try client.mutate(0x21,["/",remote],c)
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:dir) }
        let input = dir.appendingPathComponent("中文-64MiB.bin"), output = dir.appendingPathComponent("readback.bin")
        let block = Data((0..<1024*1024).map { _ in UInt8.random(in:0...255) })
        FileManager.default.createFile(atPath:input.path,contents:nil)
        let f = try FileHandle(forWritingTo:input)
        for _ in 0..<64 { try f.write(contentsOf:block) }; try f.close()
        var tick = Date.distantPast
        func progress(_ done: UInt64, _ total: UInt64) {
            if Date().timeIntervalSince(tick)>5 || done == total { print("\(done)/\(total)"); tick = Date() }
        }
        let begin = Date()
        try client.upload(input,parent:"/"+remote,overwrite:false,cancel:c,progress:progress)
        print("PUT seconds=\(Date().timeIntervalSince(begin))")
        let entry = try client.list("/"+remote,c).first { $0.name == input.lastPathComponent }!
        try client.download(entry,parent:"/"+remote,to:output,cancel:c,progress:progress)
        let expected = SHA256.hash(data:try Data(contentsOf:input)), actual = SHA256.hash(data:try Data(contentsOf:output))
        guard expected == actual else { throw APAError.message("SHA256 mismatch") }
        print("PASS SHA256=\(actual), remote=/\(remote)/\(entry.name)")
        try client.unmount(c)
    }
}
