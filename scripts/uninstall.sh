#!/usr/bin/env bash
set -euo pipefail

PREFIX="${PREFIX:-$HOME/.local}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --prefix) PREFIX="${2:?缺少路径}"; shift ;;
        --purge) PURGE=1 ;;
        -h|--help)
            echo "用法: $0 [--prefix PATH] [--purge]"
            exit 0
            ;;
        *) echo "未知参数: $1" >&2; exit 1 ;;
    esac
    shift
done

rm -rf "$PREFIX/lib/ordoa"
rm -f "$PREFIX/bin/ordoa"
rm -f "$PREFIX/share/applications/ordoa.desktop"
for size in 16 24 32 48 64 128 256 512 1024; do
    rm -f "$PREFIX/share/icons/hicolor/${size}x${size}/apps/ordoa.png"
done

if [[ "${PURGE:-0}" == "1" ]]; then
    rm -rf "$HOME/.config/ordoa" "$HOME/.cache/ordoa" "$HOME/.local/state/ordoa"
    echo "已移除 Ordoa 及本地设置（回收站内容未删除）。"
else
    echo "已移除 Ordoa。设置和缓存仍然保留；使用 --purge 可一并删除。"
fi

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
fi
