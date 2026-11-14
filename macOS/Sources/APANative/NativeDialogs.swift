import AppKit
import APACore

final class PartitionAcknowledgement: NSObject {
    let box = NSButton(checkboxWithTitle:"我确认目标来源正确，并已备份重要数据",target:nil,action:nil)
    let create: NSButton
    init(create: NSButton) {
        self.create = create; super.init(); box.target = self; box.action = #selector(toggle); create.isEnabled = false
    }
    @objc func toggle() { create.isEnabled = box.state == .on }
}
enum NativeDialogs {
    static func partitionForm(free: UInt64, maximum: UInt64? = nil) -> (NSAlert,NSTextField,NSTextField) {
        let alert = NSAlert(); alert.messageText = "新建 PFS 分区"
        alert.informativeText = "当前 APA 未分配空间：\(AppModel.bytes(free))\n当前布局可完整创建：\(AppModel.bytes(maximum ?? free))\n分区名可使用 DATA 或 +DATA；容量必须是 128MiB 的整数倍。\n大于单个 APA 分区上限时，会由 PS2SDK 自动建立主分区和子分区。"
        let name = NSTextField(string:"+OPL"), size = NSTextField(string:"\((maximum ?? free) / (128*1024*1024)*128)M")
        let grid = NSGridView(views:[[NSTextField(labelWithString:"分区名称："),name],[NSTextField(labelWithString:"容量："),size],[NSTextField(labelWithString:""),NSTextField(labelWithString:"例如：512G 或 128M")]])
        grid.frame = NSRect(x:0,y:0,width:480,height:100); grid.column(at:1).width = 370
        alert.accessoryView = grid; alert.addButton(withTitle:"下一步"); alert.addButton(withTitle:"取消"); alert.window.initialFirstResponder = name
        return (alert,name,size)
    }
    static func createPartition(free: UInt64, maximum: UInt64? = nil) -> (String,String)? {
        let (alert,name,size) = partitionForm(free:free,maximum:maximum)
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        return (name.stringValue.trimmingCharacters(in:.whitespacesAndNewlines),size.stringValue.trimmingCharacters(in:.whitespacesAndNewlines))
    }
    static func partitionConfirmation(source: String, name: String, bytes: UInt64) -> (NSAlert,PartitionAcknowledgement) {
        let alert = NSAlert(); alert.alertStyle = .warning; alert.messageText = "确认写入 APA 分区表"
        alert.informativeText = "将在以下来源的“当前未分配 APA 空间”中创建新的 PFS 逻辑卷：\n\(source)\n\n分区：\(name)\n容量：\(AppModel.bytes(bytes))\n\n此操作会写入分区表。请勿断电、拔出硬盘或强制关闭程序。"
        alert.addButton(withTitle:"创建"); alert.addButton(withTitle:"取消")
        let acknowledge = PartitionAcknowledgement(create:alert.buttons[0]); acknowledge.box.frame = NSRect(x:0,y:0,width:440,height:26)
        alert.accessoryView = acknowledge.box
        return (alert,acknowledge)
    }
    static func confirmPartition(source: String, name: String, bytes: UInt64) -> Bool {
        let (alert,acknowledge) = partitionConfirmation(source:source,name:name,bytes:bytes)
        return withExtendedLifetime(acknowledge) { alert.runModal() == .alertFirstButtonReturn && acknowledge.box.state == .on }
    }
}
