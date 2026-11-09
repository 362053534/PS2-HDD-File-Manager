# PS2-HDD-File-Manager

PS2 PFS 图形文件管理器（窗口标题：`PS2 HDD File Manager v1.0 — 峰哥原创，免费发布`）。不把 PFS 分区挂载成 Windows 盘符（不使用 Dokan / `pfsfuse`），所有浏览与读写都在窗口内完成。

## 两种来源，同一套界面

| 来源 | 后端 | 关键行为 |
|------|------|----------|
| 本地 PS2 硬盘（`\\.\PhysicalDriveN`）或硬盘镜像文件 | `tools\pfsshell.exe` 子进程 | 每次操作开一个短生命周期会话：`device` → `mount` → `cd…` → 命令 → `umount` → `exit`。分区命令（`mkpart` / `rmpart` / `rename`）不挂载分区，因此不发 `umount`。批量导入会尽量把多个文件塞进**同一个会话**（`PfsShellClient.ImportBatchAsync`），共享会话失败时自动回退为逐文件导入。 |
| PS2 主机上的 `ApaNetServer.ELF`（PFS 网络） | 自研 PfsNet 协议，见 [`ps2-server/PROTOCOL.md`](ps2-server/PROTOCOL.md) | 控制口 TCP **45061**；Put/Get 载荷由 PS2 **回连** PC 监听的 **45062**。PC 防火墙需放行入站 45062。与 HDLGameInstaller 同端口，**不要同时运行**。 |

物理盘列表由 `tools\hdl_dump.exe query` 识别：只保留输出中含 `Playstation 2 HDD` 的行，并把 `hddN:` 映射为 `\\.\PhysicalDriveN`。也可用「加载硬盘镜像」直接打开镜像文件。

> **NBD 现状**：`wnbd-client.exe` 映射 `hdd0` 的代码（`Services/NbdClient.cs`、连接/断开逻辑）仍然保留，但**界面已隐藏**（`MainForm.cs` 中 `nbdBar` 的 `Visible = false`，注释为「NBD UI 暂时隐藏，代码保留待后续开发」）。PfsNet 与 NBD 互斥：NBD 映射进程在运行时连接 PFS 网络会被拒绝。

## 当前功能

### 分区列表

- 本地：`pfsshell ls` 解析分区名后，逐个探测挂载以过滤出**可浏览的 PFS 分区**。
- 网络：`LIST_PARTITIONS` 取回后按 `is_pfs` 过滤，并显示每个分区的容量。
- 「显示系统分区」开关（默认关闭，勾选需确认）：`__mbr`、`__net`、`__system`、`__sysconf`、`__common`、`__boot` 为受保护名 —— 开启后**可以读写分区内的文件**，但始终禁止删除/重命名这些分区本身。

### 文件与目录

- 双击进入目录/分区、「上级目录」返回、`F2` 或菜单重命名、`Delete` 删除（在分区列表上按 `Delete` 等于删除分区）、`Ctrl+A` 全选。
- 快捷键：`Backspace` 上级目录（焦点不在输入框时，窗体级处理）、`Escape` 取消正在进行的传输。
- 删除目录会**递归**展开子项后逐个删除，最后 `rmdir`（`MainForm.DeleteRemoteDirectoryAsync`）。
- 新建目录、导入前冲突检测（递归比对同名项），冲突时弹「全部覆盖」确认：同名文件替换，同名目录合并。
- 导入前做**空间预检**：按 PFS zone 大小估算净占用（覆盖时把将被替换项释放的空间计入额度），不足则不启动写入。

### 与 Windows 交换

- `Ctrl+C` / 右键「复制」：把选中的文件或目录注册成 Windows **虚拟文件剪贴板**，点复制时不读盘，粘贴目标取数据时才传输，支持目录树。实现的格式：`FileGroupDescriptorW` / `FileGroupDescriptor`（`FD_UNICODE`）+ `FileContents`（`TYMED_ISTREAM` 按需生成）+ `Preferred DropEffect`（`Services/VirtualFileClipboard.cs`）。
- `Ctrl+V` / 右键「粘贴」：从资源管理器剪贴板导入（不走 `GetFileDropList`，避免只粘贴到第一个文件）。
- 从资源管理器**拖入**窗口即导入（走 `WM_DROPFILES`，并放宽消息过滤以接受非提权进程拖放）。
- **拖出**到资源管理器：仅本地 pfsshell 来源支持；PFS 网络来源会提示不支持（请用复制 + 粘贴）。
- 右键「导出」：导出**单个文件**到本地目录。目录请用「复制」→ 在资源管理器粘贴。

### 分区管理（APA）

