# PFS 网络协议草案（PfsNet）

状态：**V1 实现基线**（P1–P3 已双端落地；P4 未实现）  
适用：PS2 端 `ps2-server` ELF ↔ PC 端 PS2-HDD-File-Manager  
样板：HDLGameInstaller 的双 TCP 通道与包头风格；**命令语义与版本独立，不得与 HDLGMan 混连**

本文与代码的对齐关系：常量见 `include/protocol.h` ↔ `../Services/PfsNet/PfsNetProtocol.cs`；
命令分派见 `src/fsops.c:pfsnetHandleCommand` 与 `src/server.c:handleHello` ↔ `PfsNetClient`。
文中标注「**未实现**」的命令表示常量已定义但没有实现，发送后会收到 `ERR_UNSUPPORTED`。

---

## 0. 目标与非目标

### 目标

- PS2 ELF 开机后自动监听，用户极少操作（显示 IP / 连接状态即可）。
- PC 连接后，**文件/目录管理**与本地盘体验一致。
- **分区类操作**达到与本地同等的 APA 校验强度（写前/写后）。

### 非目标（本草案不覆盖）

- HDL 游戏安装 / OSD 属性区管理（仍由 HDLGameInstaller 负责）。
- NBD 块设备导出。
- 多客户端并发、鉴权/加密。

---

## 1. 已冻结产品决策（基线）

| 主题 | 决定 |
|------|------|
| 分区操作 | 策略 **A**：同等 APA 校验 |
| 文件/目录操作 | 策略 **B**：信任 PS2 `fileXio`/PFS |
| 挂载 | **M1** 会话保持；见 §6 |
| 分区菜单（A 未就绪） | **P1b**：可点，仅提示未启用 |
| 传输 | **D1** 控制 + 数据双通道 |
| 端口 | **R2** 控制 `45061`，数据 `45062`（与 HDL 同端口，**互斥运行**） |
| 文件名编码 | **E1 UTF-8**（与本地 `PfsNameEncoding` / pfsshell 一致） |
| 工程位置 | `PS2-HDD-File-Manager/ps2-server/` |
| 与 NBD | **N1** PC 侧互斥（协议层不感知 NBD） |
| Put 落盘 | **T1** 临时名写一次 + `rename` |
| 分区校验实现 | **A1** PS2 提供扇区只读；PC 复用 `ApaPartitionTableReader` |

---

## 2. 网络拓扑

```
PC                                      PS2 (PfsNetServer.elf)
 |                                         |
 |  TCP connect ---------------------------->|  listen :45061  (控制)
 |  握手 / RPC 命令                         |
 |                                         |
 |  listen :45062  <------------------------+  connect 回 PC   (数据，按需)
 |  大文件 Put/Get 载荷                      |
```

| 通道 | 端口 | 谁监听 | 用途 |
|------|------|--------|------|
| 控制 | 45061 | PS2 | 握手、挂载、目录/文件元数据命令、小载荷、读扇区、状态 |
| 数据 | 45062 | PC | 仅在 `Put`/`Get`（及可选批量读扇区）建立；传完关闭 |

约束：

- 同时只允许 **1** 个已握手客户端。
- 同一 PS2 **不得**同时运行 HDLGameInstaller 与本服务（端口冲突）。
- PC 防火墙需放行入站 **TCP 45062**（数据回连）。

---

## 3. 帧格式（控制通道）

所有多字节整数为 **小端**。结构与 HDL 风格对齐，但魔数/命令号独立。

### 3.1 包头（12 字节）

```c
struct PfsNetPacketHdr {
    uint32_t command;        /* 见 §5 */
    uint32_t payload_length; /* 后续载荷字节数，可为 0 */
    int32_t  result;         /* 请求方填 0；响应方填结果码，见 §8 */
};
```

规则：

- 请求：先发包头，再发 `payload_length` 字节载荷（若 >0）。
- 响应：同样先包头后载荷；业务结果以响应头 `result` 为准（`0` = 成功）。
- 单帧控制载荷上限：**131072**（128 KiB）。更大内容走数据通道或拆分命令。
- 实现说明：PS2 端响应包头的 `command` 字段是占位值（`src/server.c:pfsnetSendResponse` 填 `PFSNET_CMD_HELLO`），**不要**用它匹配请求；PC 端 `PfsNetClient` 按「一问一答」顺序读响应。
- PC 端每次读响应有 **30 秒**超时（`PfsNetCodec.ReadExactAsync`），超时抛 `TimeoutException("等待 PfsNet 响应超时。")`。

