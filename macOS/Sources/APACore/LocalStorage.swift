import Foundation
import Darwin

public enum NativeProcess {
    public static func run(_ executable: URL, _ arguments: [String], input: String = "", cancel: Cancellation? = nil) throws -> Data {
        try cancel?.check()
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("apa-process-\(UUID().uuidString)")
        guard FileManager.default.createFile(atPath:output.path,contents:nil,attributes:[.posixPermissions:0o600]) else { throw APAError.message("无法创建进程日志") }
        defer { try? FileManager.default.removeItem(at:output) }
        let log = try FileHandle(forWritingTo:output); defer { try? log.close() }
        let p = Process(); p.executableURL = executable; p.arguments = arguments
        p.standardOutput = log; p.standardError = log
        let pipe = Pipe(); p.standardInput = pipe
        try p.run()
        try pipe.fileHandleForWriting.write(contentsOf:Data(input.utf8)); try pipe.fileHandleForWriting.close()
        // 本地 pfsshell 写入不可强杀：完成当前文件并卸载后才响应取消。
        p.waitUntilExit()
        let data = try Data(contentsOf:output)
        guard p.terminationStatus == 0 else { throw APAError.message(String(decoding:data,as:UTF8.self)) }
        return data
    }
    public static func diskInfo(_ path: String) throws -> [String:Any] {
        let data = try run(URL(fileURLWithPath:"/usr/sbin/diskutil"),["info","-plist",path])
        guard let dict = try PropertyListSerialization.propertyList(from:data,format:nil) as? [String:Any] else { throw APAError.message("diskutil 返回无效数据") }
        return dict
    }
    public static func disks() throws -> [(String,String)] {
        let data = try run(URL(fileURLWithPath:"/usr/sbin/diskutil"),["list","-plist","physical"])
        guard let dict = try PropertyListSerialization.propertyList(from:data,format:nil) as? [String:Any], let disks = dict["WholeDisks"] as? [String] else { return [] }
        return try disks.map { name in
            let path = "/dev/"+name; let info = try diskInfo(path)
            return (path,"\(path) — \(info["MediaName"] as? String ?? "硬盘")")
        }
    }
}

