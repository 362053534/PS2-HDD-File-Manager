# ps2-server（PfsNet P2）

无操作网络 PFS 服务：开机自动 DHCP/静态 IP，监听 **45061**，屏幕显示 IP。PC 用「PFS 网络」连接。

## 依赖

1. **魔改 PS2SDK**：`C:\GitHub\ps2sdk`（你的 fork；路径/UTF-8 字节按本工具约定透传）
2. 该 SDK **已编译出 IRX**（至少含 `iop/irx/ps2fs.irx`、`ps2hdd.irx`、`netman.irx`、`smap.irx` 等）
3. **PS2 工具链**：`mips64r5900el-ps2-elf-gcc` + `bin2c`（通常在 WSL 的 `/usr/local/ps2dev`）

## 本阶段（P2）已实现

- HELLO / PING  
- LIST_PARTITIONS / GET_APA_FREE / GET_PFS_FREE（PFS free 为占位增强项）  
- MOUNT / UMOUNT（M1，断线强制 umount）  
- LIST_DIR / MKDIR / RMDIR / REMOVE / RENAME  
- PUT / GET（D1 数据回连 45062 + T1 临时名写入后 rename）  
- features：`FEAT_FILE_OPS | FEAT_DATA_CHANNEL`（APA 扇区/变更仍返回 `ERR_UNSUPPORTED`）

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
