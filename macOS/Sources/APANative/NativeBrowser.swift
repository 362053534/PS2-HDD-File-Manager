import AppKit
import Combine
import UniformTypeIdentifiers
import APACore

// The policy is shared by menus and keyboard actions; a shortcut never bypasses a disabled menu.
enum BrowserCommand: Int { case copy, delete, rename, paste, mkdir, createPartition, renamePartition, deletePartition, refresh }
struct BrowserMenuItem: Equatable {
    let title: String
    let command: BrowserCommand?
    let enabled: Bool
    static let separator = BrowserMenuItem(title:"",command:nil,enabled:false)
}
enum BrowserPolicy {
    static func protected(_ name: String) -> Bool {
        ["__mbr","__net","__system","__sysconf","__common","__boot"].contains(name.lowercased())
    }
    static func menu(inPartition: Bool, selection: [String], source: Bool, busy: Bool, clipboard: Bool) -> [BrowserMenuItem] {
        var items = [BrowserMenuItem]()
        func add(_ title: String, _ command: BrowserCommand, _ enabled: Bool = true) {
            items.append(BrowserMenuItem(title:title,command:command,enabled:enabled && !busy))
        }
        if inPartition {
            if !selection.isEmpty {
                add("拷贝",.copy); add("删除",.delete); add("重命名",.rename,selection.count == 1); items.append(.separator)
            }
            add("粘贴",.paste,clipboard); add("新建目录",.mkdir); items.append(.separator)
        } else {
            let mutable = source && selection.count == 1 && !protected(selection[0])
            add("新建 PFS 分区",.createPartition,source); add("重命名分区",.renamePartition,mutable); add("删除 PFS 分区",.deletePartition,mutable)
        }
        add("刷新",.refresh,source)
        return items
    }
}
final class BrowserTable: NSTableView {
    weak var owner: BrowserController?
    private var renameTimer: Timer?
    override func menu(for event: NSEvent) -> NSMenu? {
        guard let owner else { return nil }
        let hit = row(at:convert(event.locationInWindow,from:nil))
        selectContextRow(hit)
        return owner.contextMenu()
    }
    func selectContextRow(_ hit: Int) {
        if hit < 0 { deselectAll(nil) }
        else if !selectedRowIndexes.contains(hit) { selectRowIndexes(IndexSet(integer:hit),byExtendingSelection:false) }
    }
    override func mouseDown(with event: NSEvent) {
        renameTimer?.invalidate()
        let point = convert(event.locationInWindow,from:nil), hit = row(at:convert(event.locationInWindow,from:nil))
        let wasSelected = hit >= 0 && selectedRowIndexes == IndexSet(integer:hit)
        super.mouseDown(with:event)
        let current = convert(window?.mouseLocationOutsideOfEventStream ?? event.locationInWindow,from:nil)
        if event.clickCount == 1, hypot(current.x-point.x,current.y-point.y) < 4, wasSelected, hit == selectedRow, column(at:point) == 0, owner?.model.partition != nil {
            renameTimer = Timer.scheduledTimer(withTimeInterval:NSEvent.doubleClickInterval,repeats:false) { [weak self] _ in
                guard let self, self.selectedRow == hit, self.selectedRowIndexes.count == 1 else { return }
                self.owner?.beginRename()
            }
        }
    }
    override func keyDown(with event: NSEvent) {
        guard let owner else { super.keyDown(with:event); return }
        if event.keyCode == 53, owner.model.transfer { owner.model.cancel(); return }
        guard !owner.model.busy else { return }
        let modifiers = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if modifiers.contains(.command) || modifiers.contains(.control) {
            switch event.charactersIgnoringModifiers?.lowercased() {
            case "a": selectAll(nil); return
            case "c": owner.perform(.copy); return
            case "v": owner.perform(.paste); return
            default: break
            }
        }
        if event.keyCode == 51, modifiers.contains(.command) { owner.perform(owner.model.partition == nil ? .deletePartition:.delete); return }
        switch event.keyCode {
        case 120: owner.perform(owner.model.partition == nil ? .renamePartition:.rename) // F2
        case 117: owner.perform(owner.model.partition == nil ? .deletePartition:.delete) // forward Delete
        case 51: owner.model.up() // Backspace, as in Windows; Cmd+Backspace deletes on Mac.
        default: super.keyDown(with:event)
        }
    }
}
final class ExportPromise: NSObject, NSFilePromiseProviderDelegate {
    let entry: Entry, part: String, folder: String, sourceID: String
    weak var model: AppModel?
    init(_ entry: Entry, model: AppModel) {
        self.entry = entry; self.model = model; part = model.partition!; folder = model.path; sourceID = model.sourceID!
    }
    func filePromiseProvider(_ provider: NSFilePromiseProvider, fileNameForType fileType: String) -> String { entry.name }
    func filePromiseProvider(_ provider: NSFilePromiseProvider, writePromiseTo url: URL, completionHandler: @escaping (Error?) -> Void) {
        DispatchQueue.main.async {
            guard let model = self.model else { completionHandler(APAError.cancelled); return }
            model.promiseExport(self.entry,part:self.part,folder:self.folder,sourceID:self.sourceID,to:url,completion:completionHandler)
        }
    }
    func operationQueue(for filePromiseProvider: NSFilePromiseProvider) -> OperationQueue { .main }
}
final class RetainedFilePromise: NSFilePromiseProvider {
    private let exporter: ExportPromise
    init(_ exporter: ExportPromise) {
        self.exporter = exporter
        super.init(fileType:exporter.entry.directory ? UTType.folder.identifier:UTType.data.identifier,delegate:exporter)
    }
    required init?(coder: NSCoder) { fatalError() }
}
final class BrowserController: NSViewController, NSTableViewDataSource, NSTableViewDelegate, NSMenuItemValidation, NSTextFieldDelegate {
    let model: AppModel
    let table = BrowserTable()
    let source = NSPopUpButton()
    let ip = NSTextField()
    let capacity = NSTextField(labelWithString:"")
    let path = NSTextField()
    let status = NSTextField(labelWithString:"")
    let speed = NSTextField(labelWithString:"")
    let progress = NSProgressIndicator()
    let tabs = NSTabView()
    lazy var tabSelector = NSSegmentedControl(labels:["PFS 浏览器","诊断输出"],trackingMode:.selectOne,target:self,action:#selector(selectTab))
    let log = NSTextView()
    let system = NSButton(checkboxWithTitle:"显示系统分区",target:nil,action:nil)
    private var buttons = [String:NSButton]()
    private var renderedEntries = [Entry]()
    private var renderedContext = ""
    private var renderedSources = [SourceChoice]()
    private var observation: AnyCancellable?
    private var rendering = false
    private var scheduled = false
    private var lastLog = ""
    init(model: AppModel) { self.model = model; super.init(nibName:nil,bundle:nil) }
    required init?(coder: NSCoder) { fatalError() }
    private func button(_ title: String, _ selector: Selector) -> NSButton {
        let b = NSButton(title:title,target:self,action:selector); b.bezelStyle = .rounded; buttons[title] = b; return b
    }
    private func row(_ views: [NSView]) -> NSStackView {
        let r = NSStackView(views:views); r.orientation = .horizontal; r.alignment = .centerY; r.spacing = 8
        return r
    }
    private func spacer() -> NSView { let v = NSView(); v.setContentHuggingPriority(.defaultLow,for:.horizontal); return v }
    override func loadView() {
        view = NSView(frame:NSRect(x:0,y:0,width:1008,height:860))
        source.target = self; source.action = #selector(sourceChanged)
        source.setContentHuggingPriority(.defaultLow,for:.horizontal)
        source.widthAnchor.constraint(equalToConstant:549).isActive = true
        let sourceRow = row([NSTextField(labelWithString:"来源："),source,button("刷新",#selector(scan)),button("加载硬盘镜像",#selector(image)),spacer()])
        ip.placeholderString = "PS2 ApaNetServer IP，例如 192.168.1.10"; ip.stringValue = model.host
        ip.widthAnchor.constraint(equalToConstant:170).isActive = true
        let networkRow = row([NSTextField(labelWithString:"ApaNetServer："),ip,button("连接 ApaNetServer",#selector(connect)),button("断开 ApaNetServer",#selector(disconnect)),spacer()])
        let ejectRow = row([button("安全弹出硬盘",#selector(eject)),spacer()])
        capacity.font = .systemFont(ofSize:12); capacity.lineBreakMode = .byTruncatingTail
        system.target = self; system.action = #selector(toggleSystem)
        path.isEditable = false; path.isSelectable = true; path.lineBreakMode = .byTruncatingMiddle
        path.setContentHuggingPriority(.defaultLow,for:.horizontal)
        path.widthAnchor.constraint(equalToConstant:510).isActive = true
        let navigation = row([button("上级目录",#selector(up)),system,path,spacer()])
        table.owner = self; table.dataSource = self; table.delegate = self
        table.allowsMultipleSelection = true; table.allowsEmptySelection = true
        table.usesAlternatingRowBackgroundColors = false; table.rowHeight = 24
        table.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle
        for (id,title,width) in [("name","名称",310.0),("type","类型",90.0),("details","pfsshell 详情",420.0)] {
            let c = NSTableColumn(identifier:NSUserInterfaceItemIdentifier(id)); c.title = title; c.width = width; c.minWidth = 50
            table.addTableColumn(c)
        }
        table.target = self; table.doubleAction = #selector(openSelection)
        table.registerForDraggedTypes([.fileURL]); table.setDraggingSourceOperationMask(.copy,forLocal:false)
        let browserScroll = NSScrollView(); browserScroll.documentView = table; browserScroll.hasVerticalScroller = true; browserScroll.hasHorizontalScroller = true; browserScroll.borderType = .bezelBorder
        tabs.tabViewType = .noTabsNoBorder; tabSelector.selectedSegment = 0
        let tabHeader = row([tabSelector,spacer()])
        let browserTab = NSTabViewItem(identifier:"browser"); browserTab.label = "PFS 浏览器"; browserTab.view = browserScroll; tabs.addTabViewItem(browserTab)
        let logScroll = NSScrollView(); logScroll.hasVerticalScroller = true; logScroll.hasHorizontalScroller = true; logScroll.borderType = .bezelBorder
        log.isEditable = false; log.isSelectable = true; log.font = .monospacedSystemFont(ofSize:12,weight:.regular)
        log.isHorizontallyResizable = true; log.isVerticallyResizable = true; log.autoresizingMask = [.width]
        log.textContainer?.widthTracksTextView = false; log.textContainer?.containerSize = NSSize(width:CGFloat.greatestFiniteMagnitude,height:CGFloat.greatestFiniteMagnitude)
        logScroll.documentView = log
        let diagnostics = NSTabViewItem(identifier:"diagnostics"); diagnostics.label = "诊断输出"; diagnostics.view = logScroll; tabs.addTabViewItem(diagnostics)
        progress.isIndeterminate = false; progress.minValue = 0; progress.maxValue = 1; progress.style = .bar
        progress.widthAnchor.constraint(equalToConstant:150).isActive = true
        status.lineBreakMode = .byTruncatingTail; status.setContentHuggingPriority(.defaultLow,for:.horizontal)
        let footer = row([status,spacer(),progress,speed,button("取消传输",#selector(cancel))])
        let stack = NSStackView(views:[sourceRow,networkRow,ejectRow,capacity,navigation,tabHeader,tabs,footer])
        stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 8; stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo:view.leadingAnchor,constant:12),stack.trailingAnchor.constraint(equalTo:view.trailingAnchor,constant:-12),stack.topAnchor.constraint(equalTo:view.topAnchor,constant:12),stack.bottomAnchor.constraint(equalTo:view.bottomAnchor,constant:-10),tabs.heightAnchor.constraint(greaterThanOrEqualToConstant:320)])
        for item in [sourceRow,networkRow,ejectRow,navigation,tabHeader,tabs,footer] { item.widthAnchor.constraint(equalTo:stack.widthAnchor).isActive = true }
        tabs.setContentHuggingPriority(.defaultLow,for:.vertical)
        tabs.setContentCompressionResistancePriority(.defaultLow,for:.vertical)
        observation = model.objectWillChange.sink { [weak self] _ in
            guard let self, !self.scheduled else { return }; self.scheduled = true
            DispatchQueue.main.async { [weak self] in self?.scheduled = false; self?.render() }
        }
        render()
    }
    func render() {
        guard isViewLoaded else { return }; rendering = true; defer { rendering = false }
        if renderedSources != model.sources || source.numberOfItems == 0 {
            renderedSources = model.sources; source.removeAllItems()
            if model.sources.isEmpty { source.addItem(withTitle:"未检测到PS2硬盘") }
            else { model.sources.forEach { source.addItem(withTitle:$0.display) } }
        }
        if let index = model.sources.firstIndex(where: { $0.id == model.sourceID }) { source.selectItem(at:index) }
        let context = (model.sourceID ?? "")+"|"+model.displayPath
        if renderedEntries != model.entries || renderedContext != context { renderedEntries = model.entries; renderedContext = context; table.reloadData() }
        let selection = IndexSet(model.entries.indices.filter { model.selected.contains(model.entries[$0].name) })
        if table.selectedRowIndexes != selection { table.selectRowIndexes(selection,byExtendingSelection:false) }
        source.isEnabled = !model.busy && !model.sources.isEmpty; ip.isEnabled = !model.busy
        for b in buttons.values { b.isEnabled = !model.busy }
        buttons["断开 ApaNetServer"]?.isEnabled = !model.busy && model.networkConnected
        buttons["安全弹出硬盘"]?.isEnabled = !model.busy && model.hasSource
        buttons["上级目录"]?.isEnabled = !model.busy && model.partition != nil
        system.isEnabled = !model.busy; system.state = model.showSystem ? .on:.off
        capacity.stringValue = model.capacity; path.stringValue = model.displayPath; status.stringValue = model.status
        progress.isHidden = !model.transfer; progress.doubleValue = model.fraction
        speed.isHidden = !model.transfer; speed.stringValue = model.speed
        buttons["取消传输"]?.isHidden = !model.transfer; buttons["取消传输"]?.isEnabled = model.transfer && !model.cancelling
        if lastLog != model.log { lastLog = model.log; log.string = model.log; log.scrollToEndOfDocument(nil) }
        if let failure = model.failure {
            model.failure = nil
            let alert = NSAlert(); alert.messageText = "操作失败"; alert.informativeText = failure; alert.addButton(withTitle:"确定")
            if let window = view.window { alert.beginSheetModal(for:window) }
        }
    }
    func numberOfRows(in tableView: NSTableView) -> Int { model.entries.count }
    func tableViewSelectionDidChange(_ notification: Notification) {
        guard !rendering else { return }
        model.selected = Set(table.selectedRowIndexes.compactMap { model.entries.indices.contains($0) ? model.entries[$0].name:nil })
    }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard model.entries.indices.contains(row) else { return nil }
        let entry = model.entries[row], id = tableColumn!.identifier.rawValue
        if id == "name" {
            let cell = NSTableCellView(); cell.identifier = NSUserInterfaceItemIdentifier("name")
            let image = NSImageView(); image.image = NSImage(named:entry.directory ? NSImage.folderName:NSImage.multipleDocumentsName)
            let text = NSTextField(labelWithString:entry.name); text.isEditable = false; text.isSelectable = false
            text.delegate = self; text.lineBreakMode = .byTruncatingTail
            cell.addSubview(image); cell.addSubview(text); cell.imageView = image; cell.textField = text
            image.translatesAutoresizingMaskIntoConstraints = false; text.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([image.leadingAnchor.constraint(equalTo:cell.leadingAnchor,constant:3),image.widthAnchor.constraint(equalToConstant:18),image.heightAnchor.constraint(equalToConstant:18),image.centerYAnchor.constraint(equalTo:cell.centerYAnchor),text.leadingAnchor.constraint(equalTo:image.trailingAnchor,constant:5),text.trailingAnchor.constraint(equalTo:cell.trailingAnchor,constant:-4),text.centerYAnchor.constraint(equalTo:cell.centerYAnchor)])
            return cell
        }
        let sizeHint = model.partition == nil && model.sourceID?.hasPrefix("pfsnet:") == true && entry.size > 0 ? " · \(AppModel.bytes(entry.size))":""
        let type = model.partition == nil ? (BrowserPolicy.protected(entry.name) ? "系统分区":"PFS 分区") : (entry.directory ? "目录":"文件")
        let details = model.partition == nil ? (BrowserPolicy.protected(entry.name) ? "系统保留分区；可浏览读写，禁止删除/重命名分区":"可操作的 PFS 分区；双击查看") : entry.details
        let field = NSTextField(labelWithString:id == "type" ? type+sizeHint:details); field.lineBreakMode = .byTruncatingTail; return field
    }
    private var clipboard: Bool { !AppModel.clipboardURLs().isEmpty }
    func menuItems() -> [BrowserMenuItem] {
        BrowserPolicy.menu(inPartition:model.partition != nil,selection:Array(model.selected),source:model.hasSource,busy:model.busy,clipboard:clipboard)
    }
    func contextMenu() -> NSMenu {
        let menu = NSMenu(); menu.autoenablesItems = false
        for item in menuItems() {
            guard let command = item.command else { menu.addItem(.separator()); continue }
            let m = NSMenuItem(title:item.title,action:#selector(menuAction(_:)),keyEquivalent:""); m.target = self; m.tag = command.rawValue; m.isEnabled = item.enabled; menu.addItem(m)
        }
        return menu
    }
    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(selectAllFiles(_:)) { return !model.busy && !model.entries.isEmpty }
        return menuItems().contains { $0.command?.rawValue == menuItem.tag && $0.enabled }
    }
    @objc func menuAction(_ sender: NSMenuItem) { if let c = BrowserCommand(rawValue:sender.tag) { perform(c) } }
    func perform(_ command: BrowserCommand) {
        guard menuItems().contains(where: { $0.command == command && $0.enabled }) else { return }
        switch command {
        case .copy: model.copySelected()
        case .delete,.deletePartition: model.deleteSelected()
        case .rename: beginRename()
        case .renamePartition: model.rename()
        case .paste: model.paste()
        case .mkdir: model.createDirectory()
        case .createPartition: model.createPartition()
        case .refresh: model.refresh()
        }
    }
    func beginRename() {
        guard !model.busy, model.partition != nil, model.selected.count == 1, table.selectedRow >= 0,
              let cell = table.view(atColumn:0,row:table.selectedRow,makeIfNecessary:true) as? NSTableCellView, let text = cell.textField else { return }
        text.isEditable = true; text.isSelectable = true; text.selectText(nil)
    }
    func controlTextDidEndEditing(_ notification: Notification) {
        guard let field = notification.object as? NSTextField else { return }
        if (notification.userInfo?["NSTextMovement"] as? Int) == NSCancelTextMovement {
            let row = table.row(for:field)
            if model.entries.indices.contains(row) { field.stringValue = model.entries[row].name }
            field.isEditable = false; field.isSelectable = false; return
        }
        finishRename(field)
    }
    private func finishRename(_ field: NSTextField) {
        guard field.isEditable else { return }
        let row = table.row(for:field)
        guard model.entries.indices.contains(row) else { return }
        let old = model.entries[row].name, new = field.stringValue
        field.isEditable = false; field.isSelectable = false; field.stringValue = old
        view.window?.makeFirstResponder(table); model.rename(old,to:new)
    }
    @objc func selectAllFiles(_ sender: Any?) { if !model.busy { table.selectAll(nil) } }
    @objc private func selectTab() { tabs.selectTabViewItem(at:tabSelector.selectedSegment) }
    @objc private func sourceChanged() { if model.sources.indices.contains(source.indexOfSelectedItem) { model.selectSource(model.sources[source.indexOfSelectedItem].id) } }
    @objc private func scan() { model.scanDisks() }
    @objc private func image() { model.chooseImage() }
    @objc private func connect() { model.host = ip.stringValue; model.connect() }
    @objc private func disconnect() { model.disconnect() }
    @objc private func eject() { model.eject() }
    @objc private func up() { model.up() }
    @objc private func cancel() { model.cancel() }
    @objc private func toggleSystem() { model.setShowSystem(system.state == .on); render() }
    @objc private func openSelection() {
        guard !model.busy, model.selected.count == 1, let entry = model.entries.first(where: { model.selected.contains($0.name) }) else { return }
        if entry.directory { model.navigate(entry) } else { beginRename() }
    }
    func tableView(_ tableView: NSTableView, validateDrop info: NSDraggingInfo, proposedRow row: Int, proposedDropOperation dropOperation: NSTableView.DropOperation) -> NSDragOperation {
        guard !model.busy, info.draggingPasteboard.canReadObject(forClasses:[NSURL.self],options:[.urlReadingFileURLsOnly:true]) else { return [] }
        table.setDropRow(-1,dropOperation:.above); return .copy
    }
    func tableView(_ tableView: NSTableView, acceptDrop info: NSDraggingInfo, row: Int, dropOperation: NSTableView.DropOperation) -> Bool {
        guard !model.busy, let urls = info.draggingPasteboard.readObjects(forClasses:[NSURL.self],options:[.urlReadingFileURLsOnly:true]) as? [URL] else { return false }
        model.importURLs(urls); return model.partition != nil
    }
    func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? {
        guard !model.busy, model.partition != nil, model.sourceID != nil else { return nil }
        return RetainedFilePromise(ExportPromise(model.entries[row],model:model))
    }
    func tableView(_ tableView: NSTableView, draggingSession session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        // Providers retain their delegate until the receiver completes; keep them through the drag.
    }
}
