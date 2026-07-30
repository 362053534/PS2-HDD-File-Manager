/* PfsNet 服务骨架：后续在此实现 listen/accept 与命令分发。
 * 完整工程需链接 PS2SDK（fileXio / ps2ip 等），参见 README.md。 */
#include "protocol.h"

/* 占位：避免空目录；真正入口在具备工具链后补全。 */
int pfsnet_protocol_version(void)
{
    return (int)PFSNET_PROTOCOL_VERSION;
}