public final class LocalStorage: Storage {
    private let device: URL
    private let tool: URL
    private var partition: String?
    private let total: UInt64
    public init(device: URL, tool: URL) throws {
        self.device = device; self.tool = tool
        if device.path.hasPrefix("/dev/") {
            let info = try NativeProcess.diskInfo(device.path)
            guard info["Whole"] as? Bool == true,
                  info["Internal"] as? Bool == false,
                  let size = info["TotalSize"] as? NSNumber else { throw APAError.message("请选择外接整盘；不允许操作 Mac 内置系统盘") }
            total = size.uint64Value
        } else {
            let values = try device.resourceValues(forKeys:[.isRegularFileKey,.fileSizeKey,.isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else { throw APAError.message("请选择普通硬盘镜像文件") }
            total = UInt64(values.fileSize ?? 0)
        }
        _ = try layout(Cancellation())
    }
    public func disconnect() { partition = nil }
    private func read(_ lba: UInt64, _ count: Int) throws -> Data {
        guard lba <= total/512, UInt64(count) <= total/512-lba else { throw APAError.message("读取超出磁盘范围") }
        let f = try FileHandle(forReadingFrom:device); defer { try? f.close() }
        try f.seek(toOffset:lba*512)
        let b = try f.read(upToCount:count*512) ?? Data()
        guard b.count == count*512 else { throw APAError.message("磁盘读取被截断") }
        return b
    }
    public func layout(_ cancel: Cancellation) throws -> Layout { try Layout.read(total:total,cancel:cancel,sectors:read) }
    public func mount(_ name: String, _ cancel: Cancellation) throws { try cancel.check(); partition = name }
    public func unmount(_ cancel: Cancellation) throws { partition = nil }
    static func quote(_ value: String) throws -> String {
        guard !value.contains("\""), !value.contains("\n"), !value.contains("\r"), !value.utf8.contains(0) else { throw APAError.message("本地 pfsshell 不支持名称中的引号或换行") }
        return "\""+value+"\""
    }
    private func session(_ command: String, path: String = "/", cancel: Cancellation) throws -> String {
        var script = "device \(try Self.quote(device.path))\n"
        if let partition { script += "mount \(try Self.quote(partition))\n" }
        for component in path.split(separator:"/") { script += "cd \(try Self.quote(String(component)))\n" }
        script += command+"\n"
        if partition != nil { script += "umount\n" }; script += "exit\n"
        let data = try NativeProcess.run(tool,[],input:script,cancel:cancel)
        let text = String(decoding:data,as:UTF8.self)
        guard !text.contains("(!)") else { throw APAError.message(text) }
        return text
    }
    public static func parseListing(_ text: String) throws -> [Entry] {
        let regex = try NSRegularExpression(pattern:"^[^\\r\\n]*?([d-][rwx-]{9})[ \\t]+([0-9]+)[ \\t]+[0-9]{4}-[0-9]{2}-[0-9]{2}[ \\t]+[0-9]{2}:[0-9]{2}[ \\t]+(.+)$",options:.anchorsMatchLines)
        return try regex.matches(in:text,range:NSRange(text.startIndex...,in:text)).compactMap { match in
            func part(_ i: Int) -> String { String(text[Range(match.range(at:i),in:text)!]) }
            var name = part(3)
            if name.hasSuffix("\r") { name.removeLast() }
            if name.hasSuffix("/") { name.removeLast() }
            if name == "." || name == ".." { return nil }
            try Names.component(name)
            let start = match.range(at:1).location
            let detailRange = NSRange(location:start,length:NSMaxRange(match.range)-start)
            let details = String(text[Range(detailRange,in:text)!]).replacingOccurrences(of:"\r",with:"")
            return Entry(name,directory:part(1).hasPrefix("d"),size:UInt64(part(2)) ?? 0,details:details)
        }
    }
    public func list(_ path: String, _ cancel: Cancellation) throws -> [Entry] {
        guard partition != nil else { throw APAError.message("未选择分区") }
        return try Self.parseListing(session("ls -l",path:path,cancel:cancel))
    }
    public func space(_ cancel: Cancellation) throws -> Space {
        guard let name = partition, let p = try layout(cancel).partition(name) else { throw APAError.message("未选择 PFS 分区") }
        let superblock = try read(UInt64(p.start)+8192,1)
        func u32(_ b: Data,_ at: Int) throws -> UInt32 { var w = Wire(b.subdata(in:at..<at+4)); return try w.get() }
        guard try u32(superblock,0) == 0x50465300 else { throw APAError.message("PFS 超级块无效") }
        let zone = try u32(superblock,16), subs = try u32(superblock,20)
        guard zone >= 2048, zone <= 1024*1024, zone & (zone-1) == 0, subs == p.subs.count else { throw APAError.message("PFS 超级块与 APA 不一致") }
        var free: UInt64 = 0
        for (index,extent) in ([(p.start,p.length)]+p.subs).enumerated() {
            let zones = UInt64(extent.1)/(UInt64(zone)/512)
            for chunk in 0..<((zones+8191)/8192) {
                try cancel.check()
                let n = min(8192,zones-chunk*8192)
                guard n % 8 == 0 else { throw APAError.message("PFS 位图长度无效") }
                let block = UInt64(zone)/1024+chunk+(index == 0 ? 4096:0)
                let bitmap = try read(UInt64(extent.0)+block*2,2)
                for byte in bitmap.prefix(Int(n/8)) { free += UInt64(8-byte.nonzeroBitCount)*UInt64(zone) }
            }
        }
        return Space(total:p.size,free:min(free,p.size),zone:zone)
    }
    public func mutate(_ command: UInt32, _ values: [String], _ cancel: Cancellation) throws {
        let q = try values.map(Self.quote)
        let cmd: String; var path = "/"
        switch command {
        case 0x21,0x22,0x23,0x24:
            guard q.count >= 2 else { throw APAError.message("参数不足") }
            path = values[0]
            let verb = [0x21:"mkdir",0x22:"rmdir",0x23:"rm",0x24:"rename"][Int(command)]!
            cmd = verb+" "+q.dropFirst().joined(separator:" ")
        case 0x50: cmd = "mkpart "+q.joined(separator:" ")+" PFS"
        case 0x51: cmd = "rmpart "+q.joined(separator:" ")
        case 0x52: cmd = "rename "+q.joined(separator:" ")
        default: throw APAError.message("不支持的本地操作")
        }
        _ = try session(cmd,path:path,cancel:cancel)
    }
    public func upload(_ local: URL, parent: String, overwrite: Bool, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws {
        let name = local.lastPathComponent; try Names.component(name)
        let existing = try list(parent,cancel).first { $0.name == name }
        if let existing, !overwrite || existing.directory { throw APAError.message("目标已存在：\(name)") }
        let size = UInt64(try local.resourceValues(forKeys:[.fileSizeKey]).fileSize ?? 0)
        progress(0,size)
        // 先写唯一临时名并校验大小；替换时备份旧文件，提交失败则尽力恢复。
        let staging = ".apanet_" + UUID().uuidString.replacingOccurrences(of:"-",with:"")
        let backup = ".apabackup_" + UUID().uuidString.replacingOccurrences(of:"-",with:"")
        var staged = false; var backedUp = false; var committed = false
        do {
            _ = try session("put \(try Self.quote(local.path)) \(try Self.quote(staging))",path:parent,cancel:cancel)
            staged = true
            guard try list(parent,Cancellation()).contains(where: { $0.name == staging && $0.size == size && !$0.directory }) else { throw APAError.message("本地导入写后大小校验失败") }
            try cancel.check()
            let commit = Cancellation()
            if existing != nil {
                _ = try session("rename \(try Self.quote(name)) \(try Self.quote(backup))",path:parent,cancel:commit)
                backedUp = true
            }
            _ = try session("rename \(try Self.quote(staging)) \(try Self.quote(name))",path:parent,cancel:commit)
            committed = true
            if backedUp { _ = try session("rm \(try Self.quote(backup))",path:parent,cancel:commit) }
        } catch {
            if !committed {
                if backedUp { _ = try? session("rename \(try Self.quote(backup)) \(try Self.quote(name))",path:parent,cancel:Cancellation()) }
                if staged { _ = try? session("rm \(try Self.quote(staging))",path:parent,cancel:Cancellation()) }
            }
            throw error
        }
        progress(size,size); try cancel.check()
    }
    public func download(_ entry: Entry, parent: String, to destination: URL, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws {
        let temp = destination.deletingLastPathComponent().appendingPathComponent(".apa-export-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at:temp,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:temp) }
        progress(0,entry.size)
        _ = try session("lcd \(try Self.quote(temp.path))\nget \(try Self.quote(entry.name))",path:parent,cancel:cancel)
        let file = temp.appendingPathComponent(entry.name)
        let size = UInt64(try file.resourceValues(forKeys:[.fileSizeKey]).fileSize ?? 0)
        guard size == entry.size else { throw APAError.message("导出文件大小不符") }
        try cancel.check()
        guard Darwin.rename(file.path,destination.path) == 0 else { throw TCPSocket.failure("保存文件失败") }
        progress(size,size)
    }
}
