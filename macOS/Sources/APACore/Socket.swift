import Foundation
import Darwin

// 所有传输在单一工作队列中执行。非阻塞 socket + poll 让取消无需跨线程关闭/复用 fd。
final class TCPSocket {
    private(set) var fd: Int32
    init(fd: Int32) throws {
        guard fd >= 0 else { throw Self.failure("socket") }
        self.fd = fd
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
        set(SOL_SOCKET, SO_NOSIGPIPE, 1)
        set(SOL_SOCKET, SO_KEEPALIVE, 1)
        set(IPPROTO_TCP, TCP_NODELAY, 1)
        set(IPPROTO_TCP, TCP_KEEPALIVE, 60)
        set(IPPROTO_TCP, TCP_KEEPINTVL, 15)
        set(IPPROTO_TCP, TCP_KEEPCNT, 10)
        set(SOL_SOCKET, SO_SNDBUF, 512 * 1024)
        set(SOL_SOCKET, SO_RCVBUF, 512 * 1024)
    }
    deinit { close() }
    func set(_ level: Int32, _ option: Int32, _ value: Int32) {
        var v = value; _ = setsockopt(fd, level, option, &v, socklen_t(MemoryLayout.size(ofValue: v)))
    }
    func close(reset: Bool = false) {
        if fd < 0 { return }
        if reset { var l = linger(l_onoff: 1, l_linger: 0); _ = setsockopt(fd, SOL_SOCKET, SO_LINGER, &l, socklen_t(MemoryLayout.size(ofValue: l))) }
        Darwin.close(fd); fd = -1
    }
    static func failure(_ context: String) -> APAError { .network("\(context)：\(String(cString: strerror(errno)))") }
    func ready(_ events: Int16, until deadline: Date, cancel: Cancellation?) throws {
        while true {
            try cancel?.check()
            guard Date() < deadline else { throw APAError.network("网络等待超时") }
            var p = pollfd(fd: fd, events: events, revents: 0)
            let r = poll(&p, 1, 200)
            if r < 0 { if errno == EINTR { continue }; throw Self.failure("poll") }
            if r > 0 { return } // 随后的 recv/send 获取 EOF 或具体错误。
        }
    }
    static func connect(_ host: String, port: UInt16 = 45061, cancel: Cancellation?) throws -> TCPSocket {
        let s = try TCPSocket(fd: socket(AF_INET, SOCK_STREAM, 0))
        var a = sockaddr_in(); a.sin_len = UInt8(MemoryLayout.size(ofValue: a)); a.sin_family = sa_family_t(AF_INET); a.sin_port = port.bigEndian
        guard host.withCString({ inet_pton(AF_INET, $0, &a.sin_addr) }) == 1 else { throw APAError.message("请输入 PS2 的 IPv4 地址") }
        let result = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(s.fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        if result < 0 {
            guard errno == EINPROGRESS else { throw failure("连接失败") }
            try s.ready(Int16(POLLOUT), until: Date().addingTimeInterval(15), cancel: cancel)
            var e: Int32 = 0; var len = socklen_t(MemoryLayout.size(ofValue: e))
            guard getsockopt(s.fd, SOL_SOCKET, SO_ERROR, &e, &len) == 0, e == 0 else { throw APAError.message("连接失败：\(String(cString: strerror(e)))") }
        }
        return s
    }
    static func listener() throws -> TCPSocket {
        let s = try TCPSocket(fd: socket(AF_INET, SOCK_STREAM, 0)); s.set(SOL_SOCKET, SO_REUSEADDR, 1)
        var a = sockaddr_in(); a.sin_len = UInt8(MemoryLayout.size(ofValue: a)); a.sin_family = sa_family_t(AF_INET); a.sin_port = UInt16(45062).bigEndian
        let r = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(s.fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        guard r == 0, listen(s.fd, 4) == 0 else { throw failure("无法监听 TCP 45062") }
        return s
    }
    func accept(peer host: String, cancel: Cancellation?) throws -> TCPSocket {
        let deadline = Date().addingTimeInterval(30)
        while true {
            try ready(Int16(POLLIN), until: deadline, cancel: cancel)
            var a = sockaddr_in(); var len = socklen_t(MemoryLayout.size(ofValue: a))
            let n = withUnsafeMutablePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.accept(fd, $0, &len) } }
            if n < 0 { if errno == EAGAIN || errno == EINTR { continue }; throw Self.failure("数据连接失败") }
            let s = try TCPSocket(fd: n)
            var expected = in_addr(); _ = host.withCString { inet_pton(AF_INET, $0, &expected) }
            if a.sin_addr.s_addr == expected.s_addr { return s }
            s.close()
        }
    }
    func write(_ data: Data, cancel: Cancellation?, timeout: TimeInterval = 120) throws {
        try data.withUnsafeBytes { raw in
            var offset = 0; var deadline = Date().addingTimeInterval(timeout)
            while offset < data.count {
                try ready(Int16(POLLOUT), until: deadline, cancel: cancel)
                let n = send(fd, raw.baseAddress!.advanced(by: offset), data.count-offset, 0)
                if n < 0 { if errno == EAGAIN || errno == EINTR { continue }; throw Self.failure("发送失败") }
                guard n > 0 else { throw APAError.network("连接已关闭") }
                offset += n; deadline = Date().addingTimeInterval(timeout)
            }
        }
    }
    func read(_ count: Int, cancel: Cancellation?, timeout: TimeInterval = 30) throws -> Data {
        var result = Data(count: count)
        try result.withUnsafeMutableBytes { raw in
            var offset = 0; var deadline = Date().addingTimeInterval(timeout)
            while offset < count {
                try ready(Int16(POLLIN), until: deadline, cancel: cancel)
                let n = recv(fd, raw.baseAddress!.advanced(by: offset), count-offset, 0)
                if n < 0 { if errno == EAGAIN || errno == EINTR { continue }; throw Self.failure("接收失败") }
                guard n > 0 else { throw APAError.network("连接提前关闭") }
                offset += n; deadline = Date().addingTimeInterval(timeout)
            }
        }
        return result
    }
}
