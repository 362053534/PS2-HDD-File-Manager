# ps2-server（PfsNet P3）

PS2 端 PFS 网络服务 ELF：开机自动取 IP 并屏显，监听 **45061**，PC 用「PFS 网络」连接。产物为 `ApaNetServer.ELF`。

协议定义见 [`PROTOCOL.md`](PROTOCOL.md)；PC 侧对应实现在 `../Windows/Services/PfsNet/`；macOS 独立实现在 `../macOS/Sources/APACore/`。

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

- **硬盘不自动待机**：每次启动加载 HDD 栈后，对 `hdd0:` 执行 `HDIOC_IDLE`，传入一字节 `0`，关闭 ATA 自动待机计时器；屏显成功或失败码。不需要配置文件，不进行周期保活读写。此设置用于本次服务运行，不保证跨断电、IOP 重置或其他 ELF 持续生效，也不覆盖硬盘/转接器独立的节能策略。正常退出仍执行 `HDIOC_DEV9OFF`。避免空闲起转等待不等于修复传输断流或提高持续吞吐。
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

`make all` / `make release` 默认生成压缩的 `ApaNetServer.ELF`（PS2 自解压 ELF），处理顺序与 362053534 OPL 一致：链接 → 去符号 → `ps2-packer`。去符号文件仅为中间产物，构建后自动清理，不单独发布；构建目录的 `ApaNetServer-debug.ELF` 用于调试。CI 仅发布压缩版。压缩 ELF 在所有加载段之外的文件尾保留程序名称，供产物识别与 CI 校验使用，不改变解压代码或加载内容。

压缩版需验证实际启动器兼容性；压缩不降低服务解压后的内存需求，也不提升网络传输速度。

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

> 仓库包含历史 `.o`，不要用它们作为增量构建缓存；必须先 `make clean`。构建脚本与 CI 均会执行清理。改了 `src/` 或 `include/` 后，提交的 ELF 必须来自对应源码的干净构建，不能把旧 ELF 当作新源码的测试结果。

## 与 HDLGameInstaller

同端口 45061/45062，**不要同时运行**。握手魔数为 `PFSN`，误连会以 `ERR_MAGIC` 拒。
PC 防火墙需放行入站 **TCP 45062**（Put/Get 数据回连）。

## 传输与取消的实机回归

在连接 PS2 的 Mac 上，从仓库根目录执行；需要 Python 3，先断开图形客户端，避免单客户端服务被另一条连接接管。以下命令会在 `+OPL` 中新建随机命名的测试目录，不覆盖已有文件，也不会自动删除测试目录。长测在本地终端运行，保持终端打开，避免远程执行工具的超时干扰。

```sh
# 只验证握手和 PING，不写文件
python3 scripts/pfsnet-transfer-probe.py 192.168.1.10

# 上传中排队一个 PING；上传完成后读其响应，再完整下载校验 SHA-256
python3 scripts/pfsnet-transfer-probe.py 192.168.1.10 --put-mib 64 --ping-after-mib 16

# 验证客户端现有的“数据口 RST → ABORT”、临时文件清理及重连
python3 scripts/pfsnet-cancel-probe.py 192.168.1.10

# 长传输；需要至少 6 GiB 空间，并完整回读校验
python3 scripts/pfsnet-transfer-probe.py 192.168.1.10 --put-mib 6144
```

探针遇到第一次错误即失败，不自动重试掩盖断流。长传输探针设置与正式客户端相同的 TCP KeepAlive（60 秒、15 秒间隔、10 次重试）。日志为 JSON Lines，记录已完成发送的字节数、阶段与结果；Mac 还记录 TCP 窗口、重传、RTT 和上传期间的控制连接状态。发送完成不代表落盘完成，只有 `PUT_END` 成功且回读 SHA-256 一致才输出 `verified`。这类实机测试不能用主机上的 socket 模拟替代。

### 统一产物名称

`make`、`make all`、`make release` 均生成压缩版 `ApaNetServer.ELF`；处理顺序为链接、去符号、ps2-packer 压缩。PS2 屏显和 ELF 尾部识别名称均为 `ApaNetServer`。旧名称的兼容副本不再生成。

统一工作流中的 PS2 job 执行 `make release`，校验并上传 `ps2-server/ApaNetServer.ELF`，使用 `archive: false` 直接提供 ELF 下载，不再额外包装 ZIP。Windows 和 macOS 客户端保持独立的发布文件名。