- 新建 PFS 分区：名称须匹配 `^[+A-Za-z0-9_.-]{1,32}$`；容量写整数加 `M`/`G`，必须是 **128 MiB 的整数倍**，单个逻辑卷上限 2 TiB。两步对话框，第二步需勾选「已备份重要数据」才允许写入。
- 重命名分区、删除分区（受保护名不可操作）。
- 写前与写后都会重新解析 APA 分区表复核：本地直接读盘（`ApaPartitionTableReader`），网络通过 `READ_SECTORS` 拉扇区（`PfsNetApaSectorSource`，单次 ≤256 扇区）喂给同一套解析逻辑。变更前会先 `UMOUNT`。

### 容量显示

状态栏上方一行四项：硬盘总计、未分配、当前分区、当前分区剩余。

- 本地：`ApaSpaceReader` 读 APA 头链算总量/未分配，`PfsSpaceReader` 扫 PFS 位图算分区剩余。
- 网络：`GET_APA_FREE` / `GET_PFS_FREE`；旧 ELF 的 `GET_PFS_FREE` 返回总容量 0 时回退用 `LIST_PARTITIONS` 的容量。

### 其它

- 传输反馈：进度条、实时速度（`ToolStripSpeedLabel`）、「取消传输」按钮；导入按总字节推进，碎文件不刷屏。
- 安全弹出：`CM_Request_Device_Eject`，按 VetoType 区分「文件被占用」「不支持弹出」等文案；非热插拔盘只提示已可安全移除；镜像文件改为「仅从列表移除」；PFS 网络来源转为「断开 PfsNet」。
- 「诊断输出」标签页保留 pfsshell 会话原文与全部操作日志。
- 文件名编码：PFS 目录项存 UTF-8 原始字节；pfsshell 侧按 Latin-1 保真传输 UTF-8 字节，显示时再解码（`PfsNameEncoding`）。**不要**用 `StringSplitOptions.TrimEntries` 切路径 —— UTF-8 续字节 `0xA0`（如「传」）会被当 NBSP 剥掉，导致 `cd` 找不到目录。
- 上次连接的 PFS 网络 IP 记在 `%LocalAppData%\PS2-HDD-File-Manager\last-pfsnet-ip.txt`。

## 尚未实现

| 项目 | 现状 |
|------|------|
| 「一键下载ART2图集」按钮 | 占位。点击只提示「该功能尚未实现」，预留给「按游戏 ID 下载到当前分区 ART2/」(`MainForm.DownloadArt2Async`) |
| 目录导出 | 右键导出仅支持单文件（提示「目录导出将在后续版本加入」）。目录请走复制 + 粘贴 |
| PFS 网络拖出到 PC | 明确不支持（避免 staging 双写与 0 KB 占位），改用复制 + 粘贴 |
| `CMD_STAT` / `CMD_GET_SESSION` / `CMD_SHUTDOWN` | 协议常量已定义，PS2 端未实现（返回 `ERR_UNSUPPORTED`），PC 端也不调用 |
| 心跳与空闲断连清理（协议 P4） | `PfsNetClient.PingAsync` 目前无调用点，没有 10 s 心跳；PS2 端也没有 30 s 无帧判死。目前只有控制连接断开时强制 `umount` |
| WNBD 驱动安装 | 未内置，界面也已隐藏 |

## 构建和运行

### 前置依赖（不在本仓库内）

