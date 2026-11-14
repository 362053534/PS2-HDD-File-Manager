# APA File Manager — macOS 原生客户端

独立的 Swift / AppKit / Combine 应用，最低 macOS 12（含 Monterey 12.7.6）。没有第三方 Swift 包，不依赖 .NET、Avalonia、Windows 工程或 Windows 工具。发布包只包含主程序、Info.plist 和 macOS 原生 pfsshell。Windows 源码、测试与发布位于 ../Windows，各自演进。

## 构建与测试

安装 Apple Command Line Tools。pfsshell 必须来自 `362053534/pfsshell` 并编译为当前 CPU 架构的 Mach-O；Actions 的统一 `build.yml` 中，独立的 `mac-release` job 会编译它。

```sh
PFSSHELL_TEST_BIN=/path/to/pfsshell ./test.sh
PFSSHELL_BIN=/path/to/pfsshell ./build.sh
```

输出 `dist/APA-File-Manager-macOS.zip`，解压为 `APA File Manager.app`。本地构建使用 ad-hoc 签名，未包含 Apple Developer 公证。测试程序只使用系统 Swift/Foundation/Darwin，不要求安装完整 Xcode；未设置 PFSSHELL_TEST_BIN 时，本地镜像集成测试会明确标记 SKIP。Actions 必须提供该变量。

## 功能

- 连接 ApaNetServer，独立实现现有 V1 线上协议；端口 45061/45062，UTF-8 名称，64 位文件长度。
- 浏览 APA/PFS 分区、目录、总量和可用空间；网络与本地镜像均支持。
- 原生文件选择、拖入、Cmd+V 导入、导出、Cmd+C 拷贝到 Finder。拷贝先将文件下载到本应用缓存后提供原生 URL 剪贴板，不使用 Windows 的虚拟文件剪贴板实现。
- 递归目录导入/导出/删除；冲突与空间预检；覆盖及删除明确确认。
- 创建、删除、重命名 PFS 分区。写前检查 APA 校验和、链循环、越界、重叠和子分区引用；写后检查预期结果，创建后验证挂载与根目录。复核失败不继续写入、不自动删除未明确确认的分区。
- 网络取消先重置数据 socket，再发送 ABORT；TCP KeepAlive 为 60/15/10。数据口失败最多重试两次，检查源文件大小/修改时间/标识，使用服务端确认的偏移续传。PUT_END 状态不明时不自动重发提交。
- 下载写入同目录唯一临时文件，成功后才原子重命名；失败不会破坏已有目标文件。
- 本地通过原生 pfsshell 工作；覆盖先写临时文件并校验大小，再备份旧文件并提交。取消等待当前 pfsshell 文件操作完成及卸载，避免强杀写盘进程。
- 外接整盘选择、安全弹出、操作日志；写入期间通过系统活动令牌防止空闲睡眠。

## 平台边界

- macOS 不包含 Windows WNBD；旧版本不可用的 NBD 按钮和未实现的 ART2 下载入口不保留。
- 不提供格式化任意物理盘入口；集成测试仅初始化其自行创建的 UUID 临时镜像。
- 物理盘须有当前用户的读写权限；应用不修改系统权限、不索取密码、不自动提权，拒绝内置系统盘。
- 本地 pfsshell 命令格式无法安全表达含双引号/换行的路径，会明确拒绝，不静默改名。符号链接不递归导入。
- 当前发布构建为 arm64 + x86_64 通用版，包含 Intel 与 Apple Silicon。真实 PS2 的长时间大文件传输、旧版本 macOS、Intel Mac、物理盘权限与热拔插仍需独立验证，模拟测试与镜像测试不替代这些验证。

## 模块

`Sources/APACore`：协议编解码、原生 socket、APA 校验、网络/本地存储。
`Sources/APANative`：原生界面、操作队列、文件面板、剪贴板和系统电源管理。
`Tests/APACoreTests`：截断帧、中文/大文件长度、APA 损坏、双通道传输、取消、续传、下载保护、本地镜像完整生命周期。
`Tests/HardwareProbe.swift`：默认只读的实机探针，显式 `--transfer` 创建新 UUID 目录进行 64 MiB 上传与 SHA256 回读。

## 本轮验证（2026-10-21）

在用户的 Apple Silicon Mac（Swift 6.4 Command Line Tools）上完成 Release 编译、ad-hoc 签名校验、pfsshell 冒烟测试和 10 组核心/集成检查，均通过。临时镜像测试涵盖分区创建/改名/删除、中文文件导入/回读、覆盖替换、文件与目录操作。续传偏移的故意错误版本被测试检出。原生窗口成功启动并退出；屏幕截图权限不可用，PDF 导出无法完整反映 SwiftUI 图层，不作为视觉验收证据。

