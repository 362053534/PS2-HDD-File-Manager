# PS2 HDD File Manager — 项目上下文备份

更新日期：2026-07-21

## 项目目的

这是一个独立的 Windows 图形工具，用于浏览、导入、导出、删除 PS2 APA/PFS 硬盘、原始镜像和 NBD 映射来源中的 PFS 文件。

- 不使用 Dokan，也不把 PFS 挂载成 Windows 盘符；
- GUI 通过短生命周期 `pfsshell.exe` 会话读写 PFS；
- 可写入，默认不只读；
- 程序标题：`PS2 HDD File Manager v1.0 — 峰哥原创，免费发布`。

## 位置与构建

- 源码目录：`C:\GitHub\PFS-BatchKit-Manager\PS2-HDD-File-Manager`
- 项目文件：`PS2-HDD-File-Manager.csproj`
- 正式 EXE：`bin\Release\net8.0-windows\PS2-HDD-File-Manager.exe`
- 技术栈：.NET 8、Windows Forms、`net8.0-windows`
- 需要管理员权限：`app.manifest` 中为 `requireAdministrator`。

构建命令：

```powershell
dotnet build .\PS2-HDD-File-Manager\PS2-HDD-File-Manager.csproj -c Release
```

构建时必须先关闭 `PS2-HDD-File-Manager.exe`。用户已明确授权：以后每次覆盖构建前，可自动关闭**这个指定程序**；若它正在导入、导出或复制，操作会被中断。

## 外部运行依赖

项目文件会从旧项目复制以下文件到输出目录的 `tools\`：

- `..\PFS-BatchKit-Manager\BAT\hdl_dump.exe`
- `..\PFS-BatchKit-Manager\BAT\pfsshell.exe`
- `..\PFS-BatchKit-Manager\BAT\wnbd-client.exe`
- `..\PFS-BatchKit-Manager\BAT\libwnbd.dll`

因此恢复时应保留整个 `C:\GitHub\PFS-BatchKit-Manager` 仓库结构，而不是只复制新项目目录。

## 当前功能

### 来源与分区

- “刷新”通过 `hdl_dump` 只列出识别为 PS2 APA 的物理磁盘；
- 可“加载硬盘镜像”（`.img/.bin/.raw`）；
- 可连接/断开 PS2 NBD（经 WNBD）；
- 来源列表为空才显示“未检测到PS2硬盘”；只要刷新或加载镜像加入任何有效来源，必须移除该占位项；
- 选择来源或镜像后，自动扫描并仅显示能被 `pfsshell` 实际挂载、读取的 PFS 分区；
- 浏览区双击分区进入；分区根目录点“上级目录”回到分区列表；
- `__mbr`、`__net`、`__system` 等不可浏览 APA 分区必须过滤掉。

### 文件操作

- 在 PFS GUI 内浏览目录、进入目录、上级目录、刷新；
- 新建目录、重命名、删除；
- Windows → PFS：文件选择导入，或在资源管理器复制后用“粘贴导入”/`Ctrl+V`；支持多个文件与目录递归导入；
- 导入前扫描完整目标树。若有同名文件或目录，只弹一次“全部覆盖 / 取消”：
  - 全部覆盖：同名文件先删除再写入；同名目录合并；
  - 取消：不开始本次导入，不修改 PFS；
- PFS → Windows：界面操作名为“复制”；单文件使用 Windows 标准延迟虚拟文件剪贴板，点击复制不读取 PFS，资源管理器粘贴时才导出到临时目录；来源断开则粘贴失败；
- 浏览区支持左键单选、`Ctrl` 复选、鼠标拖拽选框复选；右键点击未选中项目会先选中该项目；
- 右键菜单目前为“复制 / 删除”，没有剪切；删除支持多个文件，目录删除暂未在菜单开放。

## 关键技术点与已修复问题

### pfsshell 输入编码

`ProcessRunner.cs` 必须使用无 BOM 的 UTF-8：

```csharp
startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
```

带 BOM 时 `pfsshell` 会把首条 `device` 命令识别为未知命令，导致分区列表为空。

### pfsshell 成功判定

`pfsshell` 某些失败场景仍返回进程退出码 0。必须使用：

```csharp
PfsShellClient.IsSuccessfulSession(result)
```

它会同时检查输出是否包含 `(!)`。

### Windows 虚拟剪贴板

文件：`Services\VirtualFileClipboard.cs`

- 单文件：使用 Windows Forms `DataObject` 发布 `FileGroupDescriptorW`、`FileContents`、`Preferred DropEffect`，已确认诊断输出能读回这些格式；
- 多文件/目录：使用自定义 COM `IDataObject`，由 `FileGroupDescriptorW/FileContents` 通过 `lindex` 提供多个文件内容；
- `FormatEnumerator` 必须标记 `[ComVisible(true)]`，否则 Windows 11 无法枚举多文件格式，资源管理器“粘贴”会变灰；该修复刚完成，需要后续实机再次确认多文件粘贴是否正常；
- 虚拟剪贴板不能在复制后关闭程序；程序需保持运行直到资源管理器完成粘贴。

## 主要文件

- `MainForm.cs`：UI、来源扫描、浏览、导入、冲突处理、右键菜单；
- `Services\PfsShellClient.cs`：封装 `pfsshell` 会话与目录解析；
- `Services\ProcessRunner.cs`：外部进程执行和 UTF-8 无 BOM 输入；
- `Services\DiskDiscovery.cs`：APA 硬盘发现；
- `Services\NbdClient.cs`：WNBD 映射；
- `Services\VirtualFileClipboard.cs`：Windows 延迟虚拟文件剪贴板；
- `Models\PfsEntry.cs`：浏览项模型；
- `app.manifest`：UAC；
- `README.md`：项目概览。

## 测试数据

- `TestData\test-ps2-hdd.img`：约 1 GiB 测试镜像，内含 PFS 分区 `__test`；
- 此镜像被 `.gitignore` 忽略，不会由 Git 备份；如需保留测试环境，应手动单独复制该文件；
- 曾验证过单文件导入、重命名、导出、删除；导出文件内容与源文件一致。

## 恢复建议

1. 备份整个 `C:\GitHub\PFS-BatchKit-Manager` 目录；
2. 重点保留 `PS2-HDD-File-Manager` 目录以及旧项目 `PFS-BatchKit-Manager\BAT` 的工具文件；
3. 如需测试镜像，另行备份 `PS2-HDD-File-Manager\TestData\test-ps2-hdd.img`；
4. 新系统安装 .NET 8 SDK 后，在仓库根目录运行上述构建命令；
5. 以管理员身份运行输出目录中的 EXE；
6. 将本文件作为恢复后的首要上下文交给后续开发助手。

## Git 状态提醒

截至本备份创建时，`PS2-HDD-File-Manager/` 仍是 Git 未跟踪目录（未提交）。仅有 Git 仓库并不足以恢复本项目；请实际备份整个工作目录，或在后续自行提交该目录。
