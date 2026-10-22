#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT
xcrun swiftc -O -o "$TEMP/CoreChecks" "$ROOT"/Sources/APACore/*.swift "$ROOT"/Tests/APACoreTests/*.swift
"$TEMP/CoreChecks"
