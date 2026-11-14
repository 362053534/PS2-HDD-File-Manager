import Foundation
import Darwin

// One in-flight command. No queued batch writes, prompt matching, or forced termination.
final class PfsShellSession {
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private let prefix = "__APA_NATIVE_SESSION_V1__" + UUID().uuidString + ":"
    private var pending = Data()
    private var closed = false
    private var failed = false

    init(tool: URL) throws {
        // Refuse an old cached tool before starting a process that cannot reply.
        guard try Data(contentsOf:tool,options:.mappedIfSafe).range(of:Data("APA_NATIVE_SESSION".utf8)) != nil else {
            throw APAError.message("pfsshell 不支持安全共享会话，请重新构建 macOS 工具。")
        }
        process.executableURL = tool
        var env = ProcessInfo.processInfo.environment
        env["APA_NATIVE_SESSION"] = String(prefix.dropFirst("__APA_NATIVE_SESSION_V1__".count).dropLast())
        process.environment = env
        process.standardInput = input
        process.standardOutput = output
        process.standardError = output
        try process.run()
        // Parent must not keep a writer alive when the child exits.
        try? output.fileHandleForWriting.close()
        try? input.fileHandleForReading.close()
        _ = fcntl(input.fileHandleForWriting.fileDescriptor,F_SETNOSIGPIPE,1)
        do {
            let (_,result) = try reply()
            guard result == "READY" else { throw APAError.message("pfsshell 共享会话握手失败") }
        } catch { try? finish(); throw error }
    }

    deinit { try? finish() }

    private func line() throws -> String {
        while true {
            if let end = pending.firstIndex(of:10) {
                let value = String(decoding:pending[..<end],as:UTF8.self)
                pending.removeSubrange(...end)
                return value
            }
            let bytes = output.fileHandleForReading.availableData
            guard !bytes.isEmpty else { throw APAError.message("pfsshell 会话意外结束；已停止导入。"+String(decoding:pending,as:UTF8.self)) }
            pending.append(bytes)
        }
    }

    private func reply(progress: (UInt64)->Void = { _ in }) throws -> (String,String) {
        var text = ""
        var byteCount = 0
        while true {
            let value: String
            do { value = try line() }
            catch { throw APAError.message(text+"\n"+error.localizedDescription) }
            if value.hasPrefix(prefix) { return (text,String(value.dropFirst(prefix.count))) }
            if let range = value.range(of:"__PFS_PROGRESS__:"),
               let bytes = UInt64(value[range.upperBound...].trimmingCharacters(in:.whitespacesAndNewlines)) {
                progress(bytes)
            }
            // Drain even excessive output before reporting an error, so the child cannot block.
            byteCount += value.utf8.count+1
            guard byteCount < 64*1024*1024 else {
                throw APAError.message("pfsshell 响应过大；已停止导入。")
            }
            text += value+"\n"
        }
    }

    private static func hasError(_ text: String) -> Bool {
        text.contains("(!)") || text.contains("read/write: atad") || text.contains("pfs: Error:")
    }

    func command(_ command: String, cancel: Cancellation, progress: (UInt64)->Void = { _ in }) throws -> String {
        try cancel.check()
        guard !closed, !failed else { throw APAError.message("磁盘会话已失败，已拒绝后续写入。") }
        guard !command.contains("\n"), !command.contains("\r") else { throw APAError.message("共享会话仅接受单条命令") }
        do {
            try input.fileHandleForWriting.write(contentsOf:Data((command+"\n").utf8))
            let (text,result) = try reply(progress:progress)
            guard result == "0", !Self.hasError(text) else { throw APAError.message(text+"\n命令执行失败，已停止导入（"+result+"）。") }
            return text
        } catch {
            failed = true
            throw error
        }
    }

    // EOF lets pfsshell unmount normally, including after a failed command.
    // Never send a queued rm/rename/put or kill a process that may be writing.
    func finish() throws {
        guard !closed else { return }
        closed = true
        try? input.fileHandleForWriting.close()
        var tail = pending; pending.removeAll()
        while true {
            let bytes = output.fileHandleForReading.availableData
            if bytes.isEmpty { break }
            if tail.count < 1024*1024 { tail.append(bytes) }
        }
        process.waitUntilExit()
        try? output.fileHandleForReading.close()
        let text = String(decoding:tail,as:UTF8.self)
        guard process.terminationStatus == 0, !Self.hasError(text) else {
            throw APAError.message("pfsshell 卸载或退出失败，请停止写入并检查磁盘。\n"+text)
        }
    }
}
