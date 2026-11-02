#!/usr/bin/env bash
# Build LIS Agent cho Windows bằng Go 1.20 (bản Go cuối cùng chạy được trên Windows 7).
# Cần: go install golang.org/dl/go1.20.14@latest && go1.20.14 download
set -euo pipefail
cd "$(dirname "$0")"
GO=${GO:-go1.20.14}
VERSION=${VERSION:-$(date +%Y.%m.%d)}
mkdir -p dist
"$GO" test ./...
for arch in amd64 386; do
  GOOS=windows GOARCH=$arch "$GO" build -trimpath -ldflags "-s -w -X main.version=$VERSION" -o "dist/lis-agent-$arch.exe" ./cmd/lis-agent
done
ls -la dist