`PS2-HDD-File-Manager.csproj` 用 `None Include` 把外部工具复制进输出的 `tools\`，这些路径**都在仓库之外**，必须先在 Windows 上就位：

| 复制到 | 引用路径（相对本仓库） |
|--------|------------------------|
| `tools\hdl_dump.exe` | `..\PFS-BatchKit-Manager\BAT\hdl_dump.exe` |
| `tools\wnbd-client.exe` | `..\PFS-BatchKit-Manager\BAT\wnbd-client.exe` |
| `tools\libwnbd.dll` | `..\PFS-BatchKit-Manager\BAT\libwnbd.dll` |
| `tools\pfsshell.exe` | `..\..\pfsshell\build-local-win32\pfsshell.exe` |

`pfsshell.exe` 使用源码编译产物，不从 `BAT` 目录拷贝：`build-release.ps1` 会调用 `C:\GitHub\BuildTools\scripts\build-pfsshell-win32.ps1` 从源码构建（用 `-SkipPfsshell` 跳过）。因此**单独 clone 本仓库无法直接编译**。

### 构建

```powershell
.\build-release.cmd                  # 编译 pfsshell → 编译 Release → 打 zip
.\build-release.ps1 -SkipPfsshell    # 跳过 pfsshell 编译
dotnet build .\PS2-HDD-File-Manager.csproj -c Release   # 仅编译
```

`Release` 构建结束后，csproj 里的 `ZipReleaseOutput` 目标会在 `bin\Release\` 生成 `PS2-HDD-File-Manager.zip`（剔除 `*.pdb`）。这一步用的是 PowerShell 的 `Compress-Archive`（即 `System.IO.Compression`），**不依赖 7-Zip**。

### 运行

运行 `bin\Release\net8.0-windows\PS2-HDD-File-Manager.exe`。`app.manifest` 声明 `requireAdministrator`，程序会请求管理员权限 —— 读物理盘与所有写操作都需要。

写入 PFS 与改动 APA 分区表都有风险，操作前请先备份硬盘或镜像。

## 测试

`Tests/` 是一个控制台程序（**21 条断言**，进程退出码 = 失败数），只覆盖不依赖硬件的纯逻辑：

- `PfsNameEncoding` 与 `PfsShellClient.ParseDirectoryEntries`：UTF-8 ↔ Latin-1 保真、`ls -l` 解析、路径切分（含 `0xA0` 续字节回归）。
- `DiskSafeEject`：VetoType 5 / 8 的失败文案，以及非热插拔盘的安全移除提示。
- `PfsNetProtocol.FormatCommandError`：`__` 前缀分区删除失败的专用提示。

主项目 `Program.cs` 通过 `[assembly: InternalsVisibleTo("PS2-HDD-File-Manager.Tests")]` 暴露 internal 类型给测试。

在 Windows 上运行（测试工程为 `net8.0-windows` + WinForms，Linux/macOS 上无法构建）：

```powershell
dotnet run --project Tests\PS2-HDD-File-Manager.Tests.csproj
```

## 持续集成（GitHub Actions）

`.github/workflows/build.yml`，在 `push` / `pull_request` / 手动触发时跑两个 job，产物在 run 页面底部的 **Artifacts** 下载。

| Job | Runner | 产物 |
|-----|--------|------|
| `PC Release (net8.0-windows)` | `windows-latest` | `PS2-HDD-File-Manager-Release-windows.zip`（即 `bin\Release\PS2-HDD-File-Manager.zip`，已含 `tools\`） |
| `PS2 ELF (ApaNetServer)` | `ubuntu-latest` + `ghcr.io/362053534/ps2homebrew:main` | `ApaNetServer.ELF` |

PC 端 zip 用 `actions/upload-artifact@v7` + `archive: false` **直传**（不再套一层 artifact zip），下载即得可分发 zip 本身，解压一次就能用；`archive: false` 只支持单个文件、且 artifact 名取文件名（`name` 被忽略）。`ApaNetServer.ELF` 经过 Release 去符号和 ps2-packer 压缩，使用 `archive: false` 直接下载 ELF，不再封装 ZIP。

pfsshell 不再单独出 artifact：meson 编出的 `pfsshell.exe` 经 csproj 的 `Link="tools\pfsshell.exe"` 直接进输出的 `tools\`，随发行 zip 一起走。作为替换，CI 的「校验产物」步骤会断言 zip 内确实有 `PS2-HDD-File-Manager.exe` + `tools\{pfsshell.exe,hdl_dump.exe,wnbd-client.exe,libwnbd.dll}` 且不含 `*.pdb`。

### 耗时与缓存

PC job 稳态实测 **93 s**（缓存命中）/ 231 s（冷缓存或 pfsshell 有更新）。时间几乎都花在依赖侧而不是本仓库代码：命中缓存后剩下的分布是「`setup-dotnet` 31 s」+「编 Release 主工程+Tests 33 s」+「两次 checkout 11 s」+「上传 5 s」+ 前后处理 ~13 s。

| 手段 | 效果 |
|------|------|
| `pfsshell.exe` 按上游 commit SHA 用 `actions/cache` 预热（key `pfsshell-exe-<os>-<arch>-<sha>`，只缓存那一个 exe） | 命中即整段跳过「clone 源码 + 装 MSYS2 + meson 编译」，省 ~118 s。只有 `362053534/pfsshell` 的 `master` 变了才真重编；SHA 解析失败时自动退回全量编译，不会静默用错缓存 |
| 主工程与 Tests 合成一次 `dotnet build Tests -c Release`（`ProjectReference` 会把主工程一起带成 Release），回归测试改 `dotnet run --no-build` | 时间上基本持平（省掉的重复编译 ≈ 合并带来的额外调度），真正的收益是**回归测试跑的就是要发布的 Release 二进制**——以前它跑 Debug、发的却是 Release |
| PC 端 zip 用 `upload-artifact@v7` + `archive: false` 直传 | 少一层零收益的重复压缩 |

还能再省但**没做**的两项，按需自取：

- `setup-dotnet` 的 31 s：`dotnet-version: 8.0.x` 是个**区间**，动作会去解析"最新 8.0.x"再把镜像里没带的那个补丁版本下载下来。`windows-latest` 自带 SDK `8.0.130 / 8.0.206 / 8.0.319 / 8.0.424`（另有 9.x、10.x），把版本**钉成镜像已有的**（如 `8.0.424`）就只剩"命中 tool cache + 改 PATH"，几秒完事；代价是镜像轮换后要记得跟一次版本号——不跟也不会失败，只是退回"再下载一次"。整步删掉也能跑，但那是拿 SDK 10 去编 `net8.0-windows`（编译器版本跟着镜像漂、还要从 nuget.org 拉引用包），发布链路不建议。
- 纯文档提交可用 `on.push.paths-ignore: ['**/*.md']`，让 PC job 根本不触发。

PC job 会在 runner 上按 `csproj` 写死的相对路径还原依赖布局，因此本仓库被检出到 `<workspace>/PFS-BatchKit-Manager/PS2-HDD-File-Manager`：

```
<workspace>/
├── pfsshell/build-local-win32/pfsshell.exe        ← csproj 的 ..\..\pfsshell\...
└── PFS-BatchKit-Manager/
    ├── PFS-BatchKit-Manager/BAT/{hdl_dump,wnbd-client}.exe + libwnbd.dll   ← ..\PFS-BatchKit-Manager\BAT\...
    └── PS2-HDD-File-Manager/                      ← 本仓库
