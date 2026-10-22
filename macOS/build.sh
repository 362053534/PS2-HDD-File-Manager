#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${PFSSHELL_BIN:?请指定由 362053534/pfsshell 编译的 macOS 原生 pfsshell}"
file "$PFSSHELL_BIN" | grep -q 'Mach-O' || { echo 'pfsshell 必须是 macOS Mach-O'; exit 1; }
cd "$ROOT"
swift build -c release
BINDIR="$(swift build -c release --show-bin-path)"
OUTPUT="$ROOT/dist"
APP_NAME="APA File Manager.app"
EXECUTABLE="APAFileManager"
ARCHIVE="APA-File-Manager-macOS.zip"
if [[ "${LEGACY_CI:-0}" == 1 ]]; then
  OUTPUT="$ROOT/../bin/Release"
  APP_NAME="PS2-HDD-File-Manager.app"
  EXECUTABLE="PS2-HDD-File-Manager"
  ARCHIVE="PS2-HDD-File-Manager-Release-macos.zip"
fi
mkdir -p "$OUTPUT"
STAGE="$(mktemp -d "$OUTPUT/stage.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/$APP_NAME"
mkdir -p "$APP/Contents/MacOS/tools" "$APP/Contents/Resources"
cp "$ROOT/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleExecutable $EXECUTABLE" "$APP/Contents/Info.plist"
cp "$BINDIR/APAFileManager" "$APP/Contents/MacOS/$EXECUTABLE"
cp "$PFSSHELL_BIN" "$APP/Contents/MacOS/tools/pfsshell"
chmod +x "$APP/Contents/MacOS/$EXECUTABLE" "$APP/Contents/MacOS/tools/pfsshell"
strip -x "$APP/Contents/MacOS/$EXECUTABLE"
# 仅链接系统框架，不复制第三方运行时。
otool -L "$APP/Contents/MacOS/$EXECUTABLE"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
printf 'help\nexit\n' | "$APP/Contents/MacOS/tools/pfsshell" 2>&1 | grep -q '~Command List~'
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUTPUT/$ARCHIVE"
du -sk "$APP"
ls -lh "$OUTPUT/$ARCHIVE"

if [[ "${LEGACY_CI:-0}" == 1 ]]; then
  # 工作流随后执行 app --run-tests；实际测试核心网络与本地镜像，不绕过检查。
  xcrun swiftc -O -o "$OUTPUT/CoreChecks" "$ROOT"/Sources/APACore/*.swift "$ROOT"/Tests/APACoreTests/*.swift
  if [[ -e "$OUTPUT/$APP_NAME" ]]; then
    mv "$OUTPUT/$APP_NAME" "$OUTPUT/$APP_NAME.backup.$(date +%s).$$"
  fi
  mv "$APP" "$OUTPUT/$APP_NAME"
fi
