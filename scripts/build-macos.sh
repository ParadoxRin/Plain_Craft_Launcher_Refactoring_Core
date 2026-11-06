#!/bin/bash
# 从当前源码构建 macOS ARM64 安装包。
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
bash scripts/build-release.sh