### 3.2 字符串

- 编码：**UTF-8**，无 BOM。
- 字段采用「长度 + 字节」：

```c
/* 线上：uint16_t byte_len; uint8_t bytes[byte_len];  不含 NUL */
```

- 路径使用 **POSIX 风格**，以 `/` 分隔，分区内路径例：`/`、`/APPS/tools`。
- 分区名：APA 分区名字符串（如 `+OPL`、`__common`），UTF-8；与本地工具显示名一致的字节约定见 PC `PfsNameEncoding`（写入 PFS 的为 UTF-8 原始字节）。

---

## 4. 握手与版本

握手必须在任意业务命令之前完成。用于拒绝 HDL 客户端 / 旧版客户端。

### 4.1 常量

```text
PFSNET_MAGIC            = 0x4E534650   /* 'P''F''S''N' 小端内存为 PFSN */
PFSNET_PROTOCOL_VERSION = 0x0001
```

**禁止**使用 HDLGMan 的 `0x0C` 作为本协议版本号。

### 4.2 流程

1. PC → `CMD_HELLO`（0x0001），载荷：

   ```c
   struct HelloRequest {
       uint32_t magic;    /* PFSNET_MAGIC */
       uint32_t version;  /* PFSNET_PROTOCOL_VERSION */
   };
   ```

2. PS2 → 响应 `result`：
   - `0`：成功；载荷 `HelloReply { uint32_t magic; uint32_t version; uint32_t features; }`
   - `ERR_VERSION`：版本不支持
   - `ERR_MAGIC`：魔数不对（例如误连 HDL 服务）

3. `features` 位标志（草案）：

   | Bit | 名称 | 含义 |
   |-----|------|------|
   | 0 | `FEAT_FILE_OPS` | 文件/目录 B 类命令可用 |
   | 1 | `FEAT_DATA_CHANNEL` | 支持 45062 数据通道 |
   | 2 | `FEAT_APA_SECTOR` | 支持 A1 扇区只读（分区校验） |
   | 3 | `FEAT_APA_MUTATE` | 支持分区创建/删除/重命名（需 FEAT_APA_SECTOR） |

未设置的 feature，对应命令应返回 `ERR_UNSUPPORTED`。

V1：`FEAT_FILE_OPS | FEAT_DATA_CHANNEL | FEAT_APA_SECTOR | FEAT_APA_MUTATE`（P3 已实现）。

---

## 5. 命令一览

### 5.1 会话 / 挂载（M1）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0001 | `CMD_HELLO` | 握手 |
| 0x0002 | `CMD_PING` | 心跳；载荷可空；响应 `result=0`。**已实现但当前无调用方**（见 §6.3） |
| 0x0003 | `CMD_MOUNT` | 挂载指定 PFS 分区为当前会话挂载 |
| 0x0004 | `CMD_UMOUNT` | 卸载当前挂载 |
| 0x0005 | `CMD_GET_SESSION` | **未实现**（返回 `ERR_UNSUPPORTED`）；挂载状态 PC 端自行记录在 `PfsNetClient.MountedPartition` |

`CMD_MOUNT` 载荷：分区名字符串。

**挂载切换语义（已按实现固定）**：两端都允许自动切换，PC **不需要**先发 `UMOUNT`。

- PS2 端 `fsops.c:doMount`：同名分区重复 mount 直接返回 `OK`；挂载了别的分区则先 `fileXioUmount` 再挂新的；传输进行中（`g_xfer.kind != NONE`）返回 `ERR_BUSY`。
- PC 端 `PfsNetClient.MountAsync`：换分区前自己先发一次 `CMD_UMOUNT` 以保持本地状态一致。
- 每个文件/目录命令在 PS2 端都要求已挂载，否则 `ERR_NOT_MOUNTED`。PC 端在每次操作前调用 `MountAsync` 兜底。

### 5.2 枚举与空间（B，可无 mount）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0010 | `CMD_LIST_PARTITIONS` | 列出可浏览分区。**实现只返回 PFS 类型分区**（`mode == APA_TYPE_PFS`），子分区（`APA_FLAG_SUB`）跳过；flags bit0 只决定系统分区是否包含 |
| 0x0011 | `CMD_GET_APA_FREE` | APA 总量 / 未分配（`HDIOC_TOTALSECTOR` / `HDIOC_FREESECTOR`）。**不可替代 A1 校验**，仅 UI 提示用 |
| 0x0012 | `CMD_GET_PFS_FREE` | 当前已挂载分区的 PFS 总量 / 剩余 / zone（`PDIOC_ZONESZ` × `PDIOC_ZONEFREE`，文件导入预检用，策略 B）。未挂载返回 `ERR_NOT_MOUNTED` |

