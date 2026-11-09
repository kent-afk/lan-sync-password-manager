#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

if [ $# -lt 1 ]; then
  echo "использование: bash scripts/build-windows.sh КАТАЛОГ" >&2
  exit 2
fi
OUT="$1"
mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

dotnet publish src/Pm.Desktop/Pm.Desktop.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o "$WORK/win"

cp "$WORK/win/pm-desktop.exe" "$OUT/Pm.exe"
echo "готово: $OUT/Pm.exe"
