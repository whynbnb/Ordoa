#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="${PROJECT:-$ROOT/src/Ordoa.App/Ordoa.App.csproj}"

MODE="${MODE:-framework}"
RID="${RID:-}"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT="${OUTPUT:-}"
CLEAN=true
RESTORE=true
PORTABLE=false

usage() {
    cat <<EOF
发布 Ordoa。

用法:
  ./scripts/publish.sh [模式] [选项]

模式:
  framework   托管发布（多文件，依赖目标机安装 .NET 10 运行时）
  single      托管单文件（依赖目标机安装 .NET 10 运行时）
  aot         NativeAOT 自包含发布（无需 .NET 运行时，但需要 C/C++ 工具链）
  aot-static  NativeAOT 静态单文件（SkiaSharp / HarfBuzz / ANGLE 静态链接）

选项:
  --mode <模式>       同位置参数，取值为 framework、single、aot、aot-static
  --rid <RID>         目标运行时标识，例如 linux-x64、linux-arm64、osx-arm64、win-x64
                      省略时自动检测当前 RID；framework 模式可用 --portable
  -c, --configuration <配置>   Release 或 Debug，默认 Release
  -o, --output <目录>  输出目录，默认 artifacts/publish/<模式>/<RID|portable>
  --portable          仅 framework 模式：输出不绑定 RID 的可移植版本
  --no-clean          保留输出目录已有内容
  --no-restore        跳过 dotnet restore
  -h, --help          显示本帮助

示例:
  ./scripts/publish.sh framework
  ./scripts/publish.sh single --rid linux-arm64
  ./scripts/publish.sh aot
  ./scripts/publish.sh aot-static
  MODE=aot-static RID=linux-x64 ./scripts/publish.sh

说明:
  - single 使用“托管单文件”，SkiaSharp / HarfBuzz 原生库会随单文件自解压。
  - aot 输出仍包含 SkiaSharp / HarfBuzz 原生库，不是单文件。
  - aot-static 使用 StaticLink.Avalonia 静态链接，输出一个可执行文件；
    仍依赖系统 fontconfig / freetype / zlib 等库。
  - NativeAOT 需要 clang 或 gcc；aot-static 在 Linux 上还需要 lld。
  - Windows 需要 Visual Studio C++ 生成工具。
EOF
}

fail() {
    echo "错误: $*" >&2
    exit 1
}

detect_rid() {
    local reported os arch
    reported="$(dotnet --info 2>/dev/null | awk -F': *' '/^ *RID:/{print $2; exit}')"
    case "$reported" in
        linux-*|osx-*|win-*)
            printf '%s' "$reported"
            return
            ;;
    esac

    os="$(uname -s 2>/dev/null || true)"
    arch="$(uname -m 2>/dev/null || true)"
    case "$os:$arch" in
        Linux:x86_64|Linux:amd64) printf 'linux-x64' ;;
        Linux:aarch64|Linux:arm64) printf 'linux-arm64' ;;
        Linux:armv7l|Linux:armv8l) printf 'linux-arm' ;;
        Darwin:x86_64) printf 'osx-x64' ;;
        Darwin:arm64|Darwin:aarch64) printf 'osx-arm64' ;;
        MINGW*:x86_64|MSYS*:x86_64|CYGWIN*:x86_64) printf 'win-x64' ;;
        MINGW*:aarch64|MSYS*:aarch64|CYGWIN*:aarch64) printf 'win-arm64' ;;
        *) fail "无法自动检测 RID（dotnet 报告: ${reported:-未知}），请使用 --rid 指定。" ;;
    esac
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        framework|single|aot|aot-static)
            MODE="$1"
            ;;
        --mode|-m)
            [[ $# -ge 2 ]] || fail "$1 缺少参数"
            MODE="$2"
            shift
            ;;
        --rid|-r)
            [[ $# -ge 2 ]] || fail "$1 缺少参数"
            RID="$2"
            shift
            ;;
        --configuration|-c)
            [[ $# -ge 2 ]] || fail "$1 缺少参数"
            CONFIGURATION="$2"
            shift
            ;;
        --output|-o)
            [[ $# -ge 2 ]] || fail "$1 缺少参数"
            OUTPUT="$2"
            shift
            ;;
        --portable) PORTABLE=true ;;
        --no-clean) CLEAN=false ;;
        --no-restore) RESTORE=false ;;
        -h|--help) usage; exit 0 ;;
        *) fail "未知参数: $1（使用 --help 查看帮助）" ;;
    esac
    shift
done

case "$MODE" in
    framework|single|aot|aot-static) ;;
    *) fail "未知模式: $MODE（可选 framework、single、aot、aot-static）" ;;
esac

case "$CONFIGURATION" in
    Release|Debug|release|debug) ;;
    *) fail "配置必须是 Release 或 Debug" ;;
esac

command -v dotnet >/dev/null 2>&1 || fail "未找到 dotnet，请先安装 .NET 10 SDK。"

if [[ "$PORTABLE" == true ]]; then
    [[ "$MODE" == "framework" ]] || fail "--portable 仅适用于 framework 模式。"
    [[ -z "$RID" ]] || fail "--portable 与 --rid 不能同时使用。"
elif [[ -z "$RID" ]]; then
    RID="$(detect_rid)"
fi

if [[ -z "$OUTPUT" ]]; then
    OUTPUT="$ROOT/artifacts/publish/$MODE/${RID:-portable}"
fi

