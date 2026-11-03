#!/usr/bin/env bash
set -euo pipefail

PURGE=false
YES=false

fail() {
    echo "错误: $*" >&2
    exit 1
}

usage() {
    cat <<EOF
用法: $0 [选项]

卸载通过 scripts/install-arch.sh 安装的 Ordoa。

选项:
  --purge   同时删除当前用户的设置、缓存和状态（不删除回收站内容）
  --yes     不询问 pacman 的确认
  -h, --help
            显示本帮助

示例:
  ./scripts/uninstall-arch.sh
  ./scripts/uninstall-arch.sh --purge --yes
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --purge) PURGE=true ;;
        --yes) YES=true ;;
        -h|--help) usage; exit 0 ;;
        *) fail "未知参数: $1（使用 --help 查看帮助）" ;;
    esac
    shift
done

command -v pacman >/dev/null 2>&1 || fail "未找到 pacman，这不是 Arch 系统。"

if [[ $EUID -ne 0 ]]; then
    command -v sudo >/dev/null 2>&1 || fail "未找到 sudo。"
fi

if ! pacman -Qi ordoa >/dev/null 2>&1; then
    echo "Ordoa 未通过 pacman 安装，跳过包卸载。"
else
    PACMAN_ARGS=(-Rns)
    if [[ "$YES" == true ]]; then
        PACMAN_ARGS+=(--noconfirm)
    fi

    if [[ $EUID -eq 0 ]]; then
        pacman "${PACMAN_ARGS[@]}" ordoa
    else
        sudo pacman "${PACMAN_ARGS[@]}" ordoa
    fi
fi

if [[ "$PURGE" == true ]]; then
    rm -rf "$HOME/.config/ordoa" "$HOME/.cache/ordoa" \
        "$HOME/.local/state/ordoa" "$HOME/.local/share/ordoa"
    echo "已移除 Ordoa 及本地设置（回收站内容未删除）。"
else
    echo "已移除 Ordoa。设置和缓存仍然保留；使用 --purge 可一并删除。"
fi
