#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${PFSSHELL_TEST_BIN:?Set the canonical patched pfsshell path}"
TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT
xcrun swiftc -O -target "$(uname -m)-apple-macosx12.0" -o "$TEMP/LargePartitionProbe"   "$ROOT"/Sources/APACore/*.swift "$ROOT/Tests/LargePartitionProbe.swift"
"$TEMP/LargePartitionProbe" "$PFSSHELL_TEST_BIN"
