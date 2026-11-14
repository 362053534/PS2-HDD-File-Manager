import Foundation
import Darwin

public final class NetworkStorage: Storage {
    private var control: TCPSocket?
    private let host: String
    private let port: UInt16
    private var mounted: String?
    private struct RetryUpload: Error { let underlying: Error }
    private var features: UInt32 = 0
    public init(host: String, port: UInt16 = 45061, cancel: Cancellation) throws {
        self.host = host; self.port = port
        control = try TCPSocket.connect(host, port: port, cancel: cancel)
        var hello = Wire(); hello.put(UInt32(0x4E534650)); hello.put(UInt32(1))
        var reply = Wire(try rpc(1,hello.bytes,cancel))
        guard try reply.get(UInt32.self) == 0x4E534650, try reply.get(UInt32.self) == 1 else { throw APAError.message("ApaNetServer 协议不匹配") }
        features = try reply.get(); try reply.finish()
    }
    public func disconnect() { control?.close(); control = nil }
    private func require(_ flags: UInt32) throws {
        guard features & flags == flags else { throw APAError.message("PS2 服务缺少所需功能，请更新 ApaNetServer.ELF") }
    }
    @discardableResult
    func rpc(_ command: UInt32, _ payload: Data = Data(), _ cancel: Cancellation?, timeout: TimeInterval = 30) throws -> Data {
        guard let c = control else { throw APAError.message("尚未连接 PS2") }
        guard payload.count <= 131072 else { throw APAError.message("控制请求过大") }
        var frame = Wire(); frame.put(command); frame.put(UInt32(payload.count)); frame.put(Int32(0))
        var data = frame.bytes; data.append(payload)
        let response: Data
        let result: Int32
        do {
            try c.write(data,cancel:cancel,timeout:timeout)
            var header = Wire(try c.read(12,cancel:cancel,timeout:timeout))
            _ = try header.get(UInt32.self)
            let length = try header.get(UInt32.self); result = try header.get()
            guard length <= 131072 else { throw APAError.message("控制响应过大") }
            response = try c.read(Int(length),cancel:cancel,timeout:timeout)
        } catch { disconnect(); throw error }
        if result != 0 {
            let names: [Int32:String] = [-2:"I/O 错误",-3:"不存在",-4:"已存在",-5:"目录非空",-6:"空间不足",-7:"参数或操作被拒绝",-8:"未挂载",-9:"设备忙",-10:"不支持",-13:"数据连接失败"]
            throw APAError.message("APANet 命令 \(String(command,radix:16))：\(names[result] ?? "失败")（\(result)）")
        }
        return response
    }
    public func mount(_ name: String, _ cancel: Cancellation) throws { try rpc(3,Wire.strings(name),cancel); mounted = name }
    public func unmount(_ cancel: Cancellation) throws { try rpc(4,Data(),cancel); mounted = nil }
    public func layout(_ cancel: Cancellation) throws -> Layout {
        try require(4)
        var w = Wire(try rpc(0x11,Data(),cancel)); let total = try w.get(UInt64.self)
        _ = try w.get(UInt64.self); try w.finish()
        return try Layout.read(total:total,cancel:cancel) { lba,count in
            var p = Wire(); p.put(lba); p.put(UInt32(count))
            return try self.rpc(0x40,p.bytes,cancel)
        }
    }
    public func list(_ path: String, _ cancel: Cancellation) throws -> [Entry] {
        var w = Wire(try rpc(0x20,Wire.strings(path),cancel))
        let count = try w.count(minimumEntryBytes:13)
        var entries = [Entry]()
        for _ in 0..<count {
            let name = try w.string(); try Names.component(name)
            let directory = try w.get(UInt8.self) != 0; let size = try w.get(UInt64.self); let details = try w.string()
            entries.append(Entry(name,directory:directory,size:size,details:details))
        }
        try w.finish(); return entries
    }
    public func space(_ cancel: Cancellation) throws -> Space {
        var w = Wire(try rpc(0x12,Data(),cancel))
        let total = try w.get(UInt64.self), free = try w.get(UInt64.self), zone = try w.get(UInt32.self)
        try w.finish(); guard free <= total, zone > 0 else { throw APAError.message("无效 PFS 容量响应") }
        return Space(total:total,free:free,zone:zone)
    }
    public func mutate(_ command: UInt32, _ values: [String], _ cancel: Cancellation) throws {
        if command >= 0x50 { try require(12) } else { try require(1) }
        var w = Wire(); for s in values { try w.putString(s) }; try rpc(command,w.bytes,cancel)
    }
    public func upload(_ local: URL, parent: String, overwrite: Bool, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws {
        let identity = try local.resourceValues(forKeys:[.fileSizeKey,.contentModificationDateKey,.fileResourceIdentifierKey])
        for attempt in 0..<3 {
            do {
                try uploadAttempt(local,parent:parent,overwrite:overwrite,cancel:cancel,attempt:attempt,progress:progress)
                return
            } catch let retry as RetryUpload {
                try cancel.check()
                let now = try local.resourceValues(forKeys:[.fileSizeKey,.contentModificationDateKey,.fileResourceIdentifierKey])
                guard now.fileSize == identity.fileSize, now.contentModificationDate == identity.contentModificationDate,
                      String(describing:now.fileResourceIdentifier) == String(describing:identity.fileResourceIdentifier) else {
                    _ = try? rpc(0x32,Data(),nil,timeout:8)
                    throw APAError.message("本地文件已变化，已停止续传")
                }
                do {
                    if control != nil { _ = try rpc(2,Data(),cancel) }
                    else { throw retry.underlying }
                } catch {
                    let part = mounted
                    let next = try NetworkStorage(host:host,port:port,cancel:cancel)
                    control = next.control; next.control = nil; features = next.features
                    if let part { try mount(part,cancel) }
                }
            }
        }
    }
    private func uploadAttempt(_ local: URL, parent: String, overwrite: Bool, cancel: Cancellation, attempt: Int, progress: (UInt64,UInt64)->Void) throws {
        try require(2); try Names.component(local.lastPathComponent)
        let file = try FileHandle(forReadingFrom:local); defer { try? file.close() }
        let total = try file.seekToEnd(); try file.seek(toOffset:0)
        let listener = try TCPSocket.listener(); defer { listener.close() }
        var data: TCPSocket?
        var began = false; var committing = false
        do {
            var w = Wire(); try w.putString(parent); try w.putString(local.lastPathComponent); w.put(total); w.put(UInt32(overwrite ? 1 : 0) | (attempt > 0 ? 2 : 0))
            let beginResponse = try rpc(0x30,w.bytes,cancel); began = true
            var offset: UInt64 = 0
            if attempt > 0 {
                var r = Wire(beginResponse); offset = try r.get(); try r.finish()
                guard offset <= total else { throw APAError.message("续传起点越界") }
                try file.seek(toOffset:offset)
            }
            data = try listener.accept(peer:host,cancel:cancel)
            var sent: UInt64 = offset
            progress(sent,total)
            while sent < total {
                try cancel.check()
                guard let b = try file.read(upToCount:Int(min(512*1024,total-sent))), !b.isEmpty else { throw APAError.message("本地文件提前结束") }
                try data!.write(b,cancel:cancel); sent += UInt64(b.count); progress(sent,total)
            }
            _ = shutdown(data!.fd,SHUT_WR); data?.close(); data = nil
            committing = true
            try rpc(0x31,Data(),cancel,timeout:120); progress(total,total)
        } catch {
            data?.close(reset:true)
            if began, !committing, !cancel.isCancelled, attempt < 2, case APAError.network = error {
                throw RetryUpload(underlying:error)
            }
            if began, control != nil { _ = try? rpc(0x32,Data(),nil,timeout:8) }
            throw error
        }
    }
    public func download(_ entry: Entry, parent: String, to destination: URL, cancel: Cancellation, progress: (UInt64,UInt64)->Void) throws {
        try require(2); try Names.component(entry.name)
        let temporary = destination.deletingLastPathComponent().appendingPathComponent(".apanet-\(UUID().uuidString).part")
        guard FileManager.default.createFile(atPath:temporary.path,contents:nil) else { throw APAError.message("无法创建下载临时文件") }
        defer { try? FileManager.default.removeItem(at:temporary) }
        let file = try FileHandle(forWritingTo:temporary); defer { try? file.close() }
        let listener = try TCPSocket.listener(); defer { listener.close() }
        var data: TCPSocket?; var began = false
        do {
            var w = Wire(); try w.putString(parent); try w.putString(entry.name); w.put(UInt64(0)); w.put(UInt64(0))
            var response = Wire(try rpc(0x33,w.bytes,cancel)); began = true
            let total = try response.get(UInt64.self); try response.finish()
            guard total == entry.size else { throw APAError.message("远端文件大小已变化，请刷新后重试") }
            data = try listener.accept(peer:host,cancel:cancel)
            var received: UInt64 = 0
            while received < total {
                let b = try data!.read(Int(min(512*1024,total-received)),cancel:cancel,timeout:120)
                try file.write(contentsOf:b); received += UInt64(b.count); progress(received,total)
            }
            data?.close(); data = nil
            try rpc(0x34,Data(),cancel,timeout:120)
            try file.synchronize(); try file.close(); try cancel.check()
            guard Darwin.rename(temporary.path,destination.path) == 0 else { throw TCPSocket.failure("保存下载失败") }
            progress(total,total)
        } catch {
            data?.close(reset:true)
            if began, control != nil { _ = try? rpc(0x35,Data(),nil,timeout:8) }
            throw error
        }
    }
}
