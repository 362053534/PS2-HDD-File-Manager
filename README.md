# PS2 HDD File Manager

两个独立客户端与一个 PS2 服务端，各自维护源码、依赖、测试和发布入口。

| 组件 | 目录 | 技术 | 构建入口 |
|---|---|---|---|
| Windows 客户端 | [Windows](Windows/README.md) | C# / Windows Forms / .NET 8 | `Windows/build-release.cmd` |
| macOS 客户端 | [macOS](macOS/README.md) | Swift / AppKit / SwiftUI | `macOS/build.sh` |
| PS2 服务端 | [ps2-server](ps2-server/README.md) | PS2SDK / C | `ps2-server/scripts/build.ps1` |

macOS 不引用或编译 Windows 的 Models、Services、Tests，不使用 .NET、Avalonia 或 Windows 可执行工具。两个客户端分别实现网络客户端，只共同遵循 [PS2 线上协议](ps2-server/PROTOCOL.md)。`pfsshell` 分别从 362053534 的源码编译为各平台原生二进制，不共用编译产物。

旧 Avalonia Mac 实现保留在 Git 历史中；Windows 功能说明见其目录。macOS 迁移状态与验证限制见独立 README。

## 旧 Actions 兼容入口

继续使用单个 `.github/workflows/build.yml`，由三个 job 分别构建和上传。根目录 `Tests/PS2-HDD-File-Manager.Tests.csproj` 只转接 Windows 测试；根目录 `build-release-mac.sh` 只转接 Swift 构建。平台源码和依赖仍完全分离。

Actions 分别提供 `PS2-HDD-File-Manager-Release-windows.zip`、`PS2-HDD-File-Manager-Release-macos.zip` 和 `ApaNetServer.ELF`。PS2 产物通过 release 目标去符号并由 ps2-packer 压缩，直接下载 ELF，不再额外包装 ZIP；PS2 屏显和 ELF 识别名称统一为 `ApaNetServer`。
