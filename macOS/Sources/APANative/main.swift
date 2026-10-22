import AppKit
import SwiftUI
import UniformTypeIdentifiers
import APACore

struct ContentView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(spacing:10) {
            HStack {
                TextField("PS2 IPv4 地址",text:$model.host).frame(width:170)
                Button("连接 PS2",action:model.connect)
                Button("打开镜像",action:model.chooseImage)
                Button("打开外接硬盘",action:model.chooseDisk)
                Button("断开",action:model.disconnect)
                Button("安全弹出",action:model.eject)
            }.disabled(model.busy)
            HStack { Text(model.source).font(.headline); Spacer(); Text(model.capacity).foregroundStyle(.secondary) }
            HStack {
                Button("上级",action:model.up).disabled(model.partition == nil)
                Button("刷新",action:model.refresh)
                Button("打开",action:model.openSelected).disabled(model.selected.count != 1)
                Button("导入",action:model.chooseImport).disabled(model.partition == nil)
                Button("导出",action:model.exportSelected).disabled(model.partition == nil || model.selected.isEmpty)
                Button("新建目录",action:model.createDirectory).disabled(model.partition == nil)
                Button("新建分区",action:model.createPartition)
                Spacer()
                Text(model.partition.map { $0+":"+model.path } ?? "APA 分区").textSelection(.enabled)
            }.disabled(model.busy)
            Table(model.entries,selection:$model.selected) {
                TableColumn("名称") { e in
                    Label(e.name,systemImage:e.directory ? "folder":"doc")
                        .onTapGesture(count:2) { if !model.busy { model.navigate(e) } }
                }.width(min:260,ideal:500)
                TableColumn("类型") { Text($0.directory ? "目录 / 分区":"文件") }.width(100)
                TableColumn("大小") { Text(AppModel.bytes($0.size)).monospacedDigit() }.width(130)
            }
            .contextMenu {
                Button("打开",action:model.openSelected)
                Button("导出",action:model.exportSelected).disabled(model.partition == nil)
                Button("复制",action:model.copySelected).disabled(model.partition == nil)
                Button("重命名",action:model.rename).disabled(model.selected.count != 1)
                Button("删除",role:.destructive,action:model.deleteSelected)
            }
            .disabled(model.busy)
            .onDrop(of:[UTType.fileURL],isTargeted:nil) { providers in
                guard !model.busy, model.partition != nil else { return false }
                let group = DispatchGroup(); let lock = NSLock(); var urls = [URL]()
                for provider in providers {
                    group.enter()
                    _ = provider.loadObject(ofClass:URL.self) { url,_ in
                        if let url { lock.lock(); urls.append(url); lock.unlock() }; group.leave()
                    }
                }
                group.notify(queue:.main) { model.importURLs(urls) }; return true
            }
            HStack {
                ProgressView(value:model.fraction).frame(width:160)
                Text(model.status).lineLimit(2).textSelection(.enabled)
                Spacer(); Button("取消",action:model.cancel).disabled(!model.busy)
            }
            DisclosureGroup("操作日志") {
                ScrollView { Text(model.log).font(.system(.caption,design:.monospaced)).textSelection(.enabled).frame(maxWidth:.infinity,alignment:.leading) }.frame(height:120)
            }
        }
        .padding(14).frame(minWidth:1000,minHeight:620)
        .background(Color(nsColor:.windowBackgroundColor))
        .alert("操作失败",isPresented:Binding(get:{model.failure != nil},set:{if !$0 { model.failure = nil }})) {
            Button("确定",role:.cancel) { model.failure = nil }
        } message: { Text(model.failure ?? "") }
    }
}
final class Delegate: NSObject, NSApplicationDelegate {
    var window: NSWindow?
    let model = AppModel()
    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu(); let appItem = NSMenuItem(); menu.addItem(appItem)
        let appMenu = NSMenu(); appItem.submenu = appMenu
        appMenu.addItem(withTitle:"关于 APA File Manager",action:#selector(NSApplication.orderFrontStandardAboutPanel(_:)),keyEquivalent:"")
        appMenu.addItem(.separator()); appMenu.addItem(withTitle:"退出",action:#selector(NSApplication.terminate(_:)),keyEquivalent:"q")
        let editItem = NSMenuItem(); editItem.title = "编辑"; menu.addItem(editItem)
        let edit = NSMenu(title:"编辑"); editItem.submenu = edit
        let copy = edit.addItem(withTitle:"复制",action:#selector(copyFiles),keyEquivalent:"c"); copy.target = self
        let paste = edit.addItem(withTitle:"粘贴文件",action:#selector(pasteFiles),keyEquivalent:"v"); paste.target = self
        edit.addItem(withTitle:"全选",action:#selector(NSText.selectAll(_:)),keyEquivalent:"a")
        NSApp.mainMenu = menu
        let window = NSWindow(contentRect:NSRect(x:0,y:0,width:1080,height:720),styleMask:[.titled,.closable,.miniaturizable,.resizable],backing:.buffered,defer:false)
        window.title = "APA File Manager — macOS 原生版"
        window.contentView = NSHostingView(rootView:ContentView(model:model)); window.center(); window.makeKeyAndOrderFront(nil)
        self.window = window; NSApp.activate(ignoringOtherApps:true)
        if CommandLine.arguments.contains("--ui-smoke-test") {
            DispatchQueue.main.asyncAfter(deadline:.now()+1) {
                guard let view = window.contentView, view.bounds.width >= 1000 else { exit(1) }
                if let output = ProcessInfo.processInfo.environment["APA_UI_SNAPSHOT"] {
                    do { try view.dataWithPDF(inside:view.bounds).write(to:URL(fileURLWithPath:output)) }
                    catch { print(error); exit(1) }
                }
                if let output = ProcessInfo.processInfo.environment["APA_UI_SCREENSHOT"] {
                    let capture = Process(); capture.executableURL = URL(fileURLWithPath:"/usr/sbin/screencapture")
                    capture.arguments = ["-x", "-l", String(window.windowNumber), output]
                    do { try capture.run(); capture.waitUntilExit(); print("Window capture exit: \(capture.terminationStatus)") }
                    catch { print("Window capture unavailable: \(error)") }
                }
                print("PASS native window: \(window.title), \(view.bounds)")
                NSApp.terminate(nil)
            }
        }
    }
    @objc func copyFiles() {
        if NSApp.keyWindow?.firstResponder is NSTextView { NSApp.sendAction(#selector(NSText.copy(_:)),to:nil,from:nil) }
        else { model.copySelected() }
    }
    @objc func pasteFiles() {
        if NSApp.keyWindow?.firstResponder is NSTextView { NSApp.sendAction(#selector(NSText.paste(_:)),to:nil,from:nil) }
        else { model.paste() }
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if model.busy { model.cancel(); return .terminateCancel }; return .terminateNow
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
// 兼容旧 Actions 的真实回归测试入口。测试二进制仅位于构建目录，不随应用发布。
if CommandLine.arguments.contains("--run-tests") {
    let runner = Bundle.main.bundleURL.deletingLastPathComponent().appendingPathComponent("CoreChecks")
    guard FileManager.default.isExecutableFile(atPath:runner.path) else {
        print("测试工具未随发行包分发，请在源码目录运行 macOS/test.sh")
        exit(2)
    }
    let process = Process(); process.executableURL = runner
    var environment = ProcessInfo.processInfo.environment
    environment["PFSSHELL_TEST_BIN"] = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/tools/pfsshell").path
    process.environment = environment
    do { try process.run(); process.waitUntilExit(); exit(process.terminationStatus) }
    catch { print("回归测试启动失败：\(error)"); exit(1) }
}
let app = NSApplication.shared
let delegate = Delegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
