#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMMAND="${1:-}"

fail() {
    echo "错误: $*" >&2
    exit 1
}

usage() {
    cat <<EOF
用法:
  scripts/package-macos.sh zip <发布目录> <输出.zip>
  scripts/package-macos.sh dmg <发布目录> <版本> <输出.dmg>
EOF
}

package_zip() {
    local source="$1"
    local output="$2"
    [[ -d "$source" ]] || fail "发布目录不存在: $source"
    mkdir -p "$(dirname "$output")"
    ditto -c -k --sequesterRsrc "$source" "$output"
}

package_dmg() {
    local source="$1"
    local version="${2#v}"
    local output="$3"
    [[ -d "$source" ]] || fail "发布目录不存在: $source"
    [[ -n "$version" ]] || fail "缺少版本号"
    command -v hdiutil >/dev/null 2>&1 || fail "此脚本只能在 macOS 上创建 DMG。"
    command -v iconutil >/dev/null 2>&1 || fail "未找到 iconutil。"

    local app="$TMP/Ordoa.app"
    local contents="$app/Contents"
    mkdir -p "$contents/MacOS" "$contents/Resources"

    cp -R "$source/." "$contents/MacOS/"
    if [[ -f "$ROOT/LICENSE" ]]; then
        cp "$ROOT/LICENSE" "$contents/Resources/LICENSE"
    fi

    local iconset="$TMP/Ordoa.iconset"
    local iconset_source="$ROOT/packaging/macos/Ordoa.iconset"
    [[ -d "$iconset_source" ]] || fail "缺少 macOS 图标集: $iconset_source"
    cp -R "$iconset_source" "$iconset"
    iconutil -c icns "$iconset" -o "$contents/Resources/Ordoa.icns"

    cat > "$contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>Ordoa</string>
  <key>CFBundleExecutable</key><string>Ordoa</string>
  <key>CFBundleIconFile</key><string>Ordoa.icns</string>
  <key>CFBundleIdentifier</key><string>me.whynbnb.Ordoa</string>
  <key>CFBundleName</key><string>Ordoa</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Folder</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key>
      <array><string>public.folder</string><string>public.directory</string></array>
    </dict>
  </array>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>me.whynbnb.Ordoa</string>
      <key>CFBundleURLSchemes</key>
      <array><string>sftp</string><string>ftp</string><string>ftps</string><string>smb</string><string>dav</string><string>webdav</string></array>
    </dict>
  </array>
</dict>
</plist>
EOF

    chmod +x "$contents/MacOS/Ordoa"

    local dmgroot="$TMP/dmg"
    mkdir -p "$dmgroot"
    cp -R "$app" "$dmgroot/Ordoa.app"
    ln -s /Applications "$dmgroot/Applications"

    mkdir -p "$(dirname "$output")"
    hdiutil create -volname "Ordoa $version" -srcfolder "$dmgroot" \
        -ov -format UDZO "$output" >/dev/null
}

case "$COMMAND" in
    zip)
        [[ $# -eq 3 ]] || { usage; exit 1; }
        TMP="$(mktemp -d)"
        trap 'rm -rf "$TMP"' EXIT
        package_zip "$2" "$3"
        ;;
    dmg)
        [[ $# -eq 4 ]] || { usage; exit 1; }
        TMP="$(mktemp -d)"
        trap 'rm -rf "$TMP"' EXIT
        package_dmg "$2" "$3" "$4"
        ;;
    -h|--help|help|"")
        usage
        ;;
    *)
        usage
        exit 1
        ;;
esac
