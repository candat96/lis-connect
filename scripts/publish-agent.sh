#!/usr/bin/env bash
# Publish agent cho Windows x64: một tệp LisAgent.exe tự chứa (đã nén) + version.txt.
# Dùng: bash scripts/publish-agent.sh [thư-mục-ra]   (mặc định dist/agent)
# Cùng tham số publish với stage "agent" trong Dockerfile — đổi một nơi thì đổi cả nơi kia.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-$REPO_ROOT/dist/agent}"
PROJECT="$REPO_ROOT/src/Lis.Agent/Lis.Agent.csproj"

mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

if ! command -v dotnet >/dev/null 2>&1; then
  export PATH="$HOME/.dotnet:$PATH"
fi

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version | tr -d '[:space:]')"
if [ -z "$VERSION" ]; then
  echo "Không đọc được <Version> của Lis.Agent.csproj" >&2
  exit 1
fi

PUBLISH_TMP="$(mktemp -d)"
trap 'rm -rf "$PUBLISH_TMP"' EXIT

echo "Publish LisAgent $VERSION (win-x64, tự chứa, một tệp nén)..."
dotnet publish "$PROJECT" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$PUBLISH_TMP"

cp "$PUBLISH_TMP/LisAgent.exe" "$OUT_DIR/LisAgent.exe"
printf '%s\n' "$VERSION" > "$OUT_DIR/version.txt"

SIZE="$(wc -c < "$OUT_DIR/LisAgent.exe" | tr -d '[:space:]')"
echo "Xong: $OUT_DIR/LisAgent.exe ($((SIZE / 1024 / 1024)) MB, $SIZE byte), version $VERSION"
