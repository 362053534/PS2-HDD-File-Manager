import AppKit
import Combine
import UniformTypeIdentifiers
import APACore

struct SourceChoice: Equatable {
    let id: String
    let display: String
    var network: Bool { id.hasPrefix("pfsnet:") }
}
final class AppModel: ObservableObject {
    @Published var host = UserDefaults.standard.string(forKey:"PfsNetIP") ?? "192.168.100.111"
    @Published var sources = [SourceChoice]()
    @Published var sourceID: String?
    @Published var showSystem = false
    @Published var transfer = false
    @Published var cancelling = false
    @Published var speed = ""
    @Published var entries = [Entry]()
    @Published var selected = Set<String>()
    @Published var partition: String?
    @Published var path = "/"
    @Published var source = "未连接"
    @Published var status = "就绪"
    @Published var log = ""
    @Published var busy = false
    @Published var fraction: Double = 0
    @Published var capacity = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—"
    @Published var failure: String?
    private let queue = DispatchQueue(label:"com.fengge.apa.storage",qos:.userInitiated)
    private var storage: Storage?
    private var backends = [String:Storage]()
    private var cachedLayout: Layout?
    private var diskTotal: UInt64 = 0
    private var diskFree: UInt64 = 0
    private var transferExpected: UInt64 = 0
    private var transferFiles = 0
    private var fileIndex = 0
    private var transferred: UInt64 = 0
    private var lastFileDone: UInt64 = 0
    private var pendingPromises = [(run: () -> Void, cancel: () -> Void)]()
    private var promising = false
    private var token = Cancellation()
    private var activity: NSObjectProtocol?
    private var progressAt = Date.distantPast
    private var started = Date()

