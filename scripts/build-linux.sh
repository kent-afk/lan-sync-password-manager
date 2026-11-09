#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

if [ $# -lt 1 ]; then
  echo "использование: bash scripts/build-linux.sh КАТАЛОГ" >&2
  exit 2
fi
OUT="$1"
mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

dotnet publish src/Pm.Desktop/Pm.Desktop.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$WORK/linux"

"$WORK/linux/pm-desktop" --help >/dev/null

APP="$WORK/Pm.AppDir"
mkdir -p "$APP/usr/bin" "$APP/usr/share/icons/hicolor/256x256/apps"
cp "$WORK/linux/pm-desktop" "$APP/usr/bin/pm"
cp assets/pm.png "$APP/usr/share/icons/hicolor/256x256/apps/pm.png"
cp assets/pm.png "$APP/pm.png"

cat > "$APP/pm.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Pm
Comment=Password manager for the local network
Exec=pm
Icon=pm
Terminal=false
Categories=Utility;Security;
EOF

cat > "$APP/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/pm" "$@"
EOF
chmod +x "$APP/AppRun" "$APP/usr/bin/pm"

CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/pm"
mkdir -p "$CACHE"
TOOL="$CACHE/appimagetool-x86_64.AppImage"
TOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
if [ ! -x "$TOOL" ]; then
  curl -L --fail -o "$TOOL" \
    https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
fi
echo "$TOOL_SHA256  $TOOL" | sha256sum -c -
chmod +x "$TOOL"

ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APP" "$WORK/Pm-x86_64.AppImage"
mv -f "$WORK/Pm-x86_64.AppImage" "$OUT/Pm-x86_64.AppImage"
echo "готово: $OUT/Pm-x86_64.AppImage"
