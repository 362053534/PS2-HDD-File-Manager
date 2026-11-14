#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="${1:-$ROOT/../bin/Release/PS2-HDD-File-Manager.app}"
EXECUTABLE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")"
"$APP/Contents/MacOS/$EXECUTABLE" --ui-smoke-test --ui-flow-test