当前包内文件共 598,258 字节，ZIP 211,541 字节。仅四个文件：主程序、pfsshell、Info.plist、签名资源清单；不包含第三方运行时。尺寸会随编译器和签名略变。

PS2 实机探针未连接成功（SSH 内超时、本地 Terminal 返回 No route to host），因此未执行新客户端的实机写入和长传输。物理盘热拔插、完整人工界面操作、旧版 macOS/Intel 兼容性尚未验证。Windows 的 19 个 C# 源文件迁移前后逐字节一致，但迁移后的 Windows 构建仍需 Actions 验证。

## 旧工作流适配

根目录 `build-release-mac.sh` 设置 `LEGACY_CI=1`，仍调用本目录的 Swift 构建。为满足未修改的旧 yml，CI 使用 `PS2-HDD-File-Manager.app`、同名主程序和 `PS2-HDD-File-Manager-Release-macos.zip`，写入根目录 bin/Release。窗口标题与 Windows 保持一致。直接运行本目录 build.sh 则保留新的原生命名。

`--run-tests` 在 CI 工作区调用真实的 CoreChecks 二进制，并传入本次打包的原生 pfsshell，执行含本地镜像在内的全部测试；失败返回非零。CoreChecks 放在 app 外，不随 ZIP 发布，因此发行版的 --run-tests 会明确报告测试工具未分发，不会假报通过。

## Monterey 与通用构建

Swift Package、Info.plist、两种 Swift 编译目标和 pfsshell 均以 macOS 12.0 为最低版本，覆盖 Monterey 12.7.6。主程序与 pfsshell 都包含 arm64 和 x86_64。Actions 的 Mac job 设置相同部署目标，并使用新的通用工具缓存键，避免复用旧系统或单架构产物。Windows 与 PS2 的 job 保持不变。

先安装 Meson/Ninja 到独立 Python 虚拟环境（构建需要，不随应用打包），从 362053534/pfsshell 编译原生工具：

```sh
./build-pfsshell.sh /path/to/362053534-pfsshell /path/to/pfsshell-universal
PFSSHELL_TEST_BIN=/path/to/pfsshell-universal ./test.sh
PFSSHELL_BIN=/path/to/pfsshell-universal ./build.sh
```

打包前自动检查两个可执行文件的架构和 Mach-O 最低系统版本。单架构工具或最低系统高于 12.0 的工具将被拒绝，不通过改写二进制版本头来绕过限制。测试程序按当前构建机架构编译为 macOS 12.0 目标；交叉架构编译和版本检查不等于在 Intel Monterey 实机运行验证。

### 本轮验证（2026-11-14）

在 macOS 27.2.1 / Swift 6.4.2 上，以 12.0 部署目标编译 arm64 和 x86_64 主程序及 pfsshell，合并为通用版。两种架构的 10 组核心/镜像检查均通过（Intel 程序通过 Rosetta 执行），两种架构的窗口启动检查通过，签名校验通过。最终 ZIP 内的 plist、两个 Mach-O 的架构和最低系统版本已重新检查；最低版本为 12.0。负向检查确认拒绝单架构二进制及最低系统为 13.0 的通用二进制。尚未在真实 Monterey 12.7.6 系统或 Intel 硬件上运行，需由目标系统完成最终验收。

## Windows 操作布局对齐（2026-11-14）

原生 macOS 界面以当前分支 Windows/MainForm.cs 为操作参照；不引用 Windows 工程或 .NET。

- 顶部依次为来源下拉框、刷新、加载硬盘镜像、ApaNetServer连接/断开、安全弹出硬盘、容量、上级目录、显示系统分区及只读路径。
- PFS 浏览器 / 诊断输出为独立页签。浏览器列为名称 / 类型 / pfsshell 详情；保留本地与网络后端返回的详情，不从大小伪造。
- 分区根级菜单为新建 PFS 分区、重命名分区、删除 PFS 分区、刷新；分区内菜单为拷贝、删除、重命名、粘贴、新建目录、刷新。菜单的显示/禁用与当前位置、多选、剪贴板、忙碌状态一致。
- 右键未选中行会重新选择，右键已选中行保留多选，空白处右键取消选择。双击目录进入；F2、文件双击或再次点击已选名称可行内改名，Escape 取消改名。
- Cmd+A/C/V（也接受表格内 Ctrl+A/C/V）、F2、向前 Delete、Backspace 返回上级；Mac 的 Cmd+Delete 删除所选项目。文本框保留系统文字编辑快捷键。Escape 取消传输，取消按钮仅在传输中显示。
- 系统分区默认隐藏，显示前确认，禁止删除或重命名六个系统保留分区（大小写不敏感），允许浏览其中的文件。
- 分区创建在一个表单填写名称及容量，再勾选来源/备份确认；删除分区两次确认。导入先扫描，有冲突才询问“全部覆盖”，同名目录合并。保留原生后端已有的写入校验和本地临时文件覆盖保护；本地覆盖可能需要完整新文件的临时空间。
- 多来源保留在下拉框，可在镜像、物理盘和网络之间切换。镜像安全弹出仅移除列表项，网络安全弹出断开网络，物理盘使用 diskutil eject。
- 拖入文件、Cmd+V 导入、Cmd+C 导出至 Finder；拖出使用原生 NSFilePromiseProvider，多文件串行处理，捕获来源/分区/路径，切换来源后拒绝旧拖放任务。剪贴板拷贝仍先下载至独立缓存，完成后才提供文件 URL。
- ART2 与未实现的 NBD 不显示。保留 macOS 原生控件、字体、系统主题及 Cmd 快捷键；不模拟 Windows 的非客户区。