`CMD_LIST_PARTITIONS` 请求标志：

```c
uint32_t flags; /* bit0 = include_system_partitions */
```

响应载荷（小端，已与 `fsops.c:handleListPartitions` / `PfsNetClient.ListPartitionsAsync` 对齐）：

```c
uint32_t count;
struct {
    string   name;        /* APA 分区名，UTF-8 */
    uint8_t  flags;       /* bit0 = is_system，bit1 = is_pfs（当前恒为 1） */
    uint64_t total_bytes; /* 主分区长度 × 512；含子分区的精确总量在 GET_PFS_FREE 计算 */
} entries[count];
```

`CMD_GET_APA_FREE` 响应：`uint64_t total_bytes; uint64_t free_bytes;`（16 字节，**不含 zone**）。
`CMD_GET_PFS_FREE` 响应：`uint64_t total_bytes; uint64_t free_bytes; uint32_t zone_size;`（20 字节）。

### 5.3 目录与元数据（B，需已挂载）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0020 | `CMD_LIST_DIR` | `ls`；请求：相对当前挂载的绝对路径。未挂载返回 `ERR_NOT_MOUNTED` |
| 0x0021 | `CMD_MKDIR` | 新建目录（`fileXioMkdir(full, 0777)`） |
| 0x0022 | `CMD_RMDIR` | 删除空目录（非空返回 `ERR_NOT_EMPTY`；递归删除由 PC 展开，见 `MainForm.DeleteRemoteDirectoryAsync`） |
| 0x0023 | `CMD_REMOVE` | 删除文件 |
| 0x0024 | `CMD_RENAME` | 载荷为 `(parent, old_name, new_name)`，PS2 端把 `parent` 分别与两个名字拼接。UI 只用同一父目录内改名；跨目录移动未在 UI 中出现 |
| 0x0025 | `CMD_STAT` | **未实现**（返回 `ERR_UNSUPPORTED`）。PC 需要的元数据全部来自 `LIST_DIR` |

MKDIR / RMDIR / REMOVE 载荷：`string parent_path; string name;`
RENAME 载荷：`string parent_path; string old_name; string new_name;`

`CMD_LIST_DIR` 请求载荷：`string path;`（`/` 开头，PC 端 `NormalizePath` 会规整斜杠）

响应载荷（`fsops.c:handleListDir`）：

```c
uint32_t count;
struct {
    string   name;     /* UTF-8；`.` 与 `..` 已过滤 */
    uint8_t  is_dir;   /* FIO_S_IFDIR */
    uint64_t size;     /* hisize<<32 | size */
    string   details;  /* 当前恒为 "dir" 或 "file" */
} entries[count];
```

**不含时间戳**：PS2 端没有填 `iox_dirent` 的时间字段，PC 的「pfsshell 详情」列在网络模式下只显示 `dir`/`file`。

### 5.4 文件传输（B + D1 + T1）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0030 | `CMD_PUT_BEGIN` | 开始上传：目标目录、最终文件名、总字节、是否覆盖 |
| 0x0031 | `CMD_PUT_END` | 数据发完后提交：rename 临时名→最终名；失败则删临时文件 |
| 0x0032 | `CMD_PUT_ABORT` | 取消：关闭并删除临时文件 |
| 0x0033 | `CMD_GET_BEGIN` | 开始下载：路径、可选 offset/length |
| 0x0034 | `CMD_GET_END` | 下载正常结束 |
| 0x0035 | `CMD_GET_ABORT` | 中止下载 |

#### Put（T1）约定

1. PC 先在本机 `listen(45062)`。
2. `CMD_PUT_BEGIN` 载荷（小端）：

   ```c
   /* path: UTF-8 目标目录，如 /APPS */
   /* final_name: UTF-8 最终文件名 */
   /* 线上顺序：string path; string final_name; uint64_t total_bytes; uint32_t flags; */
   /* flags bit0 = overwrite */
   ```

