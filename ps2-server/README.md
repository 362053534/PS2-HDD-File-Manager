# ps2-server（PfsNet P3）

无操作网络 PFS 服务：开机自动 DHCP/静态 IP，监听 **45061**，屏幕显示 IP。PC 用「PFS 网络」连接。

## 依赖

参考源与编译镜像**只允许** [362053534](https://github.com/362053534) 的仓库，见仓库根目录 [`AGENTS.md`](../AGENTS.md)。不要对照官方 OPL / ps2dev SDK。

1. **PS2SDK**（362053534 fork）：`C:\GitHub\ps2sdk`（路径/UTF-8 字节按本工具约定透传）
2. 该 SDK **已编译出 IRX**（至少含 `iop/irx/ps2fs.irx`、`ps2hdd.irx`、`netman.irx`、`smap.irx` 等）
3. **PS2 工具链**：`mips64r5900el-ps2-elf-gcc` + `bin2c`（通常在 WSL 的 `/usr/local/ps2dev`）
4. **OPL 行为对照**：`C:\GitHub\Open-PS2-Loader`（https://github.com/362053534/Open-PS2-Loader）
5. **Docker 编译镜像**：`ghcr.io/362053534/ps2homebrew:main`

## 本阶段（P3）已实现

- HELLO / PING  
- LIST_PARTITIONS / GET_APA_FREE / GET_PFS_FREE  
- MOUNT / UMOUNT（M1，断线强制 umount）  
- LIST_DIR / MKDIR / RMDIR / REMOVE / RENAME  
- PUT / GET（D1 数据回连 45062 + T1 临时名写入后 rename）  
- READ_SECTORS（A1，控制通道，单次 ≤256 扇区）  
- APA_CREATE_PFS / APA_REMOVE / APA_RENAME（须先 umount）  
- features：`FEAT_FILE_OPS | FEAT_DATA_CHANNEL | FEAT_APA_SECTOR | FEAT_APA_MUTATE`

## 编译

在已安装工具链的 WSL 中：

```bash
export PS2SDK=/mnt/c/GitHub/ps2sdk
export PS2DEV=/usr/local/ps2dev   # 按你的安装路径
export PATH=$PS2DEV/ee/bin:$PS2DEV/iop/bin:$PS2SDK/bin:$PATH
cd /mnt/c/GitHub/PFS-BatchKit-Manager/PS2-HDD-File-Manager/ps2-server
make clean && make
```

或在 Windows：

```powershell
.\scripts\build.ps1 -Docker
```

产物：`PfsNetServer.elf`

## 与 HDLGameInstaller

同端口 45061/45062，**不要同时运行**。握手魔数为 `PFSN`，误连会拒。
PC 防火墙需放行入站 **TCP 45062**（Put/Get 数据回连）。
