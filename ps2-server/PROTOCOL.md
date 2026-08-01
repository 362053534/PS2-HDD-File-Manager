# PFS 网络协议草案（PfsNet）

状态：**草案 / 已冻结产品决策基线**  
适用：PS2 端 `ps2-server` ELF ↔ PC 端 PS2-HDD-File-Manager  
样板：HDLGameInstaller 的双 TCP 通道与包头风格；**命令语义与版本独立，不得与 HDLGMan 混连**

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

V1 建议：`FEAT_FILE_OPS | FEAT_DATA_CHANNEL`；分区变更在 A1+实现就绪后再置 `FEAT_APA_SECTOR | FEAT_APA_MUTATE`。

---

## 5. 命令一览

### 5.1 会话 / 挂载（M1）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0001 | `CMD_HELLO` | 握手 |
| 0x0002 | `CMD_PING` | 心跳；载荷可空；响应 `result=0` |
| 0x0003 | `CMD_MOUNT` | 挂载指定 PFS 分区为当前会话挂载 |
| 0x0004 | `CMD_UMOUNT` | 卸载当前挂载 |
| 0x0005 | `CMD_GET_SESSION` | 查询：是否已挂载、分区名、当前工作目录（可选） |

`CMD_MOUNT` 载荷：分区名字符串。  
成功后该连接进入「已挂载」状态；再 `CMD_MOUNT` 其它分区前必须先 `CMD_UMOUNT`（或由实现自动 umount 再 mount，但须在文档中固定一种——**草案规定：PC 必须先 UMOUNT**）。

### 5.2 枚举与空间（B，可无 mount）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0010 | `CMD_LIST_PARTITIONS` | 列出 APA 上可浏览分区（实现可过滤非 PFS；是否含系统分区由请求标志决定） |
| 0x0011 | `CMD_GET_APA_FREE` | 返回 APA 未分配空间等摘要（**不可替代 A1 校验**；仅 UI 提示用） |
| 0x0012 | `CMD_GET_PFS_FREE` | 当前已挂载分区的 PFS 剩余/总量/zone（文件导入预检用，策略 B） |

`CMD_LIST_PARTITIONS` 请求标志：

```c
uint32_t flags; /* bit0 = include_system_partitions */
```

响应：条目数组。每条至少含：`name`、`is_directory_mountable`（PFS）、`is_system`、`total_bytes`（若可得）。

### 5.3 目录与元数据（B，需已挂载）

| ID | 名称 | 说明 |
|----|------|------|
| 0x0020 | `CMD_LIST_DIR` | `ls`；请求：相对当前挂载的绝对路径 |
| 0x0021 | `CMD_MKDIR` | 新建目录 |
| 0x0022 | `CMD_RMDIR` | 删除空目录（非空返回错误；递归删除由 PC 展开） |
| 0x0023 | `CMD_REMOVE` | 删除文件 |
| 0x0024 | `CMD_RENAME` | 同目录或带路径的重命名/移动（V1：限同一挂载内） |
| 0x0025 | `CMD_STAT` | 可选；单文件/目录信息 |

`CMD_LIST_DIR` 响应条目：`name`（UTF-8）、`is_dir`、`size`（文件字节；目录可为 0 或目录项大小）、可选时间戳。

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

V1 可不支持断点续传；`offset` 字段预留为 0。

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

V1 若未实现 0x0050–0x0052：feature 位置 0；PC 菜单 **P1b** 提示未启用。

### 5.7 关机（可选）

| ID | 名称 | 说明 |
|----|------|------|
| 0x00FF | `CMD_SHUTDOWN` | 可选；先 umount 再断电（对齐 HDL 的谨慎行为） |

---

## 6. 挂载模型（M1）与安全 umount

### 6.1 状态

每个控制连接：

- `Idle`：已握手，未挂载  
- `Mounted(partition)`：已挂载该 PFS 分区  
- `Transfer`：数据通道进行中（Put/Get）；此时禁止换挂载  

### 6.2 PC 必须主动 UMOUNT 的时机

- 返回分区列表 / 打开另一分区前  
- 用户断开 PfsNet  
- 开始分区变更（0x005x）前  
- 应用程序正常退出路径（尽力而为）  

### 6.3 PS2 强制清理（硬性）

以下情况必须：中止数据线程 → 关闭打开文件 → 删除未提交临时 Put 文件 → `umount` → 回到可监听/可接受新握手状态：

- 控制连接断开或协议级致命错误  
- `CMD_PING` 超时（建议：PC 每 **10s** ping；PS2 **30s** 无任何控制帧且无进行中传输 → 视同死连接）  
- `CMD_PUT_ABORT` / 传输异常  

---

## 7. 与 HDL / NBD 的边界

| 系统 | 关系 |
|------|------|
| HDLGameInstaller | 同端口；靠 `PFSNET_MAGIC` 拒连；用户勿同时运行两 ELF |
| NBD | PC UI **N1 互斥**；协议不定义 NBD |
| 本地 pfsshell | 文件 UTF-8 语义一致；网络后端实现同一套 UI 能力 |

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

| 阶段 | 协议子集 | PC / PS2 |
|------|----------|----------|
| P0 | 本文定稿 | 双方对照实现 |
| P1 | HELLO / PING / LIST_PARTITIONS / MOUNT / UMOUNT / LIST_DIR | 只读浏览像本地 |
| P2 | MKDIR/REMOVE/RENAME/RMDIR + PUT/GET（T1+D1） | 文件管理完整（B） |
| P3 | READ_SECTORS + APA_* + FEAT 位 | 分区管理（A1+A） |
| P4 | 心跳/断线清理打磨、关机可选 | 「极少操作」体验 |

---

## 11. 待实现时再钉死的细节（不阻塞草案）

- 临时文件名精确格式与崩溃残留清理策略（进入目录时扫 `.pfsnet_*`）。  
- `CMD_LIST_PARTITIONS` 与本地「可浏览 PFS」过滤规则逐条对齐。  
- `CMD_APA_CREATE_PFS` 尺寸单位与本地 `mkpart` 字符串完全一致。  
- 读扇区是否在 P3 对超大请求改走数据通道。  
- 协议文档版本号与 `PFSNET_PROTOCOL_VERSION` 同步规则。  

---

## 12. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-07-30 | 初稿：按冻结决策 F1 编写 |
| 2026-07-31 | P1 ELF 源码落地（main/net/server/fsops）；features 仅 FEAT_FILE_OPS |
| 2026-08-01 | P2：钉死 Put/Get 载荷布局；FEAT_DATA_CHANNEL；T1 临时名 + 数据回连 |