3. PS2 成功响应（无载荷）后 **主动 connect** 到 PC 的 45062；PC `accept`。
4. PS2 在目标目录创建唯一临时文件并写入：
   - 临时名：`.pfsnet_<8 hex>`（以 `.` 开头减少与普通文件冲突）。
5. PC 在数据通道按序发送 `total_bytes` 原始字节（无额外帧；**V1：纯原始字节流，长度以 PutBegin 为准**）。
6. 发完后 PC 发 `CMD_PUT_END`；PS2 `close` 文件，`rename` 为 `final_name`（若存在且 overwrite：先删旧文件再 rename）。
7. 任一步失败：`CMD_PUT_ABORT` 或断线兜底删除临时文件。

#### Get 约定

与 Put 对称：PC `listen(45062)` → `CMD_GET_BEGIN` → PS2 **成功响应后再 connect** → PS2 发送文件字节 → `CMD_GET_END`。

`CMD_GET_BEGIN` 载荷：

```c
/* string path; string name; uint64_t offset; uint64_t length; */
/* length==0 表示从 offset 到文件末尾；V1 通常 offset=0,length=0 */
```

成功响应载荷：

```c
uint64_t total_bytes; /* 随后数据通道将发送的字节数 */
```

3. PS2 成功响应（含 `total_bytes`）后 **主动 connect** 到 PC 的 45062；PC `accept`。
4. PS2 在数据通道按序发送 `total_bytes` 原始字节。
5. 收完后 PC 发 `CMD_GET_END`；失败则 `CMD_GET_ABORT`。

V1 可不支持断点续传；`offset` 字段预留为 0（PC 端 `PfsNetClient` 固定发 `offset=0, length=0`）。

#### 实现现状（P2 已落地，两端一致）

- **顺序**：PC 每次传输都新建一个 `TcpListener(IPAddress.Any, 45062)`（开 `ReuseAddress`），**先挂起 `accept` 再发 `*_BEGIN`**，缩小 PS2 回连与 PC accept 之间的空窗；传完即 `listener.Stop()`。45062 被占用会导致传输失败。
- **accept 超时**：30 秒（`AcceptDataAsync`）。
- **数据通道**：纯原始字节流，长度以 `PUT_BEGIN.total_bytes` / `GET_BEGIN` 响应的 `total_bytes` 为准。两端 socket 都设 512 KiB 收发缓冲 + `NoDelay`；PC 侧本地文件流按 512 KiB 读写，PS2 侧用两块 512 KiB `memalign` 缓冲 + `ioWorker` 线程双缓冲。
- **临时文件（T1）**：`.pfsnet_<8 hex>`，`PUT_END` 时 rename 为最终名（overwrite 时先删旧文件）；`PUT_ABORT` 或控制连接断开时由 `abortXferCleanup()` 删除。
- **取消 / 失败**：PC 先关数据口打断读写，再尝试发 `*_ABORT`（当前用户取消等待上限 8 秒）。控制命令串行分派，单独发送 `*_ABORT` 不保证立即打断仍在进行的数据读写。不要根据原始以太网帧中的 FIN/RST 或 PING 取消传输：旧连接的延迟报文、重传和未重组的数据都不能作为当前会话的取消依据。`GET` 失败时 PC 会删除写到一半的本地文件。
- **状态**：传输进行中（`g_xfer.kind != NONE`）执行 MOUNT / UMOUNT / APA 变更会返回 `ERR_BUSY`。

### 5.5 APA 扇区只读（A1，分区校验）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0040 | `CMD_READ_SECTORS` | 从 `hdd0` 原始盘读取 512 字节扇区 |

请求：

```c
struct ReadSectorsRequest {
    uint64_t lba;       /* 起始 LBA（512B） */
    uint32_t count;     /* 扇区数；单次建议上限 256（128 KiB），与控制载荷上限一致 */
};
```

响应载荷：`count * 512` 原始字节（若超过控制上限，应改为数据通道——**V1：count 限制在控制通道内**）。

PC 侧：

- 将读到的数据喂入现有（或抽离后的）`ApaPartitionTableReader` 同等逻辑。
- **仅在分区创建/删除/重命名的写前与写后**使用；文件操作不走此路径。

实现现状：