构建后运行 `./test-ui.sh`，或传入 app 路径。测试会启动真实 AppKit 窗口，检查菜单/选择/行内改名/忙碌状态和确认表单，再在自建 UUID 临时目录中初始化测试镜像，检查来源切换、目录进退、导入/覆盖/取消、改名、文件导出与回读。不会扫描或初始化用户物理盘，不连接 PS2，不更改系统安全权限。

完整 Finder 接收文件承诺、屏幕最终视觉效果、物理盘弹出和真实 PS2 网络交互需人工验收。PDF/离屏位图缺少部分系统控件文字，不能作为视觉一致性证据；远程会话不具备屏幕捕获权限。Intel 在当前机器通过 Rosetta 测试，不能替代真实 Intel Monterey 12.7.6。

## APA 创建容量预检

创建前按 362053534/ps2sdk 的 `apaGetPartitionMax`、`hddAddPartitionHere`、`apaInsertPartition` 规则只读模拟：实际单段上限、已有空闲块拆分、尾部对齐及最多一个主分区加 64 个子分区。表单默认填入当前布局可完整创建的容量；用户另填的容量也必须通过预检，写入前用新读取的布局再次检查，不静默缩小请求。

针对日志中的 `0x747059c0` 扇区、仅 `__mbr` 的布局，953728M 会提前被拒绝并提示 953344M；953344M 可完整创建。2TB 硬盘按其容量使用 32GiB 单段上限，不固定限制为 900 多 GiB。这里的可创建上限针对单个 PFS 卷及当前布局，不等于整盘容量限制。

pfsshell 构建会应用 `scripts/patch-pfsshell-create.py`：分配不足时关闭并清理本次新建、尚未格式化的分区，输出失败；无法清理时保留现场并报错，绝不把短分配格式化为成功。macOS 与 Windows 的工具缓存均带新修订键，避免继续使用旧行为。

`PFSSHELL_TEST_BIN=/path/to/patched/pfsshell ./test-large-partition.sh` 为额外集成验证：仅创建 UUID 临时稀疏镜像，重建日志布局，验证提前拒绝、后端短分配拒绝格式化/清理，以及实际创建并挂载 953344M。初始化范围固定为测试自己创建的镜像，不接受用户设备路径。该测试逻辑容量约 1TB，实际仅写入少量 APA/PFS 元数据；正常回归仍用小镜像及内存布局，不要求 CI 创建大镜像。

## 本地批量导入会话

本地导入在空间预检后复用一个原生 pfsshell 进程及挂载。命令逐条发送，收到带随机会话标识的结果才继续；不预先排队整批写入。遇到命令、底层磁盘或进程错误即停止后续文件，并通过关闭输入让工具正常卸载，不强杀写盘进程。失败后不会自动重开会话继续写入。

每个文件仍先写唯一临时名称，核验实际大小，覆盖时先备份原文件，再改名并同步 PFS 缓存；同步成功后才删除覆盖备份。取消等待当前命令完成，在提交前响应取消并尽力清理临时文件。硬件掉线仍可能留下临时文件或恢复备份，程序不会将它们报告为成功导入；大小核验不等同于完整内容校验。硬盘或转接设备故障不能由共享会话消除。

macOS 工具通过 scripts/patch-pfsshell-macos-session.py 增加逐命令回复和 apa_sync，仅由 macOS/build-pfsshell.sh 应用；Windows 工具和客户端不变。重新构建时必须使用此脚本生成的新工具，旧工具会明确拒绝共享会话。测试包含真实临时镜像覆盖回读、取消保留原文件，以及命令/磁盘错误/进程退出后停止后续写入。