```

上游依赖全部取自 362053534：

| 依赖 | 来源 | 用法 |
|------|------|------|
| `pfsshell.exe` | `362053534/pfsshell`（`master`） | MSYS2 MINGW32 下 `meson setup/compile`，产物即 `build-local-win32/pfsshell.exe`，由本仓库 csproj 直接复制进 `tools\`。`meson.build` 的 Windows 分支还有个无条件 `custom_target('archive', build_by_default: true)`，命令用 `find_program('7z')`（`required` 默认 true），故 MSYS2 必须装 `p7zip`，否则连 `meson setup` 都过不去。它产出的 `pfsshell-*-win32.7z` 我们**不消费也不上传**，`p7zip` 只是 meson 配置期的硬依赖 |
| `hdl_dump.exe` / `wnbd-client.exe` / `libwnbd.dll` | `362053534/PFS-BatchKit-Manager`（`main`） | sparse-checkout 只取 `PFS-BatchKit-Manager/BAT/` 下的这 3 个文件，避免拉整个仓库 |
| PS2 工具链 + SDK | `ghcr.io/362053534/ps2homebrew:main` | 镜像内 `PS2SDK=/usr/local/ps2dev/ps2sdk` 是**已编译**的 SDK（由 `362053534/ps2toolchain` + `ps2sdk` + `ps2sdk-ports` 构建），含 `iop/irx/*.irx`。注意别用 `/usr/local/ps2sdk`，那只是源码 clone |

PC job 还会跑 `Tests/`（退出码 = 失败断言数），失败即 job 失败。

## 开发测试数据

- `TestData/*.img` 被 Git 忽略（`TestData/.gitignore`），也不会随发行包复制。开发目录可自备一个 `test-ps2-hdd.img`（**该镜像不在仓库内**）：1 GiB 的独立 APA/PFS 测试盘，含 `__test` PFS 分区与 `TEST` 目录，用「加载硬盘镜像」打开即可验证导入、导出、重命名、删除和目录操作，不影响真实 PS2 HDD 或 U 盘。
- 仓库内附带 `TestData/中文测试/`（`测试A.pdb`、`测试文件.txt`），作为中文文件名的导入 / 剪贴板手工测试样本，没有代码引用。

## 目录结构

```
MainForm.cs                  单窗口 UI + 全部交互流程（本地 / 网络两条路径）
Models/                      PfsEntry
Services/                    pfsshell 会话、进程执行、盘符发现、安全弹出、
                             APA/PFS 空间与分区表解析、剪贴板与虚拟文件
Services/PfsNet/             PfsNet 协议常量、编解码、客户端、APA 扇区源
Tests/                       断言式回归测试（见上）
ps2-server/                  PS2 端 ELF 源码、协议文档与已构建的 ApaNetServer.ELF
build-release.ps1/.cmd       Windows 发布脚本
```

PS2 端的实现状态、协议细节与编译方式见 [`ps2-server/README.md`](ps2-server/README.md) 与 [`ps2-server/PROTOCOL.md`](ps2-server/PROTOCOL.md)。