- PS2 端（`fsops.c:handleReadSectors`）硬性校验 `count` 在 **1–256**、`lba ≤ 0xFFFFFFFF`（底层 `HDIOC_READSECTOR` 用 32 位 LBA），否则 `ERR_INVAL`。载荷不足 12 字节同样 `ERR_INVAL`。
- PC 端（`PfsNetClient.ReadSectorsAsync`）同样限制 256 扇区，并校验返回长度 == `count * 512`。超过 256 扇区的请求由 `PfsNetApaSectorSource` 自动切块串行拉取。
- PC 端在调用前检查 `FEAT_APA_SECTOR`，未声明则提示「请更新 PfsNetServer.elf」。

### 5.6 分区变更（A + FEAT_APA_MUTATE）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0050 | `CMD_APA_CREATE_PFS` | 创建 PFS 分区（名、大小等与本地 mkpart 语义对齐） |
| 0x0051 | `CMD_APA_REMOVE` | 删除分区 |
| 0x0052 | `CMD_APA_RENAME` | 重命名分区 |

流程（PC）：

1. 若已挂载 → `CMD_UMOUNT`。
2. A1：`CMD_READ_SECTORS` 拉表 → 本地校验通过才继续。
3. 发变更命令。
4. 再次 A1 拉表复核（目标存在性、空闲空间变化等，对齐现有 MainForm 逻辑）。
5. 失败则向用户报告，禁止继续写入。

V1 已实现 0x0050–0x0052，`FEAT_APA_MUTATE` 置位。PC 菜单的 **P1b** 提示（`MainForm.RejectNetworkPartitionMutationIfNeeded`）只在连到未声明该 feature 的旧 ELF 时才会出现。

#### 实现现状

PS2 端（`src/fsops.c`）：

- `APA_CREATE_PFS` 载荷：`string name; string size;`，`size` 为「整数 + `M`/`G`」，换算成 MB 后**必须是 128 的整数倍**，否则 `ERR_INVAL`。系统保留名（`__mbr`/`__net`/`__system`/`__sysconf`/`__common`/`__boot`）→ `ERR_INVAL`；同名已存在 → `ERR_EXIST`。
  分配方式：按 APA 尺寸表从大到小开主分区，再用 `HIOCADDSUB` 追加子分区凑容量，最后 `fileXioFormat("pfs:", "hdd0:名字", {8192, 0x00002d66, 0})`（对齐 pfsshell `mkpfs`）。
  **注意**：空间碎片/不足时可能凑不满请求容量（内部 `left > 0`）却仍返回 `OK`，格式化也可能失败后自动 `fileXioRemove` 回滚。因此 PC **必须**做写后校验。
- `APA_REMOVE` 载荷：`string name;`。系统保留名 → `ERR_INVAL`；执行前若已挂载会**自动 umount**。
  标准 IOP `ps2hdd` 拒绝删除**任意 `__` 前缀**分区并返回 `-EACCES`（映射为 `ERR_INVAL`）。要删 `__.POPS` 这类分区，需要以 `APA_ALLOW_REMOVE_PARTITION_WITH_LEADING_UNDERSCORE` 编译的 `ps2hdd.irx`。PC 端对这种情况有专门文案（`PfsNetProtocol.FormatCommandError`，含回归测试）。
- `APA_RENAME` 载荷：`string old_name; string new_name;`。已挂载或传输中 → `ERR_BUSY`（不自动 umount）；任一名字是系统保留名 → `ERR_INVAL`。
- 三者都要求 `g_xfer.kind == NONE`，否则 `ERR_BUSY`。

PC 端（`MainForm`）写前/写后校验：

1. `ReadApaLayoutAsync` 拉表校验（网络走 `READ_SECTORS` + `PfsNetApaSectorSource`，与本地同一套 `ApaPartitionTableReader`）。校验失败直接拒绝操作。
2. 变更前 `EnsureNetworkUnmountedForApaAsync` 先 umount。
3. 发变更命令。
4. **创建**：再次拉表要求分区存在、类型为 PFS 且**容量与请求值完全相等**；不等则自动删除半成品分区并报错。随后再挂载 + `LIST_DIR /` 验证 PFS 可用，失败同样自动删除。
5. **删除**：删除后要求目标消失**且未分配空间增加**，否则报「立即停止写入、保留硬盘现状」。（本地模式下 pfsshell 的 `rmpart` 成功后仍可能打印 `No such file` / exit -2，因此结论只以 APA 表复核为准。）

### 5.7 关机（可选）

