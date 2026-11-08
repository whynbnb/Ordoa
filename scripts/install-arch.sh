#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID=""
YES=false
BUILD_ONLY=false
KEEP_BUILD=false

fail() {
    echo "错误: $*" >&2
    exit 1
}

usage() {
    cat <<EOF
用法: $0 [选项]

在 Arch Linux / Arch Linux ARM 上构建并安装 Ordoa。

默认会：
  1. 使用 NativeAOT 发布 linux 可执行文件
  2. 生成 ordoa-<版本>-1-<架构>.pkg.tar.zst
  3. 通过 pacman 安装到系统

选项:
  --rid <RID>      目标架构，默认自动检测（linux-x64 / linux-arm64）
  --yes            不询问 pacman / makepkg 的确认
  --build-only     只构建 pacman 包，不安装
  --keep-build     保留临时构建目录
  -h, --help       显示本帮助

要求:
  - Arch Linux 或 Arch Linux ARM
  - pacman、makepkg、fakeroot、sudo
  - .NET 10 SDK
  - clang（缺失时脚本会尝试通过 pacman 安装）

示例:
  ./scripts/install-arch.sh
  ./scripts/install-arch.sh --yes
  ./scripts/install-arch.sh --build-only
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)
            [[ $# -ge 2 ]] || fail "$1 缺少参数"
            RID="$2"
            shift
            ;;
        --yes) YES=true ;;
        --build-only) BUILD_ONLY=true; KEEP_BUILD=true ;;
        --keep-build) KEEP_BUILD=true ;;
        -h|--help) usage; exit 0 ;;
        *) fail "未知参数: $1（使用 --help 查看帮助）" ;;
    esac
    shift
done

[[ $EUID -ne 0 ]] || fail "请用普通用户运行；脚本会在安装阶段使用 sudo。"

command -v pacman >/dev/null 2>&1 || fail "未找到 pacman，这不是 Arch 系统。"
command -v makepkg >/dev/null 2>&1 || fail "未找到 makepkg，请安装 base-devel。"
command -v dotnet >/dev/null 2>&1 || fail "未找到 dotnet，请安装 .NET 10 SDK。"
command -v sudo >/dev/null 2>&1 || fail "未找到 sudo。"

if [[ -z "$RID" ]]; then
    case "$(uname -m)" in
        x86_64|amd64) RID="linux-x64" ;;
        aarch64|arm64) RID="linux-arm64" ;;
        *) fail "不支持的架构: $(uname -m)，请使用 --rid 指定。" ;;
    esac
fi

case "$RID" in
    linux-x64) ARCH="x86_64" ;;
    linux-arm64) ARCH="aarch64" ;;
    *) fail "Arch 包只支持 linux-x64 或 linux-arm64，当前: $RID" ;;
esac

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
[[ -n "$VERSION" ]] || VERSION="0.1.0"
VERSION="${VERSION//-/_}"
VERSION="${VERSION// /_}"

if ! command -v clang >/dev/null 2>&1; then
    echo "==> 安装 NativeAOT 构建依赖 clang"
    if [[ "$YES" == true ]]; then
        sudo pacman -S --needed --noconfirm clang
    else
        sudo pacman -S --needed clang
    fi
fi

BUILD="$(mktemp -d "${TMPDIR:-/tmp}/ordoa-arch.XXXXXX")"
cleanup() {
    if [[ "$KEEP_BUILD" == true ]]; then
        echo "构建目录保留在: $BUILD"
    else
        rm -rf "$BUILD"
    fi
}
trap cleanup EXIT

PAYLOAD="$BUILD/payload"
echo "==> 构建 NativeAOT 发布 ($RID)"
"$ROOT/scripts/publish.sh" aot --rid "$RID" --output "$PAYLOAD"

[[ -f "$PAYLOAD/Ordoa" ]] || fail "发布输出中缺少 Ordoa。"
[[ -f "$PAYLOAD/libSkiaSharp.so" ]] || fail "发布输出中缺少 libSkiaSharp.so。"
[[ -f "$PAYLOAD/libHarfBuzzSharp.so" ]] || fail "发布输出中缺少 libHarfBuzzSharp.so。"

ROOT_Q="$(printf '%q' "$ROOT")"
PAYLOAD_Q="$(printf '%q' "$PAYLOAD")"

cat > "$BUILD/PKGBUILD" <<EOF
pkgname=ordoa
pkgver=$VERSION
pkgrel=1
pkgdesc="A minimal, powerful file manager for Linux"
arch=('$ARCH')
url="https://github.com/whynbnb/Ordoa"
license=('MIT')
depends=('glibc' 'fontconfig' 'freetype2' 'zlib' 'libx11' 'libxext' 'libxcb' 'wayland' 'libffi' 'gcc-libs' 'libglvnd' 'desktop-file-utils' 'hicolor-icon-theme')
optdepends=('ffmpeg: video and audio previews' 'poppler: PDF first-page previews')
makedepends=('clang')
options=('!strip')
repo_root=$ROOT_Q
payload_dir=$PAYLOAD_Q

package() {
    install -d "\$pkgdir/usr/lib/ordoa"
    cp -a "\$payload_dir/." "\$pkgdir/usr/lib/ordoa/"

    install -d "\$pkgdir/usr/bin"
    ln -s ../lib/ordoa/Ordoa "\$pkgdir/usr/bin/ordoa"

    install -Dm644 "\$repo_root/packaging/ordoa.desktop" \
        "\$pkgdir/usr/share/applications/ordoa.desktop"

    for size in 16 24 32 48 64 128 256 512 1024; do
        install -Dm644 "\$repo_root/packaging/icons/ordoa-\${size}.png" \
            "\$pkgdir/usr/share/icons/hicolor/\${size}x\${size}/apps/ordoa.png"
    done

    install -Dm644 "\$repo_root/LICENSE" \
        "\$pkgdir/usr/share/licenses/ordoa/LICENSE"
}
EOF

echo "==> 生成 pacman 包"
MAKEPKG_ARGS=(--force --cleanbuild)
if [[ "$YES" == true ]]; then
    MAKEPKG_ARGS+=(--noconfirm)
fi
(
    cd "$BUILD"
    PKGDEST="$BUILD" makepkg "${MAKEPKG_ARGS[@]}" -s
)

PACKAGE="$(find "$BUILD" -maxdepth 1 -type f -name 'ordoa-*.pkg.tar.*' -printf '%T@ %p\n' | sort -nr | head -1 | cut -d' ' -f2-)"
[[ -n "$PACKAGE" && -f "$PACKAGE" ]] || fail "没有找到构建出的 pacman 包。"

echo "==> 已生成: $PACKAGE"

if [[ "$BUILD_ONLY" == true ]]; then
    echo "未安装。使用 pacman -U 安装该包。"
    echo "$PACKAGE"
    exit 0
fi

if [[ "$YES" == true ]]; then
    sudo pacman -U --noconfirm "$PACKAGE"
else
    sudo pacman -U "$PACKAGE"
fi

echo
echo "Ordoa 已通过 pacman 安装。"
echo "  命令: ordoa"
echo "  菜单: /usr/share/applications/ordoa.desktop"
