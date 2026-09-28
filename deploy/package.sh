#!/usr/bin/env bash
# UniFlow 安装包打包脚本（Linux/Windows 单文件自包含）
#
# 用法：
#   APP_SETTINGS=/path/to/site-appsettings.json ./deploy/package.sh
#   VERSION=1.0.6.5 OUT=/path/out ./deploy/package.sh
#
# 环境变量：
#   VERSION       覆盖版本号（默认取 UniFlow.csproj 的 <Version>）
#   APP_SETTINGS  打进安装包的 appsettings.json（默认用仓库默认配置）
#   OUT           产物输出目录（默认 <repo>/release）
#   DOTNET        dotnet 可执行文件（默认 dotnet）
#
# 说明：单文件自包含发布，SQLite 原生库内嵌（IncludeNativeLibrariesForSelfExtract），
#       安装包内不再单独提供 libe_sqlite3.so / e_sqlite3.dll。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
CSPROJ="$ROOT/code/src/UniFlow/UniFlow.csproj"
VERSION="${VERSION:-$(grep -oPm1 '(?<=<Version>)[^<]+' "$CSPROJ")}"
APP_SETTINGS="${APP_SETTINGS:-$ROOT/code/src/UniFlow/appsettings.json}"
OUT="${OUT:-$ROOT/release}"

[ -n "$VERSION" ] || { echo "无法确定版本号" >&2; exit 1; }
[ -f "$APP_SETTINGS" ] || { echo "appsettings.json 不存在: $APP_SETTINGS" >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

PUBLISH_FLAGS=(
  --configuration Release
  --self-contained true
  -p:PublishSingleFile=true
  -p:EnableCompressionInSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true
)

echo "=== UniFlow $VERSION 打包 ==="
echo "  配置: $APP_SETTINGS"
echo "  输出: $OUT"

echo "--- 发布 linux-x64 ---"
"$DOTNET" publish "$CSPROJ" "${PUBLISH_FLAGS[@]}" --runtime linux-x64 -o "$STAGE/pub-linux-x64"
echo "--- 发布 win-x64 ---"
"$DOTNET" publish "$CSPROJ" "${PUBLISH_FLAGS[@]}" --runtime win-x64 -o "$STAGE/pub-win-x64"

# ---- Linux 包（uniflow/ 目录）----
LINUX="$STAGE/linux"
mkdir -p "$LINUX/uniflow"
cp -f  "$STAGE/pub-linux-x64/UniFlow"                          "$LINUX/uniflow/"
cp -f  "$STAGE/pub-linux-x64/UniFlow.pdb"                      "$LINUX/uniflow/"
cp -f  "$STAGE/pub-linux-x64/UniFlow.staticwebassets.endpoints.json" "$LINUX/uniflow/"
cp -rf "$STAGE/pub-linux-x64/wwwroot"                          "$LINUX/uniflow/"
cp -f  "$APP_SETTINGS"                                         "$LINUX/uniflow/appsettings.json"
cp -f  "$ROOT/docs/CONFIG-REFERENCE.md"                        "$LINUX/uniflow/"
cp -f  "$ROOT/deploy/linux/install.sh"                         "$LINUX/uniflow/"

# ---- Windows 包（uniflow/ 目录）----
WIN="$STAGE/win"
mkdir -p "$WIN/uniflow"
cp -f  "$STAGE/pub-win-x64/UniFlow.exe"                        "$WIN/uniflow/"
cp -f  "$STAGE/pub-win-x64/UniFlow.pdb"                        "$WIN/uniflow/"
cp -f  "$STAGE/pub-win-x64/UniFlow.staticwebassets.endpoints.json" "$WIN/uniflow/"
cp -f  "$STAGE/pub-win-x64/web.config"                         "$WIN/uniflow/"
cp -rf "$STAGE/pub-win-x64/wwwroot"                            "$WIN/uniflow/"
cp -f  "$APP_SETTINGS"                                         "$WIN/uniflow/appsettings.json"
cp -f  "$ROOT/docs/CONFIG-REFERENCE.md"                        "$WIN/uniflow/"
cp -f  "$ROOT/deploy/INSTALL.md"                               "$WIN/uniflow/"
cp -f  "$ROOT/deploy/windows/README.md"                        "$WIN/uniflow/README.md"
cp -f  "$ROOT/deploy/windows/install.ps1"                      "$WIN/uniflow/"
cp -f  "$ROOT/deploy/windows/install-enable.ps1"               "$WIN/uniflow/"
cp -f  "$ROOT/deploy/windows/uninstall.ps1"                    "$WIN/uniflow/"

# ---- 归档 ----
mkdir -p "$OUT"
LINUX_PKG="$OUT/UniFlow-${VERSION}-linux-x64.tar.gz"
WIN_PKG="$OUT/UniFlow-${VERSION}-win-x64.zip"
( cd "$LINUX" && tar -czf "$LINUX_PKG" uniflow )
( cd "$WIN"   && zip -qr "$WIN_PKG" uniflow )

echo "=== 完成 ==="
ls -la "$LINUX_PKG" "$WIN_PKG"