| ID | 名称 | 说明 |
|----|------|------|
| 0x00FF | `CMD_SHUTDOWN` | **未实现**（返回 `ERR_UNSUPPORTED`）。当前退出方式是在 PS2 上按 START：`main.c:safeExitElf()` 会 force umount → `HDIOC_DEV9OFF` → IOP `rom0:UDNL` 复位 → `_LoadExecPS2("rom0:OSDSYS")` |

---

## 6. 挂载模型（M1）与安全 umount

### 6.1 状态

每个控制连接：

- `Idle`：已握手，未挂载  
- `Mounted(partition)`：已挂载该 PFS 分区  
- `Transfer`：数据通道进行中（Put/Get）；此时禁止换挂载  

### 6.2 PC 主动 UMOUNT 的时机（实现现状）

| 场景 | 行为 |
|------|------|
| 返回分区列表 | `LoadPartitionsAsync` 先 `UmountAsync`（异常忽略） |
| 打开另一分区 | `MountAsync` 换分区前自动发一次 `UMOUNT` |
| 开始分区变更（0x005x）前 | `EnsureNetworkUnmountedForApaAsync` 显式 umount |
| 用户点「断开 PFS 网络」 | **不发 umount**，只关本地套接字并清理 UI。原因：对端已掉线时远程 umount 会卡死；PS2 端检测到断线会自行 force umount |
| 应用程序退出（`FormClosed`） | 同上，只 `Disconnect()`，不发 umount |

因此「PC 断开前必须 UMOUNT」这条**不再成立**：正确性依赖 §6.3 的 PS2 端强制清理。

### 6.3 PS2 强制清理（硬性）

目标：中止数据线程 → 关闭打开文件 → 删除未提交临时 Put 文件 → `umount` → 回到可监听/可接受新握手状态。

| 触发条件 | 现状 |
|----------|------|
| 控制连接断开或协议级致命错误 | **已实现**：`server.c:closeClient()` → `pfsnetFsForceUmount()`；`fsops.c:abortXferCleanup()` 关闭文件并删除未提交的 `.pfsnet_*` |
| `CMD_PING` 超时 / 空闲死连接 | **未实现（P4）**。PC 端 `PfsNetClient.PingAsync` 存在但**没有任何调用点**，没有 10 s 心跳定时器；PS2 端也没有「30 s 无控制帧视同死连接」的判定。`server.c` 里唯一的 idle 判定是 `pfsnetPadPollAllowed()`（收发中或停传未满 2 秒不轮询手柄），与连接存活无关 |
| `CMD_PUT_ABORT` / 传输异常 | **已实现** |

当前的实际兜底是 PC 侧超时：控制响应读取与数据通道 accept 各 30 秒（`PfsNetCodec.ReadExactAsync` / `AcceptDataAsync`）。半开连接（如 PS2 掉电）要等这个超时才会被发现，期间 PS2 上的挂载会一直保持。补齐 P4 之前不要在不可信网络上使用。

---

## 7. 与 HDL / NBD 的边界

| 系统 | 关系 |
|------|------|
| HDLGameInstaller | 同端口；靠 `PFSNET_MAGIC` 拒连（`ERR_MAGIC`）；用户勿同时运行两 ELF。PS2 端单客户端：已有连接在跑时新连接立即被 close |
| NBD | **N1 互斥**：PC 端在 NBD 映射进程运行时拒绝连接 PfsNet（`MainForm.ConnectPfsNetAsync`）。协议不定义 NBD。注：NBD 的 UI 目前已在 `MainForm` 中隐藏，代码保留 |
| 本地 pfsshell | 文件 UTF-8 语义一致；网络后端实现同一套 UI 能力。差异：网络模式的「pfsshell 详情」列只有 `dir`/`file`（无时间戳），且不支持拖拽到 PC |

---

## 8. 结果码（草案）

| 值 | 名称 | 含义 |
|----|------|------|
| 0 | `OK` | 成功 |
| -1 | `ERR_GENERIC` | 未分类失败 |
| -2 | `ERR_IO` | 设备/文件系统 I/O |
| -3 | `ERR_NOENT` | 不存在 |
| -4 | `ERR_EXIST` | 已存在 |
| -5 | `ERR_NOT_EMPTY` | 目录非空 |
| -6 | `ERR_NOSPC` | 空间不足 |
| -7 | `ERR_INVAL` | 参数非法 |
| -8 | `ERR_NOT_MOUNTED` | 需要挂载 |
| -9 | `ERR_BUSY` | 传输中或状态不允许 |
| -10 | `ERR_UNSUPPORTED` | 功能未实现 / feature 关闭 |
| -11 | `ERR_VERSION` | 协议版本不匹配 |
| -12 | `ERR_MAGIC` | 握手魔数错误 |
| -13 | `ERR_DATA_CONN` | 数据通道建立/传输失败 |
| -14 | `ERR_AGAIN` | 临时失败，可重试 |

