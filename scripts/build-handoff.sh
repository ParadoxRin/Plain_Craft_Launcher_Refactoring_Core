#!/bin/bash
# 从源码交接快照重建本机桥接与当前自包含应用。只写当前工程的生成目录。
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_root"
if [ -n "${DOTNET:-}" ]; then
    sdk="$DOTNET"
elif [ -x "$project_root/.tooling/dotnet/dotnet" ]; then
    sdk="$project_root/.tooling/dotnet/dotnet"
else
    python3 scripts/bootstrap-dotnet.py
    sdk="$project_root/.tooling/dotnet/dotnet"
fi
export DOTNET="$sdk"
bash scripts/build-native.sh
bash scripts/build-release.sh
