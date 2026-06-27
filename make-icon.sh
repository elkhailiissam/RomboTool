#!/bin/bash
# Regenerate the app icon: gui/Assets/icon_1024.png -> RomboTool.icns (+ window icon.png).
# Requires macOS (sips, iconutil) and python3 with Pillow.
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
A="$ROOT/gui/Assets"

echo "==> Drawing master PNG…"
( cd "$A" && python3 make_icon.py )

echo "==> Building RomboTool.icns…"
SET="$A/RomboTool.iconset"
rm -rf "$SET"; mkdir -p "$SET"
M="$A/icon_1024.png"
sips -z 16 16     "$M" --out "$SET/icon_16x16.png"      >/dev/null
sips -z 32 32     "$M" --out "$SET/icon_16x16@2x.png"   >/dev/null
sips -z 32 32     "$M" --out "$SET/icon_32x32.png"      >/dev/null
sips -z 64 64     "$M" --out "$SET/icon_32x32@2x.png"   >/dev/null
sips -z 128 128   "$M" --out "$SET/icon_128x128.png"    >/dev/null
sips -z 256 256   "$M" --out "$SET/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$M" --out "$SET/icon_256x256.png"    >/dev/null
sips -z 512 512   "$M" --out "$SET/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$M" --out "$SET/icon_512x512.png"    >/dev/null
cp "$M" "$SET/icon_512x512@2x.png"
iconutil -c icns "$SET" -o "$A/RomboTool.icns"
rm -rf "$SET"
echo "Done -> $A/RomboTool.icns"