（具体与 PS2SDK errno 映射在实现时附表，本草案先稳定名称。）

---

## 9. 安全说明

- 无认证：等价于局域网开放写盘，仅可信网络使用。  
- 分区变更必须走 A1，降低坏表误操作风险，但不能防止恶意客户端。  

---

## 10. 实现分期（与协议能力对应）

| 阶段 | 协议子集 | PC / PS2 | 状态 |
|------|----------|----------|------|
| P0 | 本文定稿 | 双方对照实现 | 完成 |
| P1 | HELLO / PING / LIST_PARTITIONS / MOUNT / UMOUNT / LIST_DIR | 只读浏览像本地 | 完成 |
| P2 | MKDIR/REMOVE/RENAME/RMDIR + PUT/GET（T1+D1） | 文件管理完整（B） | 完成 |
| P3 | READ_SECTORS + APA_* + FEAT 位 | 分区管理（A1+A） | 完成 |
| P4 | 心跳/断线清理打磨、关机可选 | 「极少操作」体验 | **未开始**（见 §6.3；`CMD_SHUTDOWN` 也未实现） |

---

## 11. 细节钉死情况

| 项 | 现状 |
|----|------|
| 临时文件名精确格式 | **已钉死**：`.pfsnet_%08x`（`fsops.c` 用递增 `g_tempSeq`） |
| 崩溃残留清理策略 | **部分**。控制连接断开 / `PUT_ABORT` 时 `abortXferCleanup()` 会删掉本次未提交的临时文件；但「进入目录时扫 `.pfsnet_*`」的兜底清理**未实现** —— PS2 掉电残留的临时文件不会被自动回收 |
| `CMD_LIST_PARTITIONS` 与本地过滤规则对齐 | **未完全对齐**。网络端只按 APA `mode == APA_TYPE_PFS` 过滤；本地是 `pfsshell ls` 解析名字后逐个探测挂载（`PfsShellClient.FilterBrowsablePfsPartitionsAsync`）。因此「类型是 PFS 但已格式化损坏」的分区在网络模式会列出、在本地模式会被过滤掉。系统保留名的隐藏都在 PC 端做（`IsProtectedPartitionName`），两端一致 |
| `CMD_APA_CREATE_PFS` 尺寸单位 | **已对齐**：PC 端把同一个规范化字符串（`512G` / `128M`）同时发给 pfsshell `mkpart` 与 `APA_CREATE_PFS`（`MainForm.TryParsePartitionSize` → `normalizedSize`）。PS2 端额外强制 128 MB 整数倍 |
| 超大读扇区是否改走数据通道 | **未改**。协议仍限单次 ≤256 扇区，PC 端 `PfsNetApaSectorSource` 负责切块串行拉取 |
| 文档版本号与 `PFSNET_PROTOCOL_VERSION` 同步规则 | **未定**。两端常量都是 `0x0001`，本文没有独立版本号；§12 修订记录只按日期 |

---

## 12. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-07-30 | 初稿：按冻结决策 F1 编写 |
| 2026-07-31 | P1 ELF 源码落地（main/net/server/fsops）；features 仅 FEAT_FILE_OPS |
| 2026-08-01 | P2：钉死 Put/Get 载荷布局；FEAT_DATA_CHANNEL；T1 临时名 + 数据回连 |
| 2026-09-06 | P3 落地：READ_SECTORS + APA_CREATE_PFS/REMOVE/RENAME，四个 feature 全开；修复网络模式删除 `__` 前缀分区的错误映射（`-EACCES` → `ERR_INVAL`）与 PC 端专用提示 |
| 2026-09-16 | 文档与代码对齐：状态改为「V1 实现基线」；补全 LIST_PARTITIONS / LIST_DIR / GET_*_FREE 的实际载荷布局；标注 `CMD_GET_SESSION` / `CMD_STAT` / `CMD_SHUTDOWN` 未实现；按实现改写挂载切换语义（两端均自动 umount，PC 无需先发 UMOUNT）与断开路径（PC 不再远程 umount）；P4 标注未开始并写明当前只有 30 s 超时兜底 |
