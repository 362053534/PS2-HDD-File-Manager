import AppKit
import APACore

final class Delegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation {
    var window: NSWindow?
    let model = AppModel()
    lazy var browser = BrowserController(model:model)
    private var keyMonitor: Any?
    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu(); let appItem = NSMenuItem(); menu.addItem(appItem)
        let appMenu = NSMenu(); appItem.submenu = appMenu
        appMenu.addItem(withTitle:"关于 PS2 HDD File Manager",action:#selector(NSApplication.orderFrontStandardAboutPanel(_:)),keyEquivalent:"")
        appMenu.addItem(.separator()); appMenu.addItem(withTitle:"退出",action:#selector(NSApplication.terminate(_:)),keyEquivalent:"q")
        let editItem = NSMenuItem(); editItem.title = "编辑"; menu.addItem(editItem)
        let edit = NSMenu(title:"编辑"); editItem.submenu = edit
        for (title,action,key) in [("剪切",#selector(cutFiles),"x"),("拷贝",#selector(copyFiles),"c"),("粘贴",#selector(pasteFiles),"v"),("全选",#selector(selectAllFiles),"a")] {
            let item = edit.addItem(withTitle:title,action:action,keyEquivalent:key); item.target = self
        }
        NSApp.mainMenu = menu
        let window = NSWindow(contentRect:NSRect(x:0,y:0,width:1008,height:860),styleMask:[.titled,.closable,.miniaturizable,.resizable],backing:.buffered,defer:false)
        window.title = "PS2 HDD File Manager v1.0 — 峰哥原创，免费发布"
        window.delegate = self
        window.minSize = NSSize(width:960,height:660)
        window.contentViewController = browser; window.center(); window.makeKeyAndOrderFront(nil)
        self.window = window; NSApp.activate(ignoringOtherApps:true)
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching:.keyDown) { [weak self] event in
            if event.keyCode == 53, self?.model.transfer == true { self?.model.cancel(); return nil }
            return event
        }
        if CommandLine.arguments.contains("--ui-smoke-test") {
            DispatchQueue.main.asyncAfter(deadline:.now()+0.5) {
                Task { @MainActor in
                do {
                    try self.browser.checkInterface()
                    if CommandLine.arguments.contains("--ui-flow-test") { try await self.browser.checkStorageFlow() }
                    if let output = ProcessInfo.processInfo.environment["APA_UI_SNAPSHOT"], let view = window.contentView {
                        view.layoutSubtreeIfNeeded()
                        try view.dataWithPDF(inside:view.bounds).write(to:URL(fileURLWithPath:output))
                    }
                    if let output = ProcessInfo.processInfo.environment["APA_UI_BITMAP"], let view = window.contentView,
                       let bitmap = view.bitmapImageRepForCachingDisplay(in:view.bounds) {
                        view.cacheDisplay(in:view.bounds,to:bitmap)
                        try bitmap.representation(using:.png,properties:[:])!.write(to:URL(fileURLWithPath:output))
                    }
                    print("PASS native window: \(window.title), \(window.contentView!.bounds)")
                    NSApp.terminate(nil)
                } catch { print("FAIL UI: \(error)"); exit(1) }
                }
            }
        } else { model.scanDisks() }
    }
    private var editing: Bool { NSApp.keyWindow?.firstResponder is NSTextView }
    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        if editing { return true }
        if item.action == #selector(cutFiles) { return false }
        if item.action == #selector(selectAllFiles) { return !model.busy && !model.entries.isEmpty }
        let command: BrowserCommand = item.action == #selector(copyFiles) ? .copy:.paste
        return browser.menuItems().contains { $0.command == command && $0.enabled }
    }
    @objc func cutFiles() { if editing { NSApp.sendAction(#selector(NSText.cut(_:)),to:nil,from:nil) } }
    @objc func copyFiles() {
        if editing { NSApp.sendAction(#selector(NSText.copy(_:)),to:nil,from:nil) }
        else { browser.perform(.copy) }
    }
    @objc func pasteFiles() {
        if editing { NSApp.sendAction(#selector(NSText.paste(_:)),to:nil,from:nil) }
        else { browser.perform(.paste) }
    }
    @objc func selectAllFiles() {
        if editing { NSApp.sendAction(#selector(NSText.selectAll(_:)),to:nil,from:nil) }
        else { browser.selectAllFiles(nil) }
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if model.busy { model.cancel(); return false }; return true
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