    private func append(_ text: String) {
        log += "[\(Date().formatted(date:.omitted,time:.standard))] \(text)\n"
        if log.count > 60000 { log = String(log.suffix(50000)) }
    }
    func operation(_ title: String, transfer: Bool = false, _ work: @escaping (Cancellation) throws -> Void) {
        guard !busy else { return }
        busy = true; cancelling = false; self.transfer = transfer; speed = ""; transferExpected = 0; transferFiles = 0; fileIndex = 0; transferred = 0; lastFileDone = 0; status = title; fraction = 0; token = Cancellation(); started = Date()
        let current = token
        activity = ProcessInfo.processInfo.beginActivity(options:[.userInitiated,.idleSystemSleepDisabled],reason:"APA 文件传输或磁盘操作")
        append(title)
        queue.async {
            var errorText: String?
            do { try work(current) } catch { errorText = error.localizedDescription }
            DispatchQueue.main.async {
                self.busy = false; self.transfer = false
                if let a = self.activity { ProcessInfo.processInfo.endActivity(a); self.activity = nil }
                if let text = errorText {
                    self.status = text; self.append(text)
                    if !current.isCancelled { self.failure = text }
                } else { self.status = "操作完成"; self.append("操作完成") }
                self.nextPromise()
            }
        }
    }
    func cancel() {
        let pending = pendingPromises; pendingPromises.removeAll(); promising = false
        pending.forEach { $0.cancel() }
        cancelling = true; token.cancel(); status = "正在取消（本地写入需完成当前文件并卸载）…"
    }
    private func backend() throws -> Storage {
        guard let storage else { throw APAError.message("请先连接 PS2 或打开本地硬盘") }; return storage
    }
    private func publish(_ entries: [Entry], partition: String?, path: String, space: Space) {
        let total = diskTotal, free = diskFree
        DispatchQueue.main.async {
            self.entries = entries.sorted { a,b in a.directory == b.directory ? a.name.localizedStandardCompare(b.name) == .orderedAscending : a.directory }
            self.partition = partition; self.path = path; self.selected = []
            self.capacity = "硬盘总计：\(Self.bytes(total))　未分配：\(Self.bytes(free))　当前分区：\(partition == nil ? "—" : Self.bytes(space.total))　当前分区剩余：\(partition == nil ? "—" : Self.bytes(space.free))"
        }
    }
    private func showPartitions(_ c: Cancellation) throws {
        let s = try backend(); try s.unmount(c); let layout = try s.layout(c)
        diskTotal = layout.total; diskFree = layout.free
        DispatchQueue.main.async { self.cachedLayout = layout }
        publish(layout.browsable.filter { self.showSystem || !BrowserPolicy.protected($0.name) },partition:nil,path:"/",space:Space(total:layout.total,free:layout.free))
    }
    private func showDirectory(_ part: String, _ path: String, _ c: Cancellation) throws {
        let s = try backend(); try s.mount(part,c)
        let entries = try s.list(path,c); let space = try s.space(c)
        publish(entries,partition:part,path:path,space:space)
    }
    var hasSource: Bool { sourceID != nil }
    var networkConnected: Bool { sources.contains { $0.network } }
    var displayPath: String { partition.map { $0+":"+path } ?? "PFS 分区（双击查看）" }
    private var tool: URL { Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/tools/pfsshell") }
    private func activate(_ choice: SourceChoice, storage next: Storage, _ c: Cancellation) throws {
        try c.check()
        do { try storage?.unmount(c) }
        catch {
            try c.check()
            DispatchQueue.main.async { self.append("原来源卸载失败，切换来源后请重新连接：\(error.localizedDescription)") }
        }
        storage = next; backends[choice.id] = next
        DispatchQueue.main.async {
            if !self.sources.contains(where: { $0.id == choice.id }) { self.sources.append(choice) }
            self.sourceID = choice.id; self.source = choice.display; self.cachedLayout = nil
            self.entries = []; self.selected = []; self.partition = nil; self.path = "/"
            self.capacity = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—"
        }
        try showPartitions(c)
    }
    func selectSource(_ id: String) {
        guard !busy, id != sourceID, let choice = sources.first(where: { $0.id == id }) else { return }
        operation("正在读取分区表…") { c in
            guard let next = self.backends[id] else { throw APAError.message("来源已断开，请刷新后重试") }
            try self.activate(choice,storage:next,c)
        }
    }
    func connect() {
        guard !busy else { return }
        let ip = host.trimmingCharacters(in:.whitespacesAndNewlines)
        guard !ip.isEmpty else { failure = "请先填写 PS2 IP。"; return }
        UserDefaults.standard.set(ip,forKey:"PfsNetIP")
        operation("正在连接 ApaNetServer…") { c in
            let oldIDs = self.backends.keys.filter { $0.hasPrefix("pfsnet:") }
            for id in oldIDs {
                if let old = self.backends.removeValue(forKey:id) { if self.storage === old { self.storage = nil }; old.disconnect() }
            }
            DispatchQueue.main.async {
                self.sources.removeAll { $0.network }
                if self.sourceID?.hasPrefix("pfsnet:") == true { self.sourceID = nil; self.source = "未连接"; self.entries = []; self.selected = []; self.partition = nil; self.capacity = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—" }
            }
            let next = try NetworkStorage(host:ip,cancel:c)
            try self.activate(SourceChoice(id:"pfsnet:"+ip,display:"PS2 ApaNetServer · \(ip)"),storage:next,c)
        }
    }
    func open(_ url: URL) {
        operation("正在读取分区表…") { c in
            let next = try LocalStorage(device:url,tool:self.tool)
            try self.activate(SourceChoice(id:url.path,display:(url.path.hasPrefix("/dev/") ? "硬盘 · " : "镜像 · ")+url.path),storage:next,c)
        }
    }
    func chooseImage() {
        guard !busy else { return }
        let p = NSOpenPanel(); p.canChooseDirectories = false; p.allowsMultipleSelection = false
        p.title = "选择 PS2 HDD 原始镜像"; p.allowedContentTypes = ["img","bin","raw"].compactMap { UTType(filenameExtension:$0) }; p.allowsOtherFileTypes = true
        if p.runModal() == .OK, let url = p.url { open(url) }
    }
    func scanDisks() {
        let previous = sourceID, remaining = sources.filter { !$0.id.hasPrefix("/dev/") }
        operation("正在扫描 PS2 APA 硬盘…") { c in
            var found = [SourceChoice]()
            for (path,display) in try NativeProcess.disks() {
                try c.check()
                do {
                    let local = try LocalStorage(device:URL(fileURLWithPath:path),tool:self.tool)
                    self.backends[path] = local; found.append(SourceChoice(id:path,display:display))
                } catch {
                    DispatchQueue.main.async { self.append("跳过 \(path)：\(error.localizedDescription)") }
                }
            }
            let choices = found
            DispatchQueue.main.async {
                self.sources.removeAll { $0.id.hasPrefix("/dev/") }; self.sources.insert(contentsOf:choices,at:0)
                self.append(choices.isEmpty ? "未检测到 PS2 APA 硬盘。" : "发现 \(choices.count) 个 PS2 APA 硬盘。")
            }
            if let choice = choices.first, let next = self.backends[choice.id] { try self.activate(choice,storage:next,c) }
            else if let previous, previous.hasPrefix("/dev/") {
                self.storage = nil
                if let choice = remaining.first, let next = self.backends[choice.id] { try self.activate(choice,storage:next,c) }
                else { DispatchQueue.main.async { self.sourceID = nil; self.entries = []; self.selected = []; self.partition = nil; self.capacity = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—" } }
            }
        }
    }
    func disconnect() {
        guard !busy, let choice = sources.first(where: { $0.network }) else { return }
        removeSource(choice)
    }
    private func removeSource(_ choice: SourceChoice, eject: Bool = false) {
        let active = choice.id == sourceID
        let nextChoice = sources.first { $0.id != choice.id }
        operation(eject ? "正在安全弹出硬盘…" : "正在移除来源…") { c in
            if let removed = self.backends[choice.id] { try removed.unmount(c); removed.disconnect() }
            if eject { _ = try NativeProcess.run(URL(fileURLWithPath:"/usr/sbin/diskutil"),["eject",choice.id]) }
            self.backends.removeValue(forKey:choice.id)
            DispatchQueue.main.async { self.sources.removeAll { $0.id == choice.id } }
            if active {
                self.storage = nil
                if let nextChoice, let next = self.backends[nextChoice.id] { try self.activate(nextChoice,storage:next,c) }
                else {
                    DispatchQueue.main.async {
                        self.sourceID = nil; self.source = "未连接"; self.entries = []; self.selected = []; self.partition = nil; self.path = "/"
                        self.capacity = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—"
                    }
                }
            }
        }
    }
    func eject() {
        guard !busy, let choice = sources.first(where: { $0.id == sourceID }) else { return }
        if choice.network {
            if confirm("安全弹出硬盘", "当前来源是 ApaNetServer连接，无法硬件弹出。是否断开 ApaNetServer？") { removeSource(choice) }
        } else if choice.id.hasPrefix("/dev/") {
            if confirm("安全弹出硬盘", "确定要安全弹出吗？\n\n\(choice.display)\n\n弹出前请确认没有其它程序正在访问该硬盘。") { removeSource(choice,eject:true) }
        } else if confirm("移除镜像", "“\(choice.display)”是镜像文件，无法执行硬件弹出。是否仅从列表中移除？") { removeSource(choice) }
    }
    func setShowSystem(_ value: Bool) {
        guard !busy else { return }
        if value && !confirm("显示系统分区", "即将显示系统保留分区（如 __system、__common 等）。\n\n确认后可以浏览并自由读写这些分区内的文件，但禁止删除或重命名系统分区本身。\n误操作可能导致 PS2 无法正常启动。\n\n是否继续？") { return }
        showSystem = value
        if hasSource && partition == nil { refresh() }
    }
    func refresh() {
        guard hasSource, !busy else { return }
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
        guard !busy else { return }
        guard let p = partition, let name = prompt("新建目录") else { return }
        let folder = path
        operation("新建目录 \(name)") { c in
            try Names.component(name); let s = try self.backend(); try s.mount(p,c)
            try s.mutate(0x21,[folder,name],c); try self.showDirectory(p,folder,c)
        }
    }
    func rename() {
        guard !busy, selected.count == 1, let name = selected.first,
              partition != nil || !BrowserPolicy.protected(name),
              let newName = prompt(partition == nil ? "重命名分区" : "重命名",initial:name) else { return }
        rename(name,to:newName)
    }
    func rename(_ name: String, to newName: String) {
        guard !busy, newName != name, partition != nil || (!BrowserPolicy.protected(name) && !BrowserPolicy.protected(newName)) else { return }
        let p = partition, folder = path
        operation("正在重命名…") { c in
            try Names.component(newName); let s = try self.backend()
            if let p { try s.mount(p,c); try s.mutate(0x24,[folder,name,newName],c); try self.showDirectory(p,folder,c) }
            else { try PartitionOperations.apply(0x52,values:[name,newName],storage:s,cancel:c); try self.showPartitions(c) }
        }
    }
    func createPartition() {
        guard !busy, hasSource, partition == nil else { return }
        guard let layout = cachedLayout else { failure = "请先刷新分区表。"; return }
        let maximum: UInt64
        do { maximum = try ApaAllocation.maximum(layout) } catch { failure = error.localizedDescription; return }
        guard maximum > 0 else { failure = "当前布局没有可完整创建 PFS 分区的空间。"; return }
        guard let (name,size) = NativeDialogs.createPartition(free:diskFree,maximum:maximum) else { return }
        do {
            try Names.partition(name)
            guard !layout.partitions.contains(where: { $0.isMain && $0.name.caseInsensitiveCompare(name) == .orderedSame }) else {
                throw APAError.message("分区“\(name)”已存在，已取消创建。")
            }
            guard !BrowserPolicy.protected(name) else { throw APAError.message("不能使用系统保留分区名") }
            let bytes = try Names.size(size)
            guard bytes <= diskFree else { throw APAError.message("APA 未分配空间不足。未执行任何写入。") }
            try ApaAllocation.requireComplete(layout,bytes:bytes)
            guard NativeDialogs.confirmPartition(source:source,name:name,bytes:bytes) else { return }
        } catch { failure = error.localizedDescription; return }
        operation("创建分区 \(name)") { c in
            try PartitionOperations.apply(0x50,values:[name,size],storage:self.backend(),cancel:c); try self.showPartitions(c)
        }
    }
    func deleteSelected() {
        let items = entries.filter { selected.contains($0.name) }, p = partition, folder = path
        guard !busy, !items.isEmpty, p != nil || (items.count == 1 && !BrowserPolicy.protected(items[0].name)) else { return }
        if p == nil {
            guard confirm("第一次确认：删除 PFS 分区", "将永久删除 PFS 分区“\(items[0].name)”（\(Self.bytes(items[0].size))）及其全部文件。\n\n来源：\(source)\n\n此操作会修改 APA 分区表，删除后无法恢复。确定继续？"),
                  confirm("第二次确认：永久删除", "最后确认：立即删除“\(items[0].name)”？\n\n程序将在删除后重新校验 APA 分区表。") else { return }
        } else if !confirm("删除", "确定要删除选中的 \(items.count) 个项目吗？\n"+items.map(\.name).joined(separator:"\n")+"\n目录将递归删除，此操作无法撤销。") { return }
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
        transferred += done >= lastFileDone ? done-lastFileDone : done; lastFileDone = done
        guard Date().timeIntervalSince(progressAt) > 0.15 || done == total else { return }
        progressAt = Date()
        let rate = Double(transferred)/max(0.001,Date().timeIntervalSince(started))
        let elapsed = Int(Date().timeIntervalSince(started))
        let fraction = transferExpected > 0 ? min(1,Double(transferred)/Double(transferExpected)) : (total == 0 ? 1 : Double(done)/Double(total))
        let index = fileIndex, count = max(1,transferFiles)
        DispatchQueue.main.async {
            self.speed = "\(Self.bytes(UInt64(rate)))/s · \(elapsed/60):\(String(format:"%02d",elapsed%60))"
            self.fraction = fraction
            self.status = "[\(index)/\(count)] \(name) · \(Self.bytes(done)) / \(Self.bytes(total))"
        }
    }
    func chooseImport() {
        let p = NSOpenPanel(); p.canChooseFiles = true; p.canChooseDirectories = true; p.allowsMultipleSelection = true
        if p.runModal() == .OK { importURLs(p.urls) }
    }
    static func clipboardURLs() -> [URL] {
        let urls = NSPasteboard.general.readObjects(forClasses:[NSURL.self],options:[.urlReadingFileURLsOnly:true]) as? [URL] ?? []
        return urls.filter { $0.isFileURL && FileManager.default.fileExists(atPath:$0.path) }
    }
    func paste() { importURLs(Self.clipboardURLs()) }
    func importURLs(_ urls: [URL]) {
        guard !busy, !urls.isEmpty else { return }
        guard let part = partition else { failure = "请先双击打开一个 PFS 分区，再拖入文件。"; return }
        let folder = path
        operation("正在扫描导入冲突…",transfer:true) { c in
            let s = try self.backend(); try s.mount(part,c)
            var plan = [(url:URL,parent:String,directory:Bool,size:UInt64,existing:Entry?)]()
            var cache = [String:[Entry]](), freshFolders = Set<String>()
            func scan(_ url: URL, _ parent: String, _ depth: Int) throws {
                try c.check(); guard depth < 128 else { throw APAError.message("目录嵌套过深") }
                let v = try url.resourceValues(forKeys:[.isSymbolicLinkKey,.isDirectoryKey,.isRegularFileKey,.fileSizeKey])
                guard v.isSymbolicLink != true else { throw APAError.message("不导入符号链接：\(url.lastPathComponent)") }
                try Names.component(url.lastPathComponent)
                let directory = v.isDirectory == true
                guard directory || v.isRegularFile == true else { throw APAError.message("仅支持普通文件和目录") }
                if cache[parent] == nil { cache[parent] = freshFolders.contains(parent) ? [] : try s.list(parent,c) }
                let existing = cache[parent]!.first { $0.name == url.lastPathComponent }
                plan.append((url,parent,directory,UInt64(v.fileSize ?? 0),existing))
                cache[parent]!.removeAll { $0.name == url.lastPathComponent }
                cache[parent]!.append(Entry(url.lastPathComponent,directory:directory,size:UInt64(v.fileSize ?? 0)))
                if directory {
                    let remote = try Names.path(parent,url.lastPathComponent)
                    if existing?.directory != true { freshFolders.insert(remote) }
                    for child in try FileManager.default.contentsOfDirectory(at:url,includingPropertiesForKeys:nil).sorted(by: { $0.path < $1.path }) { try scan(child,remote,depth+1) }
                }
            }
            for url in urls { try scan(url,folder,0) }
            var overwrite = false
            if plan.contains(where: { $0.existing != nil }) {
                DispatchQueue.main.sync {
                    let alert = NSAlert(); alert.messageText = "发现同名项目"
                    alert.informativeText = "目标 PFS 目录中发现同名文件或目录。\n选择“全部覆盖”将替换同名文件，并合并同名目录。"
                    alert.addButton(withTitle:"全部覆盖"); alert.addButton(withTitle:"取消")
                    overwrite = alert.runModal() == .alertFirstButtonReturn
                }
                if !overwrite { c.cancel(); throw APAError.cancelled }
            }
            try c.check()
            let space = try s.space(c)
            let zone = max(UInt64(1),UInt64(space.zone))
            var needed: UInt64 = 0
            for item in plan {
                let required = item.directory ? (item.existing?.directory == true ? 0:zone) : ((item.size+zone-1)/zone)*zone
                let old = item.existing.map { $0.directory ? 0 : (($0.size+zone-1)/zone)*zone } ?? 0
                let net = s is LocalStorage ? required : (required > old ? required-old:0)
                let (next,overflow) = needed.addingReportingOverflow(net)
                guard !overflow else { throw APAError.message("导入大小超出支持范围") }; needed = next
            }
            guard needed <= space.free else { throw APAError.message("PFS 分区剩余空间不足：需要 \(Self.bytes(needed))，当前可用 \(Self.bytes(space.free))。未执行任何写入。") }
            self.transferExpected = plan.filter { !$0.directory }.reduce(0) { $0+$1.size }
            self.transferFiles = plan.filter { !$0.directory }.count
            DispatchQueue.main.async { self.append("PFS 导入空间预检通过：当前分区剩余 \(Self.bytes(space.free))，预计净占用 \(Self.bytes(needed))。"); self.status = "正在导入 \(plan.count) 个项目…" }
            let performImport = {
                for item in plan {
                    try c.check()
                    let existing = try s.list(item.parent,c).first { $0.name == item.url.lastPathComponent }
                    if let existing, existing.directory != item.directory {
                        guard overwrite else { throw APAError.message("目标已变化，请重新导入") }
                        try self.delete(existing,parent:item.parent,storage:s,cancel:c,depth:0)
                    }
                    if item.directory {
                        if existing?.directory != true { try s.mutate(0x21,[item.parent,item.url.lastPathComponent],c) }
                    } else {
                        guard existing == nil || overwrite else { throw APAError.message("目标已存在：\(item.url.lastPathComponent)") }
                        self.lastFileDone = 0; self.fileIndex += 1
                        try s.upload(item.url,parent:item.parent,overwrite:overwrite,cancel:c) { self.progress(item.url.lastPathComponent,$0,$1) }
                        DispatchQueue.main.async { self.append("已导入 \(item.url.lastPathComponent)（\(Self.bytes(item.size))）") }
                    }
                }
            }
            if let local = s as? LocalStorage { try local.withImportSession(c,performImport) }
            else { try performImport() }
            try self.showDirectory(part,folder,c)
        }
    }
    func exportSelected() {
        let items = entries.filter { selected.contains($0.name) }
        guard let part = partition, !items.isEmpty else { return }
        let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let destination = panel.url else { return }
        let folder = path
        operation("正在导出文件…",transfer:true) { c in
            let s = try self.backend(); try s.mount(part,c)
            try self.measureExport(items,parent:folder,storage:s,cancel:c)
            for entry in items { try self.export(entry,folder,destination,0,storage:s,cancel:c) }
        }
    }
    private func measureExport(_ items: [Entry], parent: String, storage s: Storage, cancel c: Cancellation, depth: Int = 0) throws {
        try c.check(); guard depth < 128 else { throw APAError.message("目录嵌套过深") }
        for item in items {
            if item.directory { try measureExport(s.list(Names.path(parent,item.name),c),parent:Names.path(parent,item.name),storage:s,cancel:c,depth:depth+1) }
            else { transferExpected += item.size; transferFiles += 1 }
        }
    }
    private func export(_ entry: Entry, _ remote: String, _ local: URL, _ depth: Int, storage s: Storage, cancel c: Cancellation, targetName: String? = nil) throws {
                try c.check(); guard depth < 128 else { throw APAError.message("目录嵌套过深") }; try Names.component(entry.name)
                let target = local.appendingPathComponent(targetName ?? entry.name)
                guard !FileManager.default.fileExists(atPath:target.path) else { throw APAError.message("导出目标已存在：\(target.lastPathComponent)。请选择空目录。") }
                if entry.directory {
                    try FileManager.default.createDirectory(at:target,withIntermediateDirectories:false)
                    for child in try s.list(Names.path(remote,entry.name),c) { try export(child,Names.path(remote,entry.name),target,depth+1,storage:s,cancel:c) }
                } else { self.lastFileDone = 0; self.fileIndex += 1; try s.download(entry,parent:remote,to:target,cancel:c) { self.progress(entry.name,$0,$1) } }
            }

    func copySelected() {
        let items = entries.filter { selected.contains($0.name) }
        guard let part = partition, !items.isEmpty else { return }
        let folder = path
        operation("正在拷贝文件…",transfer:true) { c in
            let s = try self.backend(); try s.mount(part,c)
            try self.measureExport(items,parent:folder,storage:s,cancel:c)
            let root = FileManager.default.urls(for:.cachesDirectory,in:.userDomainMask)[0]
                .appendingPathComponent("com.fengge.apa-file-manager.mac/Clipboard/"+UUID().uuidString)
            try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
            for e in items { try self.export(e,folder,root,0,storage:s,cancel:c) }
            let urls = items.map { root.appendingPathComponent($0.name) as NSURL }
            DispatchQueue.main.async { NSPasteboard.general.clearContents(); NSPasteboard.general.writeObjects(urls) }
        }
    }

    private func nextPromise() {
        guard !busy else { return }
        if pendingPromises.isEmpty { promising = false; return }
        pendingPromises.removeFirst().run()
    }
    func promiseExport(_ entry: Entry, part: String, folder: String, sourceID: String, to url: URL, completion: @escaping (Error?) -> Void) {
        guard (!busy || promising), self.sourceID == sourceID else { completion(APAError.message("来源已切换或正在执行其它操作，请重新拖出文件。")); return }
        promising = true
        pendingPromises.append((run: {
            guard self.sourceID == sourceID else { completion(APAError.cancelled); self.nextPromise(); return }
            self.operation("正在导出文件…",transfer:true) { c in
                do {
                    let s = try self.backend(); try s.mount(part,c)
                    try self.measureExport([entry],parent:folder,storage:s,cancel:c)
                    try self.export(entry,folder,url.deletingLastPathComponent(),0,storage:s,cancel:c,targetName:url.lastPathComponent)
                    completion(nil)
                } catch { completion(error); throw error }
            }
        }, cancel: { completion(APAError.cancelled) }))
        nextPromise()
    }
}
