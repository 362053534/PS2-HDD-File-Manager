import Foundation
import Darwin

extension CoreTests {
    func testSharedImportRejectsFaultBeforeNextFile() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("apa-shared-fault-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:root) }
        let image = root.appendingPathComponent("fixture.img")
        try header().write(to:image)
        let file = try FileHandle(forWritingTo:image)
        try file.truncate(atOffset:256*1024*1024); try file.close()
        let first = root.appendingPathComponent("first.iso"), second = root.appendingPathComponent("second.iso")
        try Data([1,2,3,4]).write(to:first); try Data([5,6,7,8]).write(to:second)
        for mode in ["marker","nonzero","exit","cd","size","rename","sync"] {
            let tool = root.appendingPathComponent(mode+".sh")
            let trace = root.appendingPathComponent(mode+".trace")
            let script = #"""
#!/bin/sh
# APA_NATIVE_SESSION: deliberately exercise the real framed client with a faulty backend.
token="__APA_NATIVE_SESSION_V1__""$APA_NATIVE_SESSION":
printf '\n%sREADY\n' "$token"
while IFS= read -r command; do
    printf '%s\n' "$command" >> "\#(trace.path)"
    case "$command" in
        ls*) if [ "\#(mode)" = size ] || [ "\#(mode)" = rename ] || [ "\#(mode)" = sync ]; then
            printf '%s\n' '-rw-rw-rw- 4 2026-11-14 00:00 first.iso'
            if [ -n "$staging" ]; then
                size=4; if [ "\#(mode)" = size ]; then size=2; fi
                printf '%s %s %s %s\n' '-rw-rw-rw-' "$size" '2026-11-14 00:00' "$staging"
            fi
        fi;;
        rename*)
            case "$command" in *'"first.iso"')
                if [ "\#(mode)" = rename ]; then printf '(!) rename: Input/output error.\n\n%s-5\n' "$token"; continue; fi;;
            esac;;
        apa_sync*) if [ "\#(mode)" = sync ]; then printf '(!) sync: Input/output error.\n\n%s-5\n' "$token"; continue; fi;;
        cd*) if [ "\#(mode)" = cd ]; then printf '(!) cd: Input/output error.\n\n%s-5\n' "$token"; continue; fi;;
        put*)
            if [ "\#(mode)" = size ] || [ "\#(mode)" = rename ] || [ "\#(mode)" = sync ]; then
                staging=$(printf '%s' "$command" | awk -F '"' '{print $(NF-1)}')
                printf '\n%s0\n' "$token"; continue
            fi
            if [ "\#(mode)" = exit ]; then echo 'read/write: atad device fd 3: Permission denied'; exit 1; fi
            if [ "\#(mode)" = marker ]; then printf '(!) Input/output error.\n\n%s0\n' "$token"; continue; fi
            printf 'read/write: atad device fd 3: Permission denied\n\n%s-5\n' "$token"; continue;;
    esac
    printf '\n%s0\n' "$token"
done

"""#
            try script.write(to:tool,atomically:true,encoding:.utf8)
            try FileManager.default.setAttributes([.posixPermissions:0o700],ofItemAtPath:tool.path)
            let local = try LocalStorage(device:image,tool:tool), c = Cancellation()
            try local.mount("+OPL",c)
            var reachedSecond = false, completed = false
            var diagnostic = ""
            do { try local.withImportSession(c) {
                try local.upload(first,parent:"/DVD",overwrite:true,cancel:c) { done,total in completed = done == total }
                reachedSecond = true
                try local.upload(second,parent:"/DVD",overwrite:true,cancel:c) { _,_ in }
            }; XCTFail("fault was accepted") }
            catch { diagnostic = error.localizedDescription }
            if mode == "rename" || mode == "sync" { XCTAssertTrue(diagnostic.contains("原文件备份")) }
            if mode == "exit" { XCTAssertTrue(diagnostic.contains("Permission denied")) }
            let commands = try String(contentsOf:trace,encoding:.utf8)
            XCTAssertTrue(!reachedSecond); XCTAssertTrue(!completed)
            let renames = commands.components(separatedBy:"\n").filter { $0.hasPrefix("rename ") }.count
            XCTAssertEqual(renames,mode == "rename" || mode == "sync" ? 2:0)
            if mode != "size" { XCTAssertTrue(!commands.contains("rm ")) }
            XCTAssertEqual(commands.components(separatedBy:"\n").filter { $0.hasPrefix("put ") }.count,mode == "cd" ? 0:1)
        }
    }

    func testSharedImportPersistsFilesAndProtectsCancelledOverwrite() throws {
        guard let executable = ProcessInfo.processInfo.environment["PFSSHELL_TEST_BIN"] else { throw CheckSkip.unavailable("set PFSSHELL_TEST_BIN") }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("apa-shared-real-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:root) }
        let image = root.appendingPathComponent("fixture.img"), tool = URL(fileURLWithPath:executable)
        XCTAssertTrue(FileManager.default.createFile(atPath:image.path,contents:nil))
        let file = try FileHandle(forWritingTo:image); try file.truncate(atOffset:1024*1024*1024); try file.close()
        _ = try NativeProcess.run(tool,[],input:"device \"\(image.path)\"\ninitialize yes\nexit\n")
        let local = try LocalStorage(device:image,tool:tool), c = Cancellation()
        try PartitionOperations.apply(0x50,values:["+Shared","128M"],storage:local,cancel:c)
        try local.mount("+Shared",c)
        let source = root.appendingPathComponent("游戏 中文.iso"), second = root.appendingPathComponent("second.iso")
        let original = Data(repeating:17,count:1_100_003), replacement = Data(repeating:31,count:730_007)
        try original.write(to:source); try Data([8,9]).write(to:second)
        try local.withImportSession(c) {
            try local.mutate(0x21,["/","DVD"],c)
            try local.upload(source,parent:"/DVD",overwrite:false,cancel:c) { _,_ in }
            try local.upload(second,parent:"/",overwrite:false,cancel:c) { _,_ in }
            try replacement.write(to:source)
            try local.upload(source,parent:"/DVD",overwrite:true,cancel:c) { _,_ in }
        }
        let after = try XCTUnwrap(local.list("/DVD",c).first { $0.name == source.lastPathComponent })
        let output = root.appendingPathComponent("readback.iso")
        try local.download(after,parent:"/DVD",to:output,cancel:c) { _,_ in }
        XCTAssertEqual(try Data(contentsOf:output),replacement)
        XCTAssertEqual(try local.list("/",c).first { $0.name == "second.iso" }?.size,2)
        try original.write(to:source)
        let cancel = Cancellation()
        XCTAssertThrowsError(try local.withImportSession(cancel) {
            try local.upload(source,parent:"/DVD",overwrite:true,cancel:cancel) { done,_ in if done > 0 { cancel.cancel() } }
            XCTFail("cancelled import reached the next file")
        })
        let preserved = try XCTUnwrap(local.list("/DVD",c).first { $0.name == source.lastPathComponent })
        try local.download(preserved,parent:"/DVD",to:output,cancel:c) { _,_ in }
        XCTAssertEqual(try Data(contentsOf:output),replacement)
        XCTAssertTrue(try !local.list("/DVD",c).contains { $0.name.hasPrefix(".apanet_") || $0.name.hasPrefix(".apabackup_") })
    }
}
