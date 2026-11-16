#!/usr/bin/env bash
# Publishes WinCAN for macOS and wraps it in a WinCAN.app bundle (+ zip).
#
#   build/macos/package-app.sh [version] [rid]
#
# version defaults to 0.0.0-dev, rid to the host architecture (osx-arm64 / osx-x64).
# Output: artifacts/macos/WinCAN.app and artifacts/macos/wincan-<rid>.zip
#
# The bundle is ad-hoc signed (required to run on Apple silicon) but not notarized, so a
# downloaded copy is quarantined by Gatekeeper; clear it with:
#   xattr -dr com.apple.quarantine /Applications/WinCAN.app
set -euo pipefail

version="${1:-0.0.0-dev}"
if [[ -n "${2:-}" ]]; then
  rid="$2"
elif [[ "$(uname -m)" == "arm64" ]]; then
  rid="osx-arm64"
else
  rid="osx-x64"
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="$repo_root/artifacts/macos"
publish="$out/publish-$rid"
app="$out/WinCAN.app"

rm -rf "$publish" "$app" "$out/wincan-$rid.zip"
mkdir -p "$out"

dotnet publish "$repo_root/src/WinCAN.App/WinCAN.App.csproj" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  -p:Version="$version" \
  -p:UseAppHost=true \
  --output "$publish"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -a "$publish/." "$app/Contents/MacOS/"

# CFBundleShortVersionString must be numeric (drop any prerelease suffix).
short_version="${version%%-*}"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>WinCAN</string>
  <key>CFBundleDisplayName</key><string>WinCAN</string>
  <key>CFBundleIdentifier</key><string>org.wincan.WinCAN</string>
  <key>CFBundleExecutable</key><string>WinCAN.App</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSHumanReadableCopyright</key><string>Based on SavvyCAN (C) 2015-2026 Collin Kidder</string>
</dict>
</plist>
PLIST

chmod +x "$app/Contents/MacOS/WinCAN.App"

# Ad-hoc signature over the whole bundle (individual dylibs are signed by publish).
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

ditto -c -k --keepParent "$app" "$out/wincan-$rid.zip"
echo "Created $app"
echo "Created $out/wincan-$rid.zip"