mkdir -p "$OUTPUT"
OUTPUT_ABS="$(cd "$OUTPUT" && pwd)"
case "$OUTPUT_ABS" in
    "/"|"$HOME"|"$ROOT")
        fail "拒绝清空危险目录: $OUTPUT_ABS"
        ;;
esac

if [[ "$CLEAN" == true ]]; then
    shopt -s dotglob nullglob
    rm -rf -- "$OUTPUT_ABS"/*
    shopt -u dotglob nullglob
fi

if [[ "$MODE" == "aot" || "$MODE" == "aot-static" ]] &&
   ! command -v clang >/dev/null 2>&1 &&
   ! command -v gcc >/dev/null 2>&1 && ! command -v cc >/dev/null 2>&1; then
    echo "警告: 未找到 clang/gcc；NativeAOT 发布通常需要本机 C/C++ 工具链。" >&2
fi

if [[ "$MODE" == "aot-static" ]]; then
    case "$RID" in
        linux-*)
            if ! command -v ld.lld >/dev/null 2>&1 && ! command -v lld >/dev/null 2>&1; then
                fail "aot-static 在 Linux 上需要 lld。请安装 lld 后重试。"
            fi
            ;;
    esac
fi

PUBLISH_ARGS=(
    publish "$PROJECT"
    -c "$CONFIGURATION"
    -o "$OUTPUT_ABS"
    -p:DebugType=None
    -p:DebugSymbols=false
)

if [[ -n "$RID" ]]; then
    PUBLISH_ARGS+=(-r "$RID")
fi
if [[ "$RESTORE" == false ]]; then
    PUBLISH_ARGS+=(--no-restore)
    if [[ "$MODE" == "aot" || "$MODE" == "aot-static" ]]; then
        echo "警告: AOT 与 --no-restore 仅在之前已针对 $RID 还原 AOT 工具链时可靠。" >&2
    fi
fi

case "$MODE" in
    framework)
        PUBLISH_ARGS+=(--self-contained false -p:PublishSingleFile=false)
        ;;
    single)
        PUBLISH_ARGS+=(
            --self-contained false
            -p:PublishSingleFile=true
            -p:IncludeNativeLibrariesForSelfExtract=true
        )
        ;;
    aot)
        PUBLISH_ARGS+=(
            --self-contained true
            -p:PublishAot=true
            -p:StripSymbols=true
            -p:TrimmerSingleWarn=true
        )
        ;;
    aot-static)
        PUBLISH_ARGS+=(
            --self-contained true
            -p:PublishAot=true
            -p:PublishSingleFile=false
            -p:UseStaticLinkAvalonia=true
            -p:StripSymbols=true
            -p:TrimmerSingleWarn=true
        )
        ;;
esac

echo "==> Ordoa 发布"
echo "    模式: $MODE"
echo "    配置: $CONFIGURATION"
echo "    RID:  ${RID:-portable}"
echo "    输出: $OUTPUT_ABS"
echo

dotnet "${PUBLISH_ARGS[@]}"

if [[ "$CONFIGURATION" == "Release" || "$CONFIGURATION" == "release" || "$MODE" == "single" ]]; then
    # Debug symbols from managed/native packages are not needed at runtime.
    find "$OUTPUT_ABS" -type f -name '*.pdb' -delete
fi

COUNT="$(find "$OUTPUT_ABS" -type f | wc -l | tr -d ' ')"
SIZE="$(du -sh "$OUTPUT_ABS" | awk '{print $1}')"
EXE="$OUTPUT_ABS/Ordoa"
if [[ "$RID" == win* ]]; then
    EXE="$OUTPUT_ABS/Ordoa.exe"
elif [[ -z "$RID" ]]; then
    case "$(uname -s 2>/dev/null || true)" in
        MINGW*|MSYS*|CYGWIN*) EXE="$OUTPUT_ABS/Ordoa.exe" ;;
    esac
fi

if [[ "$MODE" == "aot" || "$MODE" == "aot-static" ]]; then
    [[ -f "$EXE" ]] || fail "AOT 发布未生成原生可执行文件。若使用了 --no-restore，请先去掉它重新发布。"
    if [[ -f "$OUTPUT_ABS/Ordoa.dll" ]]; then
        fail "输出看起来是托管发布而不是 AOT。请去掉 --no-restore 重新发布。"
    fi
fi

if [[ "$MODE" == "aot-static" ]]; then
    if find "$OUTPUT_ABS" -type f \( -name '*.so' -o -name '*.dylib' -o -name '*.dll' -o -name '*.a' \) | grep -q .; then
        fail "aot-static 输出仍包含动态/静态原生库文件；请检查 StaticLink.Avalonia 是否生效。"
    fi
fi

echo
echo "==> 发布完成"
echo "    输出: $OUTPUT_ABS"
echo "    文件: $COUNT 个，共 $SIZE"
if [[ -f "$EXE" ]]; then
    echo "    程序: $EXE"
fi
if [[ "$MODE" == "framework" ]]; then
    echo "    提示: 目标机需要安装 .NET 10 Desktop Runtime。"
elif [[ "$MODE" == "single" ]]; then
    echo "    提示: 目标机需要安装 .NET 10 Desktop Runtime；原生库在首次启动时自解压。"
elif [[ "$MODE" == "aot" ]]; then
    echo "    提示: AOT 输出仍需携带同目录下的 SkiaSharp / HarfBuzz 原生库。"
else
    echo "    提示: aot-static 为单文件；SkiaSharp / HarfBuzz / ANGLE 已静态链接，仍依赖系统 fontconfig / freetype / zlib 等库。"
fi
