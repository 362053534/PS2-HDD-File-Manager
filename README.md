# PS2-HDD-File-Manager（第一版）

一个不把 PFS 分区挂载为 Windows 盘符的 PS2 PFS 图形浏览器。界面将操作转换为短生命周期的 `pfsshell` 会话：每次读取、导入或导出结束后均执行 `umount` 和 `exit`。

## 当前功能

- 来源列表只显示由 `hdl_dump` 识别为 PS2 APA 格式的硬盘；也可手动选择 PS2 HDD 原始镜像；
- 用 `pfsshell` 列出 APA/PFS 分区；
- 在窗口内浏览目录、进入目录、返回上层；
- 导出单个文件；
- 可直接导入文件、创建目录、重命名和删除文件；
- 通过 `wnbd-client` 建立或断开名为 `PS2HDD` 的 NBD 映射，再从刷新后的物理磁盘列表选择它。

## 不包含的内容

- 不使用 Dokan 或 `pfsfuse`，不会创建 Windows 盘符；
- 首版不递归导出目录、不递归导入文件夹，也不删除非空目录；
- WNBD 驱动安装未内置。网络模式要求用户已根据原项目说明安装 WNBD 驱动，并正确配置 PS2 端 NBD Server。

## 构建和运行

在仓库根目录执行：

```powershell
dotnet build .\PS2-HDD-File-Manager\PS2-HDD-File-Manager.csproj -c Release
```

运行输出目录下的 `PS2-HDD-File-Manager.exe`。构建会自动将原项目的 `pfsshell.exe`、`wnbd-client.exe` 和 `libwnbd.dll` 复制到输出目录的 `tools` 文件夹。

对物理磁盘的读取和所有写入操作通常需要以管理员身份运行。写入 PFS 具有风险；请先备份硬盘或镜像。

## 开发测试镜像

开发目录可包含 `TestData/test-ps2-hdd.img`。该文件是一个 1 GiB 的独立 APA/PFS 测试盘，含 `__test` PFS 分区与 `TEST` 目录；它被 Git 忽略，也不会随发行包复制。运行时可用“加载硬盘镜像”选择它，用于验证导入、导出、重命名、删除和目录操作，而不影响真实 PS2 HDD 或 U 盘。
