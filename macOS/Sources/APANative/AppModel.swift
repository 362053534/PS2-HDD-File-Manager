import AppKit
import SwiftUI
import APACore

final class AppModel: ObservableObject {
    @Published var host = "192.168.100.111"
    @Published var entries = [Entry]()
    @Published var selected = Set<String>()
    @Published var partition: String?
    @Published var path = "/"
    @Published var source = "未连接"
    @Published var status = "连接 PS2 或打开 APA 硬盘镜像"
    @Published var log = ""
    @Published var busy = false
    @Published var fraction: Double = 0
    @Published var capacity = ""
    @Published var failure: String?
    private let queue = DispatchQueue(label:"com.fengge.apa.storage",qos:.userInitiated)
    private var storage: Storage?
    private var token = Cancellation()
    private var activity: NSObjectProtocol?
    private var progressAt = Date.distantPast
    private var started = Date()

    private func append(_ text: String) {
        log += "[\(Date().formatted(date:.omitted,time:.standard))] \(text)\n"
        if log.count > 60000 { log = String(log.suffix(50000)) }
    }
    func operation(_ title: String, _ work: @escaping (Cancellation) throws -> Void) {
        guard !busy else { return }
        busy = true; status = title; fraction = 0; token = Cancellation(); started = Date()
        let current = token
        activity = ProcessInfo.processInfo.beginActivity(options:[.userInitiated,.idleSystemSleepDisabled],reason:"APA 文件传输或磁盘操作")
        append(title)
        queue.async {
            var errorText: String?
            do { try work(current) } catch { errorText = error.localizedDescription }
            DispatchQueue.main.async {
                self.busy = false
                if let a = self.activity { ProcessInfo.processInfo.endActivity(a); self.activity = nil }
                if let text = errorText {
                    self.status = text; self.append(text)
                    if !current.isCancelled { self.failure = text }
                } else { self.status = "操作完成"; self.append("操作完成") }
            }
        }
    }
    func cancel() {
        token.cancel(); status = "正在取消（本地写入需完成当前文件并卸载）…"
    }
    private func backend() throws -> Storage {
        guard let storage else { throw APAError.message("请先连接 PS2 或打开本地硬盘") }; return storage
    }
    private func publish(_ entries: [Entry], partition: String?, path: String, space: Space) {
        DispatchQueue.main.async {
            self.entries = entries.sorted { a,b in a.directory == b.directory ? a.name.localizedStandardCompare(b.name) == .orderedAscending : a.directory }
            self.partition = partition; self.path = path; self.selected = []
            self.capacity = "总量 \(Self.bytes(space.total)) · 可用 \(Self.bytes(space.free))"
        }
    }
    private func showPartitions(_ c: Cancellation) throws {
        let s = try backend(); try s.unmount(c); let layout = try s.layout(c)
        publish(layout.browsable,partition:nil,path:"/",space:Space(total:layout.total,free:layout.free))
    }
    private func showDirectory(_ part: String, _ path: String, _ c: Cancellation) throws {
        let s = try backend(); try s.mount(part,c)
        let entries = try s.list(path,c); let space = try s.space(c)
        publish(entries,partition:part,path:path,space:space)
    }
    func connect() {
        let ip = host.trimmingCharacters(in:.whitespacesAndNewlines)
        entries = []; selected = []; partition = nil; path = "/"; capacity = ""
        operation("正在连接 APANetServer…") { c in
            self.storage?.disconnect(); self.storage = nil
            self.storage = try NetworkStorage(host:ip,cancel:c)
            DispatchQueue.main.async { self.source = "PS2 · \(ip)" }
            try self.showPartitions(c)
        }
    }
    func open(_ url: URL) {
        let tool = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/tools/pfsshell")
        operation("正在打开 \(url.lastPathComponent)…") { c in
            let next = try LocalStorage(device:url,tool:tool)
            self.storage?.disconnect(); self.storage = next
            DispatchQueue.main.async { self.source = url.path }
            try self.showPartitions(c)
        }
    }
    func chooseImage() {
        let p = NSOpenPanel(); p.canChooseDirectories = false; p.allowsMultipleSelection = false
        p.message = "选择完整的 PS2 APA 硬盘镜像（不会格式化）"
        if p.runModal() == .OK, let url = p.url { open(url) }
    }
    func chooseDisk() {
        operation("正在枚举外接硬盘…") { _ in
            let disks = try NativeProcess.disks()
            DispatchQueue.main.async {
                let alert = NSAlert(); alert.messageText = "打开 PS2 APA 硬盘"
                alert.informativeText = "只接受外接 APA 整盘。需要当前用户具有设备读写权限；程序不会更改系统权限。"
                let menu = NSPopUpButton(frame:NSRect(x:0,y:0,width:420,height:26))
                for d in disks { menu.addItem(withTitle:d.1) }
                alert.accessoryView = menu; alert.addButton(withTitle:"打开"); alert.addButton(withTitle:"取消")
                // 枚举操作的完成回调先结束 busy，再打开所选设备。
                DispatchQueue.main.async {
                    if alert.runModal() == .alertFirstButtonReturn, disks.indices.contains(menu.indexOfSelectedItem) {
                        self.open(URL(fileURLWithPath:disks[menu.indexOfSelectedItem].0))
                    }
                }
            }
        }
    }
    func disconnect() {
        operation("断开连接") { _ in
            self.storage?.disconnect(); self.storage = nil
            DispatchQueue.main.async { self.source = "未连接"; self.entries = []; self.selected = []; self.partition = nil; self.capacity = "" }
        }
    }
    func eject() {
        let device = source
        guard device.hasPrefix("/dev/disk") else { failure = "只有本地外接硬盘可以弹出"; return }
        operation("正在安全弹出…") { _ in
            self.storage?.disconnect(); self.storage = nil
            _ = try NativeProcess.run(URL(fileURLWithPath:"/usr/sbin/diskutil"),["eject",device])
            DispatchQueue.main.async { self.source = "未连接"; self.entries = []; self.partition = nil }
        }
    }
    func refresh() {
        let p = partition, folder = path
        operation("刷新") { c in
            if let p { try self.showDirectory(p,folder,c) } else { try self.showPartitions(c) }
        }
    }
    func navigate(_ e: Entry) {
        guard e.directory else { return }
        let p = partition, folder = path
        operation("打开 \(e.name)") { c in
            if let p { try self.showDirectory(p,Names.path(folder,e.name),c) }
            else { try self.showDirectory(e.name,"/",c) }
        }
    }
    func openSelected() { if selected.count == 1, let e = entries.first(where:{ selected.contains($0.name) }) { navigate(e) } }
    func up() {
        guard let p = partition else { return }
        let current = path
        operation("返回上级") { c in
            if current == "/" { try self.showPartitions(c) }
            else { let parent = (current as NSString).deletingLastPathComponent; try self.showDirectory(p,parent.isEmpty ? "/" : parent,c) }
        }
    }
    func prompt(_ title: String, initial: String = "") -> String? {
        let alert = NSAlert(); alert.messageText = title
        let field = NSTextField(string:initial); field.frame = NSRect(x:0,y:0,width:360,height:26)
        alert.accessoryView = field; alert.addButton(withTitle:"确定"); alert.addButton(withTitle:"取消")
        alert.window.initialFirstResponder = field
        return alert.runModal() == .alertFirstButtonReturn ? field.stringValue : nil
    }
    func confirm(_ title: String, _ message: String) -> Bool {
        let alert = NSAlert(); alert.alertStyle = .warning; alert.messageText = title; alert.informativeText = message
        alert.addButton(withTitle:"继续"); alert.addButton(withTitle:"取消")
        return alert.runModal() == .alertFirstButtonReturn
    }
    func createDirectory() {
        guard let p = partition, let name = prompt("新建目录") else { return }
        let folder = path
        operation("新建目录 \(name)") { c in
            try Names.component(name); let s = try self.backend(); try s.mount(p,c)
            try s.mutate(0x21,[folder,name],c); try self.showDirectory(p,folder,c)
        }
    }
    func rename() {
        guard selected.count == 1, let name = selected.first, let newName = prompt("重命名",initial:name), newName != name else { return }
        let p = partition, folder = path
        operation("重命名 \(name)") { c in
            try Names.component(newName); let s = try self.backend()
            if let p { try s.mount(p,c); try s.mutate(0x24,[folder,name,newName],c); try self.showDirectory(p,folder,c) }
            else { try PartitionOperations.apply(0x52,values:[name,newName],storage:s,cancel:c); try self.showPartitions(c) }
        }
    }
    func createPartition() {
        guard let name = prompt("新建 PFS 分区名称",initial:"+"), let size = prompt("容量（128 MiB 的整数倍，例如 1G）") else { return }
        guard confirm("创建 PFS 分区", "将在 APA 未分配区域创建并格式化 \(name)，容量 \(size)。") else { return }
        operation("创建分区 \(name)") { c in
            try PartitionOperations.apply(0x50,values:[name,size],storage:self.backend(),cancel:c); try self.showPartitions(c)
        }
    }
    func deleteSelected() {
        let items = entries.filter { selected.contains($0.name) }, p = partition, folder = path
        guard !items.isEmpty, confirm("永久删除 \(items.count) 项？",items.map(\.name).joined(separator:"\n")+"\n此操作无法撤销，目录将递归删除。") else { return }
        operation("删除所选项目") { c in
            let s = try self.backend()
            if let p {
                try s.mount(p,c)
                for e in items { try self.delete(e,parent:folder,storage:s,cancel:c,depth:0) }
                try self.showDirectory(p,folder,c)
            } else {
                for e in items { try PartitionOperations.apply(0x51,values:[e.name],storage:s,cancel:c) }
                try self.showPartitions(c)
            }
        }
    }
    private func delete(_ e: Entry, parent: String, storage: Storage, cancel: Cancellation, depth: Int) throws {
        try cancel.check(); guard depth < 128 else { throw APAError.message("目录嵌套过深") }
        if e.directory {
            let folder = try Names.path(parent,e.name)
            for child in try storage.list(folder,cancel) { try delete(child,parent:folder,storage:storage,cancel:cancel,depth:depth+1) }
        }
        try storage.mutate(e.directory ? 0x22:0x23,[parent,e.name],cancel)
    }
    static func bytes(_ n: UInt64) -> String { ByteCountFormatter.string(fromByteCount:Int64(clamping:n),countStyle:.binary) }
    private func progress(_ name: String, _ done: UInt64, _ total: UInt64) {
        guard Date().timeIntervalSince(progressAt) > 0.15 || done == total else { return }
        progressAt = Date()
        DispatchQueue.main.async {
            self.fraction = total == 0 ? 1 : Double(done)/Double(total)
            self.status = "\(name) · \(Self.bytes(done)) / \(Self.bytes(total))"
        }
    }
    func chooseImport() {
        let p = NSOpenPanel(); p.canChooseFiles = true; p.canChooseDirectories = true; p.allowsMultipleSelection = true
        if p.runModal() == .OK { importURLs(p.urls) }
    }
    func paste() {
        if let urls = NSPasteboard.general.readObjects(forClasses:[NSURL.self],options:[.urlReadingFileURLsOnly:true]) as? [URL] { importURLs(urls) }
    }
    func importURLs(_ urls: [URL]) {
        guard !busy, let part = partition, !urls.isEmpty else { return }
        let folder = path
        // 默认拒绝同名覆盖；用户可明确选择替换网络模式中的同名文件。
        let alert = NSAlert(); alert.messageText = "导入 \(urls.count) 项"
        alert.informativeText = "目录会合并。同名文件如何处理？选择替换将覆盖旧内容。"
        alert.addButton(withTitle:"遇到同名停止"); alert.addButton(withTitle:"替换同名文件"); alert.addButton(withTitle:"取消")
        let answer = alert.runModal(); if answer == .alertThirdButtonReturn { return }
        let overwrite = answer == .alertSecondButtonReturn
        operation("导入文件") { c in
            let s = try self.backend(); try s.mount(part,c)
            var plan = [(URL,String,Bool,UInt64)](); var needed: UInt64 = 0
            func scan(_ url: URL, _ parent: String, _ depth: Int) throws {
                try c.check(); guard depth < 128 else { throw APAError.message("目录嵌套过深") }
                let v = try url.resourceValues(forKeys:[.isSymbolicLinkKey,.isDirectoryKey,.isRegularFileKey,.fileSizeKey])
                guard v.isSymbolicLink != true else { throw APAError.message("不导入符号链接：\(url.lastPathComponent)") }
                try Names.component(url.lastPathComponent)
                if v.isDirectory == true {
                    plan.append((url,parent,true,0))
                    let remote = try Names.path(parent,url.lastPathComponent)
                    for child in try FileManager.default.contentsOfDirectory(at:url,includingPropertiesForKeys:nil).sorted(by: { $0.path < $1.path }) { try scan(child,remote,depth+1) }
                } else {
                    guard v.isRegularFile == true else { throw APAError.message("仅支持普通文件") }
                    let size = UInt64(v.fileSize ?? 0); plan.append((url,parent,false,size))
                }
            }
            for url in urls { try scan(url,folder,0) }
            let space = try s.space(c)
            let zone = UInt64(space.zone)
            for item in plan { needed += item.2 ? zone : ((item.3+zone-1)/zone)*zone }
            guard needed <= space.free else { throw APAError.message("可用空间不足：保守预检需要 \(Self.bytes(needed))") }
            var known = [String:[Entry]](); var newFolders = Set<String>()
            for (url,parent,directory,_) in plan {
                if known[parent] == nil { known[parent] = newFolders.contains(parent) ? [] : try s.list(parent,c) }
                let existing = known[parent]!.first { $0.name == url.lastPathComponent }
                if let existing {
                    guard existing.directory == directory, directory || overwrite else { throw APAError.message("导入冲突：\(url.lastPathComponent)") }
                } else if directory { newFolders.insert(try Names.path(parent,url.lastPathComponent)) }
                known[parent]!.append(Entry(url.lastPathComponent,directory:directory))
            }
            for (url,parent,directory,_) in plan {
                try c.check()
                let existing = try s.list(parent,c).first { $0.name == url.lastPathComponent }
                if directory {
                    if let existing { guard existing.directory else { throw APAError.message("目录与文件重名") } }
                    else { try s.mutate(0x21,[parent,url.lastPathComponent],c) }
                } else {
                    if let existing { guard overwrite && !existing.directory else { throw APAError.message("目标已存在：\(url.lastPathComponent)") } }
                    try s.upload(url,parent:parent,overwrite:overwrite,cancel:c) { self.progress(url.lastPathComponent,$0,$1) }
                }
            }
            try self.showDirectory(part,folder,c)
        }
    }
    func exportSelected() {
        let items = entries.filter { selected.contains($0.name) }
        guard let part = partition, !items.isEmpty else { return }
        let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let destination = panel.url else { return }
        let folder = path
        operation("导出文件") { c in
            let s = try self.backend(); try s.mount(part,c)
            for entry in items { try self.export(entry,folder,destination,0,storage:s,cancel:c) }
        }
    }
    private func export(_ entry: Entry, _ remote: String, _ local: URL, _ depth: Int, storage s: Storage, cancel c: Cancellation) throws {
                try c.check(); guard depth < 128 else { throw APAError.message("目录嵌套过深") }; try Names.component(entry.name)
                let target = local.appendingPathComponent(entry.name)
                guard !FileManager.default.fileExists(atPath:target.path) else { throw APAError.message("导出目标已存在：\(target.lastPathComponent)。请选择空目录。") }
                if entry.directory {
                    try FileManager.default.createDirectory(at:target,withIntermediateDirectories:false)
                    for child in try s.list(Names.path(remote,entry.name),c) { try export(child,Names.path(remote,entry.name),target,depth+1,storage:s,cancel:c) }
                } else { try s.download(entry,parent:remote,to:target,cancel:c) { self.progress(entry.name,$0,$1) } }
            }

    func copySelected() {
        let items = entries.filter { selected.contains($0.name) }
        guard let part = partition, !items.isEmpty else { return }
        let folder = path
        operation("准备文件剪贴板") { c in
            let s = try self.backend(); try s.mount(part,c)
            let root = FileManager.default.urls(for:.cachesDirectory,in:.userDomainMask)[0]
                .appendingPathComponent("com.fengge.apa-file-manager.mac/Clipboard/"+UUID().uuidString)
            try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
            for e in items { try self.export(e,folder,root,0,storage:s,cancel:c) }
            let urls = items.map { root.appendingPathComponent($0.name) as NSURL }
            DispatchQueue.main.async { NSPasteboard.general.clearContents(); NSPasteboard.general.writeObjects(urls) }
        }
    }

}
