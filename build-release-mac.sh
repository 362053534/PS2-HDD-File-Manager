#!/usr/bin/env bash
# 手动编译 macOS Release（产出 bin/Release/PS2-HDD-File-Manager.app 与 PS2-HDD-File-Manager-Release-macos.zip）
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

RID="${RID:-osx-arm64}"
PFSSHELL_BIN="${PFSSHELL_BIN:-$ROOT/../../pfsshell/build-local-macos/pfsshell}"
PUBLISH_DIR="$ROOT/bin/Release/publish-$RID"
APP_BUNDLE="$ROOT/bin/Release/PS2-HDD-File-Manager.app"
ZIP_PATH="$ROOT/bin/Release/PS2-HDD-File-Manager-Release-macos.zip"

if [[ ! -s "$PFSSHELL_BIN" ]]; then
  echo "未找到 macOS pfsshell 二进制：$PFSSHELL_BIN" >&2
  echo "请先编译 362053534/pfsshell，或通过 PFSSHELL_BIN=/path/to/pfsshell 指定。" >&2
  exit 1
fi

rm -rf "$PUBLISH_DIR" "$APP_BUNDLE" "$ZIP_PATH"

echo "Publishing Mac/PS2-HDD-File-Manager.Mac.csproj ($RID)..."
dotnet publish "$ROOT/Mac/PS2-HDD-File-Manager.Mac.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$PUBLISH_DIR" \
  --nologo

mkdir -p "$APP_BUNDLE/Contents/MacOS/tools" "$APP_BUNDLE/Contents/Resources"
cp "$ROOT/Mac/Info.plist" "$APP_BUNDLE/Contents/Info.plist"

find "$PUBLISH_DIR" -maxdepth 1 -mindepth 1 ! -name '*.pdb' ! -name '*.dbg' -exec cp -R {} "$APP_BUNDLE/Contents/MacOS/" \;
find "$APP_BUNDLE/Contents/MacOS" -name '*.pdb' -delete
cp "$PFSSHELL_BIN" "$APP_BUNDLE/Contents/MacOS/tools/pfsshell"
chmod +x "$APP_BUNDLE/Contents/MacOS/PS2-HDD-File-Manager" "$APP_BUNDLE/Contents/MacOS/tools/pfsshell"

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$APP_BUNDLE"
fi

if command -v ditto >/dev/null 2>&1; then
  ditto -c -k --sequesterRsrc --keepParent "$APP_BUNDLE" "$ZIP_PATH"
else
  (cd "$ROOT/bin/Release" && zip -r -y "$ZIP_PATH" "PS2-HDD-File-Manager.app")
fi

echo "Done."
echo "App: $APP_BUNDLE"
echo "Zip: $ZIP_PATH"
