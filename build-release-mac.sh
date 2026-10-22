#!/bin/bash
# 仅适配旧 Actions 的入口与文件名，源码及构建逻辑全部在 macOS/。
set -euo pipefail
export LEGACY_CI=1
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/macOS/build.sh" "$@"
