# ps2-server（PfsNet P3）

PS2 端 PFS 网络服务 ELF：开机自动取 IP 并屏显，监听 **45061**，PC 用「PFS 网络」连接。产物为 `ApaNetServer.ELF`。

协议定义见 [`PROTOCOL.md`](PROTOCOL.md)；PC 侧对应实现在 `../Services/PfsNet/`。

## 依赖

参考源与编译镜像**只允许** [362053534](https://github.com/362053534) 的仓库，见仓库根目录 [`AGENTS.md`](../AGENTS.md)。不要对照官方 OPL / ps2dev SDK。

1. **PS2SDK**（362053534 fork）：`C:\GitHub\ps2sdk`（路径/UTF-8 字节按本工具约定透传）
2. 该 SDK **已编译出 IRX**（至少含 `iop/irx/ps2fs.irx`、`ps2hdd.irx`、`netman.irx`、`smap.irx` 等）
3. **PS2 工具链**：`mips64r5900el-ps2-elf-gcc` + `bin2c`（通常在 WSL 的 `/usr/local/ps2dev`）
4. **OPL 行为对照**：`C:\GitHub\Open-PS2-Loader`（https://github.com/362053534/Open-PS2-Loader）
5. **Docker 编译镜像**：`ghcr.io/362053534/ps2homebrew:main`

## 本阶段（P3）已实现

命令分发表在 `src/fsops.c` 的 `pfsnetHandleCommand`；握手在 `src/server.c` 的 `handleHello`。

- HELLO / PING
- LIST_PARTITIONS / GET_APA_FREE / GET_PFS_FREE
- MOUNT / UMOUNT（M1 会话保持；断线强制 umount）
- LIST_DIR / MKDIR / RMDIR / REMOVE / RENAME
- PUT / GET（D1 数据回连 45062 + T1 临时名 `.pfsnet_<8 hex>` 写入后 rename）
- READ_SECTORS（A1，控制通道，单次 1–256 扇区，LBA ≤ 0xFFFFFFFF）
- APA_CREATE_PFS / APA_REMOVE / APA_RENAME（须先 umount）
- features：`FEAT_FILE_OPS | FEAT_DATA_CHANNEL | FEAT_APA_SECTOR | FEAT_APA_MUTATE`（`server.c:handleHello`）

### 未实现

| 项 | 现状 |
|----|------|
| `CMD_GET_SESSION` (0x0005)、`CMD_STAT` (0x0025)、`CMD_SHUTDOWN` (0x00FF) | `protocol.h` 里有常量，但 `pfsnetHandleCommand` 没有对应 `case`，落到 `default` 返回 `ERR_UNSUPPORTED(-10)`。PC 端也不调用 |
| 心跳 / 空闲断连清理（协议 P4） | 没有 30 s 无帧判死。`server.c` 里唯一的 idle 判定是 `pfsnetPadPollAllowed()`（收发中或停传未满 2 秒不轮询手柄），与连接存活无关。目前只有控制连接断开 / 协议致命错误时 `closeClient()` → `pfsnetFsForceUmount()` |
| 鉴权 / 加密 | 无（见 PROTOCOL.md §9） |

## 运行时行为

- **取 IP 顺序**（`src/main.c`）：① DHCP → ② 失败则读 OPL `conf_network.cfg` 的静态地址 → ③ 再失败读 uLE `IPCONFIG.DAT` → ④ 仍无可用地址则屏显 `FATAL: failed to obtain an IP address`，按 START 退出（不写兜底 IP）。
- 屏显最终状态：IP 与来源、NM/GW、Link UP/DOWN、`Control :45061  Data(PC listen):45062`。链路 DOWN 直接报错等 START。
- **单客户端**：`serverThread` 中若 `g_client.running` 仍在，新连接**立即 close**（不排队）。
- **停栈**：按 START → `safeExitElf()`：force umount → `HDIOC_DEV9OFF` → IOP `rom0:UDNL` 复位 → `_LoadExecPS2("rom0:OSDSYS", 0, NULL)`（不带参数，交给 OSDSYS 自己找 MC/HDD，才能回到 FHDB 选单）。
- 手柄轮询由后台低优先级线程做，收发期间与停传后 2 秒内不碰手柄（避免抢 IOP）。
- I/O：`fileXioSetRWBufferSize` 依次尝试 512 KiB → 256 KiB → 128 KiB；Put/Get 用两块 512 KiB `memalign` 缓冲 + `ioWorker` 线程双缓冲。
- 响应包头的 `command` 字段是占位值（填 `PFSNET_CMD_HELLO`）；业务结果只看响应头 `result`。

## 实现注意点（与 PC 端强耦合，改动需同步）

- **LIST_PARTITIONS 只返回 PFS 分区**：`fileXioDread` 循环里 `if (!isPfs) continue;`，flags bit0 只控制系统分区是否包含。子分区（`APA_FLAG_SUB`）跳过。
- **载荷布局**：LIST_PARTITIONS = `count:u32` + N×(`string name`, `flags:u8`（bit0=system，bit1=pfs）, `total_bytes:u64`)；LIST_DIR = `count:u32` + N×(`string name`, `is_dir:u8`, `size:u64`, `string details`（`"dir"`/`"file"`）)；GET_APA_FREE = `total:u64, free:u64`（16 字节，**无 zone**）；GET_PFS_FREE = `total:u64, free:u64, zone:u32`（20 字节）。
- **GET_PFS_FREE 需要已挂载**，否则 `ERR_NOT_MOUNTED`。分区总量在 mount 时缓存（`g_mountedTotalBytes`），为 0 时用 `getPartitionTotalBytes()` 累加主分区 + 子分区扇区。
- **APA_CREATE_PFS**：容量串 `整数 + M/G`，必须是 **128 的整数倍（MB）**，否则 `ERR_INVAL`。按 APA 尺寸表从大到小开主分区，再用 `HIOCADDSUB` 补子分区凑容量；随后 `fileXioFormat("pfs:", "hdd0:名字", {8192, 0x00002d66, 0})`（对齐 pfsshell `mkpfs`）。**空间不足时可能凑不满请求容量（`left > 0`）仍返回 OK**，由 PC 的写后 APA 复核决定是否报告/回滚。
- **APA_REMOVE**：`__mbr`/`__net`/`__system`/`__sysconf`/`__common`/`__boot` 直接 `ERR_INVAL`。标准 IOP `ps2hdd` 还会拒绝删除**任意 `__` 前缀**分区并返回 `-EACCES`（`mapErrno` 映射为 `ERR_INVAL`，不落到笼统的 `ERR_IO`）。要删 `__.POPS` 这类分区，需要定义 `APA_ALLOW_REMOVE_PARTITION_WITH_LEADING_UNDERSCORE` 编译的 `ps2hdd.irx`。PC 端对此有专门提示文案（`PfsNetProtocol.FormatCommandError`，有回归测试）。
- 已挂载或传输进行中执行 APA 变更 → `ERR_BUSY`；APA_REMOVE 会先自动 umount，APA_RENAME 不会。
- 断线兜底：`abortXferCleanup()` 关闭打开的文件并删除未提交的 `.pfsnet_*` 临时文件，然后 force umount。

## 编译

在已安装工具链的 WSL 中：

```bash
export PS2SDK=/mnt/c/GitHub/ps2sdk
export PS2DEV=/usr/local/ps2dev   # 按你的安装路径
export PATH=$PS2DEV/ee/bin:$PS2DEV/iop/bin:$PS2SDK/bin:$PATH
cd ps2-server
make clean && make
```

或在 Windows（用 362053534 的编译镜像）：

```powershell
.\ps2-server\scripts\build.ps1 -Docker -Image "ghcr.io/362053534/ps2homebrew:main"
```

产物：`ApaNetServer.ELF`。

> 仓库里的 `.o` 与 `ApaNetServer.ELF` 是**已提交的构建产物**。改了 `src/` 或 `include/` 之后必须重新编译并一并提交，否则 PC 端会对不上协议行为。

## 与 HDLGameInstaller

同端口 45061/45062，**不要同时运行**。握手魔数为 `PFSN`，误连会以 `ERR_MAGIC` 拒。
PC 防火墙需放行入站 **TCP 45062**（Put/Get 数据回连）。

## Release ELF

`make release`（`make all` 同样调用该目标）先链接 `ApaNetServer-unstripped.ELF`，再用工具链的 `strip` 去符号，最后用 `ps2-packer` 生成 `ApaNetServer.ELF`。处理顺序与 362053534 OPL 一致。屏幕标题为 `ApaNetServer`；通信协议和内部 PfsNet 接口名称保持兼容。Actions 只直传最终的单个 ELF（`archive: false`），下载无需解压 ZIP。未去符号的 ELF 仅用于构建校验与调试，不上传。
