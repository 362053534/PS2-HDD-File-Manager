#!/bin/bash
# 从 362053534/pfsshell 源码编译 Monterey 通用工具，不依赖 Rosetta 执行交叉构建。
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="$(cd "${1:?请指定 pfsshell 源码目录}" && pwd)"
OUTPUT="${2:?请指定输出文件}"
export MACOSX_DEPLOYMENT_TARGET=12.0

# 兼容旧 fork 中 POSIX put 的参数问题；新版已修复时直接使用原实现。
python3 - "$SOURCE/src/shell.c" <<'PY'
from pathlib import Path
import sys
p = Path(sys.argv[1])
s = p.read_text()
start = s.index('static int do_put(')
old = '    const char *remote_name = argv[1];\n#ifdef _WIN32'
body = s[start:]
if old in body:
    replacement = '    const char *remote_name = (argc >= 3) ? argv[2] : argv[1];\n#ifdef _WIN32'
    p.write_text(s[:start] + body.replace(old, replacement, 1))
elif 'remote_name = argv[2]' not in body and 'remote_name = (argc >= 3) ? argv[2]' not in body:
    raise SystemExit('无法确认 pfsshell put 双参数实现，请检查该 fork 的源码')
PY

python3 "$ROOT/../scripts/patch-pfsshell-create.py" "$SOURCE/src/shell.c"
python3 "$ROOT/../scripts/patch-pfsshell-macos-session.py" "$SOURCE/src/shell.c"

mkdir -p "$(dirname "$OUTPUT")"
# 独立构建目录避免旧架构或旧部署目标的对象文件被复用。
WORK="$(mktemp -d "${TMPDIR:-/tmp}/apanet-pfsshell.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
for architecture in arm64 x86_64; do
  family="$architecture"
  if [[ "$architecture" == arm64 ]]; then family=aarch64; fi
  cat > "$WORK/$architecture.ini" <<EOF
[binaries]
c = 'clang'
ar = 'ar'
strip = 'strip'
[properties]
needs_exe_wrapper = true
[host_machine]
system = 'darwin'
cpu_family = '$family'
cpu = '$architecture'
endian = 'little'
[built-in options]
c_args = ['-arch', '$architecture', '-mmacosx-version-min=12.0']
c_link_args = ['-arch', '$architecture', '-mmacosx-version-min=12.0']
EOF
  meson setup "$WORK/$architecture" "$SOURCE" --cross-file "$WORK/$architecture.ini" \
    --buildtype=release -Denable_pfs2tar=false
  meson compile -C "$WORK/$architecture" pfsshell
 done
xcrun lipo -create "$WORK/arm64/pfsshell" "$WORK/x86_64/pfsshell" -output "$WORK/pfsshell"
python3 "$ROOT/check-compatibility.py" "$WORK/pfsshell"
cp "$WORK/pfsshell" "$OUTPUT"
chmod +x "$OUTPUT"
