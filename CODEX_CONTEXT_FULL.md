# Codex 完整项目上下文备份

> 说明：这是当前 Codex 线程上下文的可读备份，包含需求、决策、实现状态和后续恢复信息。它不是 Codex 服务端保存的原始聊天数据库文件；完整原始线程仍由 Codex 线程本身保存。

## 1. 用户最初目标

用户希望从原项目 HDD Management 功能中抽离 `[6] Explore PS2 HDD` 和 `[7] NBD Server`，做成一个轻量化的 PFS 硬盘浏览器。

讨论过 Dokan MSI 和 WNBD 驱动：

- Dokan 用于把用户态文件系统挂载为 Windows 盘符；
- WNBD 用于把网络块设备映射为 Windows 块设备；
- 用户最终选择 GUI 内直接浏览 PFS，不挂载到 Windows 盘符，因此不依赖 Dokan；
- 软件核心是连接 PFS 硬盘并进行复制、粘贴、删除，所以名称最终确定为 `PS2-HDD-File-Manager`。

## 2. 当前项目位置

原仓库：

`C:\GitHub\PFS-BatchKit-Manager\`

新项目：

`C:\GitHub\PFS-BatchKit-Manager\PS2-HDD-File-Manager\`

正式输出：

`C:\GitHub\PFS-BatchKit-Manager\PS2-HDD-File-Manager\bin\Release\net8.0-windows\PS2-HDD-File-Manager.exe`

项目没有替换原来的旧版 `PFS-BatchKit-Manager`，新项目是独立目录。

## 3. 当前界面和名称

窗口标题当前为：

`PS2 HDD File Manager v1.0 — 峰哥原创，免费发布`

当前界面主要区域：

- 来源列表；
- 刷新；
- 加载硬盘镜像；
- NBD IP、连接 NBD、断开 NBD；
- PFS 浏览器；
- 诊断输出；
- 上级目录、当前路径、刷新文件；
- 导出选中项；
- 复制；
- 导入文件；
- 粘贴导入；
- 新建目录、重命名、删除。

曾经存在的“读取 PFS 分区 / 分区下拉框 / 打开分区”手动操作行已经移除。

用户要求：选择镜像或来源硬盘后，浏览区自动列出所有可挂载、可操作的 PFS 分区，双击分区直接打开。

## 4. 来源和分区规则

来源扫描使用旧项目中的 `hdl_dump.exe`，只显示被识别为 PS2 APA 格式的物理磁盘。

来源包括：

- 物理 PS2 APA 硬盘；
- 通过 WNBD 映射得到的来源；
- 手动加载的 `.img`、`.bin`、`.raw` 镜像。

来源列表占位规则：

- 来源完全为空时显示“未检测到PS2硬盘”；
- 只要刷新检测到有效硬盘，立即移除占位项；
- 只要加载镜像，也立即移除占位项；
- 统一通过 `AddSourceDevice` 处理，避免有效来源和占位项同时存在。

分区加载规则：

- `pfsshell ls` 先列 APA 分区；
- 对每个候选分区执行实际挂载和根目录读取；
- 只有会话成功的 PFS 分区才显示；
- 不能挂载的 `__mbr`、`__net`、`__system` 等不会显示；
- 测试镜像中通常只显示可操作的 `__test`。

## 5. 已修复的问题

### 5.1 StandardInputEncoding 错误

早期启动错误：

`StandardInputEncoding is only supported when standard input is redirected.`

原因是对没有重定向标准输入的进程设置了 `StandardInputEncoding`。现在只在 `input != null` 时设置。

### 5.2 pfsshell BOM 问题

如果给 `pfsshell` 发送带 UTF-8 BOM 的输入，第一条命令会变成未知命令，例如：

`(!) ﻿device: unknown command or bad number of arguments.`

当前 `ProcessRunner.cs` 使用无 BOM UTF-8：

```csharp
new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
```

### 5.3 pfsshell 退出码问题

`pfsshell` 某些错误仍返回退出码 0，因此不能只看 `ExitCode`。

当前通过 `PfsShellClient.IsSuccessfulSession(result)` 同时检查：

- `ExitCode == 0`；
- 输出中不包含 `(!)`。

### 5.4 PFS 解析问题

已处理：

- APA 分区按一行一个名称的输出格式；
- `pfsshell ls -l` 的目录/文件长列表；
- 目录名称末尾的 `/`；
- `.`、`..` 和命令回显过滤。

### 5.5 UAC

`app.manifest` 已设置 `requireAdministrator`，物理磁盘读取和写入需要管理员权限。

## 6. 文件操作设计

PFS 读写不通过 Windows 文件系统挂载，而是每次创建短生命周期 `pfsshell` 会话：

1. 发送 `device`；
2. 必要时发送 `mount`；
3. 进入目标目录；
4. 执行 `ls`、`get`、`put`、`mkdir`、`rm`、`rmdir` 或 `rename`；
5. 执行 `umount`；
6. 执行 `exit`。

支持：

- 浏览目录；
- 双击进入目录；
- 上级目录；
- 新建目录；
- 重命名；
- 删除文件；
- 导入文件；
- 导出文件；
- 多文件导入；
- 递归导入目录。

目录删除尚未作为普通右键操作开放，但递归覆盖时内部可以使用 `rmdir` 清理目录树。

## 7. Windows 到 PFS 的导入和冲突规则

Windows 资源管理器中复制文件或目录后，在 PFS 当前目录点击“粘贴导入”或按 `Ctrl+V`。

导入前会递归扫描整个待导入树：

- 没有同名项目：直接导入；
- 发现任意同名文件或目录：只弹出一次对话框；
- 对话框只有“全部覆盖”和“取消”。

“全部覆盖”：

- 同名文件先执行 `rm`，再执行 `put`；
- 同名目录保留并合并；
- 如果目标是同名文件而来源是目录，会先删除文件再 `mkdir`；
- 如果目标是同名目录而来源是文件，会递归删除目标目录后再写入文件。

“取消”：

- 本次导入不执行任何 `mkdir`、`rm`、`put`；
- 不修改目标 PFS。

## 8. PFS 到 Windows 的复制和剪贴板

用户明确要求：点击复制时不要等待、不要提前导出；只有资源管理器真正粘贴时才读取 PFS。

实现文件：

`Services\VirtualFileClipboard.cs`

单文件复制：

- 使用 Windows Forms `DataObject`；
- 注册 `FileGroupDescriptorW`；
- 注册 `FileGroupDescriptor`；
- 注册 `FileContents`；
- 注册 `Preferred DropEffect`；
- 实际 PFS `get` 在资源管理器读取延迟流时发生。

单文件诊断已经确认成功显示：

`Windows 剪贴板已注册格式：FileGroupDescriptorW, FileGroupDescriptor, FileContents, Preferred DropEffect`

多文件/目录复制：

- 使用自定义 COM `IDataObject`；
- `FileGroupDescriptorW/FileContents` 通过 `lindex` 区分多个文件；
- `FormatEnumerator` 必须带 `[ComVisible(true)]`；
- 多文件修复已经构建到正式版，但用户还需要实机确认资源管理器是否能正常粘贴。

程序必须保持运行，直到资源管理器完成粘贴。

## 9. 浏览区选择和右键菜单

浏览区现在支持：

- 左键单击选中；
- `Ctrl` 多选；
- 鼠标拖拽选框多选；
- 右键点击未选中项时先选中该项；
- 右键菜单“复制”；
- 右键菜单“删除”；
- 暂不提供剪切。

删除支持多个已选文件，目录删除在右键菜单中禁用。

## 10. 测试镜像

测试镜像路径：

`C:\GitHub\PFS-BatchKit-Manager\PS2-HDD-File-Manager\TestData\test-ps2-hdd.img`

大小约 1 GiB，曾初始化为 APA/PFS 镜像，并创建了 `__test` PFS 分区。

已验证过：

- 识别 `__test`；
- 列出根目录；
- 创建目录；
- 导入 `README.md`；
- 重命名；
- 导出并核对字节数；
- 删除文件。

该镜像被 `.gitignore` 忽略，Git 不会自动备份，必须单独复制。

## 11. 依赖文件

新项目通过项目文件从旧目录取得：

- `PFS-BatchKit-Manager\BAT\hdl_dump.exe`；
- `PFS-BatchKit-Manager\BAT\pfsshell.exe`；
- `PFS-BatchKit-Manager\BAT\wnbd-client.exe`；
- `PFS-BatchKit-Manager\BAT\libwnbd.dll`。

恢复时不要只备份新项目目录，最好备份整个：

`C:\GitHub\PFS-BatchKit-Manager\`

## 12. 构建规则

用户已经明确授权：以后每次构建覆盖前自动关闭已打开的：

`PS2-HDD-File-Manager.exe`

只允许关闭这个指定程序，不要关闭其他程序。若正在导入、导出或复制，关闭会中断操作。

标准构建命令：

```powershell
dotnet build .\PS2-HDD-File-Manager\PS2-HDD-File-Manager.csproj -c Release
```

验证命令：

```powershell
git diff --check
```

当前项目使用 .NET 8 SDK 和 Windows Forms，目标框架是 `net8.0-windows`。

## 13. 主要源文件

- `MainForm.cs`：窗口、来源、分区、浏览、导入、冲突、选择、右键菜单；
- `Services\PfsShellClient.cs`：pfsshell 会话、目录解析和成功判定；
- `Services\ProcessRunner.cs`：外部进程执行、编码和输出；
- `Services\DiskDiscovery.cs`：APA 磁盘发现；
- `Services\NbdClient.cs`：NBD 映射和断开；
- `Services\VirtualFileClipboard.cs`：Windows 延迟虚拟文件剪贴板；
- `Models\PfsEntry.cs`：PFS 条目模型；
- `app.manifest`：UAC 清单；
- `PS2-HDD-File-Manager.csproj`：项目和工具依赖；
- `README.md`：使用与构建说明。

## 14. 当前最后状态

最后一次正式构建成功：

- 0 个警告；
- 0 个错误；
- 窗口标题已经是 `PS2 HDD File Manager v1.0 — 峰哥原创，免费发布`；
- UI 上“复制到 Windows”已经精简为“复制”；
- 来源列表占位项规则已修复；
- 右键菜单已加入；
- 多文件虚拟剪贴板的 COM 枚举器修复已加入并构建，但仍应进行一次 Windows 11 实机验证。

## 15. 重装系统后的恢复顺序

1. 恢复整个 `C:\GitHub\PFS-BatchKit-Manager\`；
2. 确保 `BAT` 工具文件存在；
3. 确保测试镜像另有备份；
4. 安装 .NET 8 SDK；
5. 在仓库根目录构建 Release；
6. 以管理员身份启动 EXE；
7. 使用本文件作为新 Codex 线程的上下文；
8. 首先验证测试镜像的 `__test` 分区；
9. 再验证单文件复制和多文件复制；
10. 最后验证真实 PS2 APA 硬盘和 NBD。

## 16. 重要边界

Codex 的原始线程上下文不是这个文件，也不是工作区里的某个源码文件。Codex 线程当前 ID 为：

`019f7fb6-865b-7d82-ac3f-c170c06fa566`

本文件是为了在新线程中恢复项目上下文而整理的可读备份；若需要完整原始聊天记录，应在 Codex 应用中保留或导出该线程。
