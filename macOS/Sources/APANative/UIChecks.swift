import AppKit
import APACore

extension BrowserController {
    // Runs the real AppKit controls without disks or a PS2; --ui-smoke-test never scans devices.
    func checkInterface() throws {
        func check(_ condition: @autoclosure () -> Bool, _ claim: String) throws {
            guard condition() else { throw APAError.message(claim) }
            print("PASS UI: \(claim)")
        }
        let (form,name,size) = NativeDialogs.partitionForm(free:512*1024*1024)
        try check(form.messageText == "新建 PFS 分区" && name.stringValue == "+OPL" && size.stringValue == "512M","partition name and size in a single form")
        let (_,_,safeSize) = NativeDialogs.partitionForm(free:953728*1048576,maximum:953344*1048576)
        try check(safeSize.stringValue == "953344M","partition form defaults to allocatable capacity rather than total free bytes")
        let (confirmation,acknowledge) = NativeDialogs.partitionConfirmation(source:"测试.img",name:"+OPL",bytes:512*1024*1024)
        try check(!confirmation.buttons[0].isEnabled,"partition creation requires acknowledgement")
        acknowledge.box.state = .on; acknowledge.toggle()
        try check(confirmation.buttons[0].isEnabled,"acknowledgement enables creation")
        let savedEntries = model.entries, savedSource = model.sourceID, savedSources = model.sources
        defer {
            model.entries = savedEntries; model.sources = savedSources; model.sourceID = savedSource
            model.partition = nil; model.selected = []; model.busy = false; model.transfer = false; render()
        }
        try check(tabs.tabViewItems.map(\.label) == ["PFS 浏览器","诊断输出"],"browser and diagnostics tabs")
        try check(table.tableColumns.map(\.title) == ["名称","类型","pfsshell 详情"],"Windows column names and order")
        try check(source.titleOfSelectedItem == "未检测到PS2硬盘","empty source placeholder")
        try check(menuItems().filter(\.enabled).isEmpty,"no-source partition actions disabled")
        model.sources = [SourceChoice(id:"fixture",display:"镜像 · 测试.img")]; model.sourceID = "fixture"
        model.entries = [Entry("+OPL",directory:true),Entry("__system",directory:true)]; render()
        table.selectContextRow(0); render()
        try check(contextMenu().items.map(\.title) == ["新建 PFS 分区","重命名分区","删除 PFS 分区","刷新"],"partition context menu order")
        try check(menuItems().filter(\.enabled).count == 4,"ordinary partition operations available")
        table.selectContextRow(1); render()
        try check(menuItems().filter(\.enabled).map(\.title) == ["新建 PFS 分区","刷新"],"protected partition cannot be renamed or deleted")
        model.partition = "+OPL"; model.entries = [Entry("DVD",directory:true),Entry("中文.iso",directory:false,size:1024,details:"-rw-rw-rw- 1024 2026-11-14 12:00 中文.iso")]; model.selected = []; render()
        table.selectRowIndexes(IndexSet([0,1]),byExtendingSelection:false); table.selectContextRow(1)
        try check(model.selected.count == 2,"right click selected row preserves multiselection")
        try check(!contextMenu().items.first(where: { $0.title == "重命名" })!.isEnabled,"multiple selection disables rename")
        table.selectContextRow(-1)
        try check(model.selected.isEmpty,"right click blank clears selection")
        try check(contextMenu().items.filter { !$0.isSeparatorItem }.map(\.title) == ["粘贴","新建目录","刷新"],"blank folder context has no file actions")
        table.selectContextRow(1)
        try check(model.selected == ["中文.iso"],"right click unselected row selects it")
        try check(contextMenu().items.filter { !$0.isSeparatorItem }.map(\.title) == ["拷贝","删除","重命名","粘贴","新建目录","刷新"],"file context menu order")
        let details = tableView(table,viewFor:table.tableColumns[2],row:1) as? NSTextField
        try check(details?.stringValue.contains("1024") == true,"pfsshell details displayed")
        view.layoutSubtreeIfNeeded()
        beginRename()
        let cell = table.view(atColumn:0,row:1,makeIfNecessary:true) as? NSTableCellView
        try check(cell?.textField?.isEditable == true,"file rename edits table row")
        if let field = cell?.textField {
            field.stringValue = "should-not-be-saved.iso"
            controlTextDidEndEditing(Notification(name:NSControl.textDidEndEditingNotification,object:field,userInfo:["NSTextMovement":NSCancelTextMovement]))
            try check(field.stringValue == "中文.iso" && !field.isEditable,"Escape cancels inline rename without saving")
        }
        model.busy = true; model.transfer = true; render()
        try check(contextMenu().items.allSatisfy { !$0.isEnabled },"busy state disables context actions")
        try check(!source.isEnabled && !system.isEnabled,"busy state disables source and system controls")
        try check(!progress.isHidden,"transfer progress visible")
        model.busy = false; model.transfer = false; render()
        try check(progress.isHidden,"idle transfer progress hidden")
        tabs.selectTabViewItem(at:1)
        try check(tabs.selectedTabViewItem?.label == "诊断输出","diagnostic output tab opens")
        tabs.selectTabViewItem(at:0)
        view.layoutSubtreeIfNeeded()
        try check(table.visibleRect.height > 300,"browser receives expanding window area")
        print("PASS UI: native menu/selection/rename/busy controls exercised")
    }
}

