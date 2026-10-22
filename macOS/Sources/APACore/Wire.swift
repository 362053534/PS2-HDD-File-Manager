import Foundation

public enum APAError: Error, LocalizedError {
    case message(String), network(String), cancelled
    public var errorDescription: String? {
        switch self { case .message(let s), .network(let s): return s; case .cancelled: return "操作已取消" }
    }
}
public final class Cancellation {
    private let lock = NSLock()
    private var value = false
    public init() {}
    public func cancel() { lock.lock(); value = true; lock.unlock() }
    public var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return value }
    public func check() throws { if isCancelled { throw APAError.cancelled } }
}
public struct Wire {
    public private(set) var bytes: Data
    private var offset = 0
    public init(_ data: Data = Data()) { bytes = data }
    public mutating func put<T: FixedWidthInteger>(_ value: T) {
        var v = value.littleEndian
        withUnsafeBytes(of: &v) { bytes.append(contentsOf: $0) }
    }
    public mutating func putString(_ value: String) throws {
        let data = Data(value.utf8)
        guard data.count <= 65535, !data.contains(0) else { throw APAError.message("名称或路径无效") }
        put(UInt16(data.count)); bytes.append(data)
    }
    public mutating func get<T: FixedWidthInteger>(_ type: T.Type = T.self) throws -> T {
        let n = MemoryLayout<T>.size
        guard offset <= bytes.count - n else { throw APAError.message("响应数据被截断") }
        var v: T = 0
        for i in 0..<n { v |= T(truncatingIfNeeded: bytes[offset+i]) << (i*8) }
        offset += n
        return v
    }
    public mutating func string() throws -> String {
        let n = Int(try get(UInt16.self))
        guard offset <= bytes.count - n,
              let s = String(data: bytes.subdata(in: offset..<offset+n), encoding: .utf8),
              !s.utf8.contains(0) else { throw APAError.message("无效 UTF-8 响应") }
        offset += n; return s
    }
    public mutating func count(minimumEntryBytes: Int) throws -> Int {
        let n = Int(try get(UInt32.self))
        guard n <= (bytes.count - offset) / minimumEntryBytes else { throw APAError.message("响应条目数量无效") }
        return n
    }
    public func finish() throws {
        guard offset == bytes.count else { throw APAError.message("响应包含未知数据") }
    }
    public static func strings(_ values: String...) throws -> Data {
        var w = Wire(); for s in values { try w.putString(s) }; return w.bytes
    }
}
public struct Entry: Identifiable, Hashable {
    public var id: String { name }
    public let name: String
    public let directory: Bool
    public let size: UInt64
    public init(_ name: String, directory: Bool, size: UInt64 = 0) {
        self.name = name; self.directory = directory; self.size = size
    }
}
public struct Space {
    public let total: UInt64
    public let free: UInt64
    public let zone: UInt32
    public init(total: UInt64, free: UInt64, zone: UInt32 = 8192) { self.total = total; self.free = free; self.zone = zone }
}
public enum Names {
    public static func component(_ name: String, maxBytes: Int = 255) throws {
        guard !name.isEmpty, name != ".", name != "..", !name.contains("/"),
              !name.contains("\\"), !name.utf8.contains(0),
              !name.contains("\n"), !name.contains("\r"), name.utf8.count <= maxBytes else {
            throw APAError.message("无效的名称：\(name)")
        }
    }
    public static func path(_ parent: String, _ name: String) throws -> String {
        try component(name)
        return parent == "/" ? "/" + name : parent + "/" + name
    }
    public static func partition(_ name: String) throws {
        try component(name, maxBytes: 31)
        guard !["__mbr", "__net", "__system", "__sysconf", "__common", "__boot"].contains(name) else {
            throw APAError.message("不能修改系统保留分区")
        }
    }
    public static func size(_ value: String) throws -> UInt64 {
        let s = value.uppercased()
        guard let unit = s.last, unit == "M" || unit == "G", let n = UInt64(s.dropLast()), n > 0,
              n <= UInt64(UInt32.max) / (unit == "G" ? 1024 : 1) else { throw APAError.message("容量格式为 128M、1G 等") }
        let mb = n * (unit == "G" ? 1024 : 1)
        guard mb % 128 == 0 else { throw APAError.message("容量必须为 128 MiB 的整数倍") }
        return mb * 1024 * 1024
    }
}
