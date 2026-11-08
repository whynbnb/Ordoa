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
  scripts/package-linux.sh tar <发布目录> <输出.tar.gz>
  scripts/package-linux.sh deb <发布目录> <版本> <输出.deb>
  scripts/package-linux.sh appimage <发布目录> <输出.AppImage>

appimage 需要设置 APPIMAGETOOL 为 appimagetool-x86_64.AppImage 的路径。
EOF
}

copy_license() {
    local destination="$1"
    if [[ -f "$ROOT/LICENSE" ]]; then
        cp "$ROOT/LICENSE" "$destination/LICENSE"
    fi
}

package_tar() {
    local source="$1"
    local output="$2"
    [[ -d "$source" ]] || fail "发布目录不存在: $source"
    mkdir -p "$(dirname "$output")"

    local stage="$TMP/ordoa"
    mkdir -p "$stage"
    cp -a "$source/." "$stage/"
    copy_license "$stage"
    tar -czf "$output" -C "$stage" .
}

package_deb() {
    local source="$1"
    local version="${2#v}"
    local output="$3"
    [[ -d "$source" ]] || fail "发布目录不存在: $source"
    [[ -n "$version" ]] || fail "缺少 Debian 版本号"
    mkdir -p "$(dirname "$output")"

    local stage="$TMP/deb"
    mkdir -p "$stage/DEBIAN" "$stage/usr/bin" "$stage/usr/lib/ordoa" \
        "$stage/usr/share/applications" "$stage/usr/share/doc/ordoa"
    cp -a "$source/." "$stage/usr/lib/ordoa/"
    copy_license "$stage/usr/share/doc/ordoa"
    ln -s ../lib/ordoa/Ordoa "$stage/usr/bin/ordoa"
    install -m 644 "$ROOT/packaging/ordoa.desktop" "$stage/usr/share/applications/ordoa.desktop"

    for size in 16 24 32 48 64 128 256 512 1024; do
        local icon="$ROOT/packaging/icons/ordoa-$size.png"
        [[ -f "$icon" ]] || continue
        install -Dm644 "$icon" "$stage/usr/share/icons/hicolor/${size}x${size}/apps/ordoa.png"
    done

    cat > "$stage/DEBIAN/control" <<EOF
Package: ordoa
Version: $version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Ordoa Contributors <ordoa@users.noreply.github.com>
Homepage: https://github.com/whynbnb/Ordoa
Depends: libc6, libfontconfig1, libfreetype6, zlib1g, libx11-6, libxext6, libxcb1, libwayland-client0, libffi8, libstdc++6, libgl1
Recommends: ffmpeg, poppler-utils
Installed-Size: $(du -sk "$stage" | awk '{print $1}')
Description: Ordoa file manager
 A Linux-first file manager built with Avalonia.
EOF

    dpkg-deb --build --root-owner-group "$stage" "$output" >/dev/null
}

package_appimage() {
    local source="$1"
    local output="$2"
    [[ -d "$source" ]] || fail "发布目录不存在: $source"
    mkdir -p "$(dirname "$output")"

    local tool="${APPIMAGETOOL:-}"
    if [[ -z "$tool" ]]; then
        tool="$(command -v appimagetool || true)"
    fi
    [[ -n "$tool" && -x "$tool" ]] || fail "未找到 appimagetool；请设置 APPIMAGETOOL。"

    local appdir="$TMP/Ordoa.AppDir"
    mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" \
        "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/doc/ordoa"
    cp -a "$source/." "$appdir/usr/bin/"
    copy_license "$appdir/usr/share/doc/ordoa"
    cp "$ROOT/packaging/ordoa.desktop" "$appdir/ordoa.desktop"
    sed -i 's|^Exec=.*|Exec=Ordoa %U|' "$appdir/ordoa.desktop"

    if [[ -f "$ROOT/packaging/icons/ordoa-256.png" ]]; then
        cp "$ROOT/packaging/icons/ordoa-256.png" "$appdir/ordoa.png"
        cp "$ROOT/packaging/icons/ordoa-256.png" \
            "$appdir/usr/share/icons/hicolor/256x256/apps/ordoa.png"
    fi

    cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Ordoa" "$@"
EOF
    chmod +x "$appdir/AppRun"

    ARCH=x86_64 "$tool" --appimage-extract-and-run --no-appstream "$appdir" "$output"
}

case "$COMMAND" in
    tar)
        [[ $# -eq 3 ]] || { usage; exit 1; }
        TMP="$(mktemp -d)"
        trap 'rm -rf "$TMP"' EXIT
        package_tar "$2" "$3"
        ;;
    deb)
        [[ $# -eq 4 ]] || { usage; exit 1; }
        command -v dpkg-deb >/dev/null 2>&1 || fail "未找到 dpkg-deb。"
        TMP="$(mktemp -d)"
        trap 'rm -rf "$TMP"' EXIT
        package_deb "$2" "$3" "$4"
        ;;
    appimage)
        [[ $# -eq 3 ]] || { usage; exit 1; }
        TMP="$(mktemp -d)"
        trap 'rm -rf "$TMP"' EXIT
        package_appimage "$2" "$3"
        ;;
    -h|--help|help|"")
        usage
        ;;
    *)
        usage
        exit 1
        ;;
esac