extension BrowserController {
    func checkStorageFlow() async throws {
        func check(_ condition: @autoclosure () -> Bool, _ claim: String) throws {
            guard condition() else { throw APAError.message(claim) }; print("PASS UI flow: \(claim)")
        }
        func wait() async throws {
            let until = Date().addingTimeInterval(40)
            while model.busy && Date() < until { try await Task.sleep(nanoseconds:20_000_000) }
            guard !model.busy else { throw APAError.message("UI operation timed out") }
            render()
        }
        let tool = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/tools/pfsshell")
        guard FileManager.default.isExecutableFile(atPath:tool.path) else { throw APAError.message("UI flow requires the built app with pfsshell") }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("apa-ui-flow-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:root) }
        func answerDialog(_ title: String) -> Timer {
            let timer = Timer(timeInterval:0.03,repeats:true) { timer in
                guard let window = NSApp.modalWindow else { return }
                func find(_ view: NSView) -> NSButton? {
                    if let b = view as? NSButton, b.title == title { return b }
                    return view.subviews.lazy.compactMap { find($0) }.first
                }
                if let view = window.contentView, let button = find(view) { timer.invalidate(); button.performClick(nil) }
            }
            RunLoop.main.add(timer,forMode:.common); RunLoop.main.add(timer,forMode:.modalPanel)
            return timer
        }
        func image(_ name: String, part: String) throws -> URL {
            let url = root.appendingPathComponent(name+".img")
            FileManager.default.createFile(atPath:url.path,contents:nil)
            let f = try FileHandle(forWritingTo:url); try f.truncate(atOffset:1024*1024*1024); try f.close()
            _ = try NativeProcess.run(tool,[],input:"device \"\(url.path)\"\ninitialize yes\nexit\n")
            let storage = try LocalStorage(device:url,tool:tool)
            try PartitionOperations.apply(0x50,values:[part,"128M"],storage:storage,cancel:Cancellation())
            return url
        }
        let first = try image("第一张",part:"+UI"), second = try image("第二张",part:"+Other")
        model.open(first); try await wait()
        try check(model.entries.map(\.name) == ["+UI"],"image opens at partition list; system partitions hidden")
        table.selectContextRow(0)
        NSApp.sendAction(table.doubleAction!,to:table.target,from:table); try await wait()
        try check(model.partition == "+UI" && path.stringValue == "+UI:/","double click opens partition and updates path")
        let input = root.appendingPathComponent("中文目录"), nested = input.appendingPathComponent("子目录")
        try FileManager.default.createDirectory(at:nested,withIntermediateDirectories:true)
        var payload = Data((0..<12345).map { UInt8(truncatingIfNeeded:$0) })
        try payload.write(to:input.appendingPathComponent("测试.iso")); try Data([1,2,3]).write(to:nested.appendingPathComponent("另一文件.bin"))
        model.importURLs([input]); try await wait()
        try check(model.entries.map(\.name) == ["中文目录"] && model.log.contains("已导入 测试.iso"),"recursive import succeeds without a conflict prompt")
        let replacement = Data([7,8,9,10,11]); try replacement.write(to:input.appendingPathComponent("测试.iso"))
        let cancelDialog = answerDialog("取消")
        model.importURLs([input]); try await wait(); cancelDialog.invalidate()
        try check(model.status.contains("取消"),"cancelling conflict confirmation ends import before writes")
        let overwriteDialog = answerDialog("全部覆盖")
        model.importURLs([input]); try await wait(); overwriteDialog.invalidate(); payload = replacement
        try check(model.status == "操作完成","overwrite confirmation merges directory and replaces existing file")
        model.rename("中文目录",to:"重命名目录"); try await wait()
        try check(model.entries.first?.name == "重命名目录","rename refreshes actual directory contents")
        table.selectContextRow(0); NSApp.sendAction(table.doubleAction!,to:table.target,from:table); try await wait()
        try check(path.stringValue == "+UI:/重命名目录","directory double click updates path")
        let file = model.entries.first { !$0.directory }!
        let out = root.appendingPathComponent("out"); try FileManager.default.createDirectory(at:out,withIntermediateDirectories:false)
        var completed = 0, errors = [String]()
        let entries = model.entries
        for entry in entries {
            model.promiseExport(entry,part:"+UI",folder:model.path,sourceID:first.path,to:out.appendingPathComponent(entry.name)) { error in
                DispatchQueue.main.async { completed += 1; if let error { errors.append(error.localizedDescription) } }
            }
        }
        try await wait()
        try check(completed == entries.count && errors.isEmpty,"multiple outgoing file promises are serialized and completed")
        let downloaded = try Data(contentsOf:out.appendingPathComponent(file.name))
        try check(downloaded == payload,"outgoing promised file matches imported bytes")
        model.up(); try await wait(); try check(model.path == "/" && model.partition == "+UI","up returns to partition root")
        model.up(); try await wait(); try check(model.partition == nil,"up returns to partition list")
        model.open(second); try await wait()
        try check(source.numberOfItems == 2 && model.entries.first?.name == "+Other","opening another image retains both sources")
        source.selectItem(at:0); NSApp.sendAction(source.action!,to:source.target,from:source); try await wait()
        try check(model.sourceID == first.path && model.entries.first?.name == "+UI","source dropdown switches back to the original image")
        model.showSystem = true; model.refresh(); try await wait()
        try check(model.entries.contains { $0.name == "__mbr" } == false,"non-PFS MBR stays unbrowsable even when showing system partitions")
        model.showSystem = false
        let cancelEject = answerDialog("取消"); model.eject(); cancelEject.invalidate()
        try check(model.sources.count == 2,"cancelling image removal retains both sources")
        let confirmEject = answerDialog("继续"); model.eject(); confirmEject.invalidate(); try await wait()
        try check(model.sources.count == 1 && model.sourceID == second.path,"image safe-eject removes only selected source and activates remaining source")
        print("PASS UI flow: disposable image source/navigation/import/rename/export checks")
    }
}
