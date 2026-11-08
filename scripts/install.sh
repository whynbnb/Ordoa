#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="${PREFIX:-$HOME/.local}"
RID="${RID:-linux-x64}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"

usage() {
    cat <<EOF
用法: $0 [选项]

安装 Ordoa 到当前用户（默认 ~/.local）。

环境变量:
  PREFIX           安装前缀，默认 \$HOME/.local
  RID              .NET 运行时标识，默认 linux-x64
  SELF_CONTAINED   是否打包运行时，默认 false
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --self-contained) SELF_CONTAINED=true ;;
        --prefix) PREFIX="${2:?缺少路径}"; shift ;;
        --rid) RID="${2:?缺少 RID}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "未知参数: $1" >&2; usage; exit 1 ;;
    esac
    shift
done

if ! command -v dotnet >/dev/null 2>&1; then
    echo "未找到 dotnet，请先安装 .NET 10 SDK。" >&2
    exit 1
fi

OUT="$ROOT/artifacts/publish/$RID"
echo "==> 发布 Ordoa ($RID, 自包含=$SELF_CONTAINED)"
dotnet publish "$ROOT/src/Ordoa.App/Ordoa.App.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained "$SELF_CONTAINED" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$OUT"

echo "==> 安装到 $PREFIX"
install -d "$PREFIX/bin" "$PREFIX/lib" \
    "$PREFIX/share/applications" \
    "$PREFIX/share/icons/hicolor"

rm -rf "$PREFIX/lib/ordoa"
install -d "$PREFIX/lib/ordoa"
cp -a "$OUT/." "$PREFIX/lib/ordoa/"
ln -sfn "$PREFIX/lib/ordoa/Ordoa" "$PREFIX/bin/ordoa"

for size in 16 24 32 48 64 128 256 512 1024; do
    icon_dir="$PREFIX/share/icons/hicolor/${size}x${size}/apps"
    install -d "$icon_dir"
    install -m 644 "$ROOT/packaging/icons/ordoa-$size.png" "$icon_dir/ordoa.png"
done

desktop_src="$ROOT/packaging/ordoa.desktop"
desktop_dst="$PREFIX/share/applications/ordoa.desktop"
sed "s|^Exec=.*|Exec=\"$PREFIX/bin/ordoa\" %U|" "$desktop_src" > "$desktop_dst"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true
fi

cat <<EOF

Ordoa 已安装。
  程序: $PREFIX/lib/ordoa/Ordoa
  命令: $PREFIX/bin/ordoa
  菜单: $PREFIX/share/applications/ordoa.desktop

如果 $PREFIX/bin 不在 PATH 中，请将其加入 PATH：
  export PATH="$PREFIX/bin:\$PATH"
EOF
