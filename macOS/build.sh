#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${PFSSHELL_BIN:?请指定由 362053534/pfsshell 编译的 Monterey 通用 pfsshell}"
export MACOSX_DEPLOYMENT_TARGET=12.0
# 工具也必须为 Monterey 通用版，不能仅降低主程序的 plist 门槛。
python3 "$ROOT/check-compatibility.py" "$PFSSHELL_BIN"
cd "$ROOT"
for architecture in arm64 x86_64; do
  swift build -c release --triple "$architecture-apple-macosx12.0" \
    --scratch-path "$ROOT/.build/monterey-$architecture"
done
ARM_BIN="$(swift build -c release --triple arm64-apple-macosx12.0 --scratch-path "$ROOT/.build/monterey-arm64" --show-bin-path)/APAFileManager"
INTEL_BIN="$(swift build -c release --triple x86_64-apple-macosx12.0 --scratch-path "$ROOT/.build/monterey-x86_64" --show-bin-path)/APAFileManager"
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
xcrun lipo -create "$ARM_BIN" "$INTEL_BIN" -output "$APP/Contents/MacOS/$EXECUTABLE"
cp "$PFSSHELL_BIN" "$APP/Contents/MacOS/tools/pfsshell"
chmod +x "$APP/Contents/MacOS/$EXECUTABLE" "$APP/Contents/MacOS/tools/pfsshell"
strip -x "$APP/Contents/MacOS/$EXECUTABLE"
python3 "$ROOT/check-compatibility.py" "$APP/Contents/MacOS/$EXECUTABLE" "$APP/Contents/MacOS/tools/pfsshell"
otool -L "$APP/Contents/MacOS/$EXECUTABLE"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
printf 'help\nexit\n' | "$APP/Contents/MacOS/tools/pfsshell" 2>&1 | grep -q '~Command List~'
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUTPUT/$ARCHIVE"
du -sk "$APP"
ls -lh "$OUTPUT/$ARCHIVE"

if [[ "${LEGACY_CI:-0}" == 1 ]]; then
  # 测试工具只在当前构建机运行，不随 ZIP 发布。
  xcrun swiftc -O -target "$(uname -m)-apple-macosx12.0" -o "$OUTPUT/CoreChecks" \
    "$ROOT"/Sources/APACore/*.swift "$ROOT"/Tests/APACoreTests/*.swift
  if [[ -e "$OUTPUT/$APP_NAME" ]]; then
    mv "$OUTPUT/$APP_NAME" "$OUTPUT/$APP_NAME.backup.$(date +%s).$$"
  fi
  mv "$APP" "$OUTPUT/$APP_NAME"
fi
