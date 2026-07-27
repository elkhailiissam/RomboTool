#!/bin/bash
# Package the GUI into a double-clickable macOS RomboTool.app bundle (in dist/).
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
APP="$ROOT/dist/RomboTool.app"

echo "==> Building GUI (Release)…"
( cd "$ROOT/gui" && dotnet publish -c Release -o "$ROOT/gui/bin/publish" )

echo "==> Building icon…"
[ -f "$ROOT/gui/Assets/RomboTool.icns" ] || "$ROOT/make-icon.sh"

echo "==> Assembling $APP …"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$ROOT/gui/bin/publish/." "$APP/Contents/MacOS/"
cp "$ROOT/gui/Assets/RomboTool.icns" "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>RomboTool</string>
    <key>CFBundleDisplayName</key><string>RomboTool</string>
    <key>CFBundleIdentifier</key><string>com.rombotool.app</string>
    <key>CFBundleVersion</key><string>3.0.0</string>
    <key>CFBundleShortVersionString</key><string>3.0</string>
    <key>CFBundleExecutable</key><string>RomboTool</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/RomboTool"
echo "Done -> $APP"
echo "Launch with: open \"$APP\""
