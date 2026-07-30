# ps2-server（PfsNet）

PS2 端网络 PFS 服务，协议见同目录 [PROTOCOL.md](PROTOCOL.md)。

## 状态

- **P1 目标**：HELLO / PING / LIST_PARTITIONS / MOUNT / UMOUNT / LIST_DIR / GET_*_FREE / 目录元数据命令  
- PC 端客户端已接入浏览与 mkdir/rename/delete；**Put/Get 与 APA 变更**待下一阶段  
- 本目录目前提供协议头与实现骨架；完整 ELF 需在具备 **PS2SDK** 的环境，参照 `C:\GitHub\HDLGameInstaller` 的 IOP 加载与网络初始化进行链接

## 建议实现步骤（PS2）

1. 从 HDLGameInstaller 复用：`DEV9` / `ATAD` / `HDD` / `PFS` / `FILEXIO` / `NETMAN` / `SMAP` / `ps2ip` 加载顺序  
2. 启动后 `listen(45061)`，屏幕显示 IP；无菜单操作即可  
3. 按 `include/protocol.h` 实现控制帧；断线时强制 `umount`（M1 契约）  
4. features：V1 先报 `FEAT_FILE_OPS | FEAT_DATA_CHANNEL`；分区变更待 A1 后再置位  

## 与 HDLGameInstaller

- **同端口** 45061/45062，不可同时运行  
- 握手魔数 `PFSN`，勿使用 HDL 的 `0x0C`  

## 目录

```
ps2-server/
  PROTOCOL.md
  README.md
  include/protocol.h
  src/          （后续填入 server 实现）
```
