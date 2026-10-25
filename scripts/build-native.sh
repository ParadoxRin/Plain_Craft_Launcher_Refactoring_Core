#!/bin/bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$project_root/artifacts/native"
xcrun clang -dynamiclib -fobjc-arc -fblocks -arch arm64 -mmacosx-version-min=14.0 -framework Cocoa -Wl,-install_name,@rpath/libpclwindow.dylib "$project_root/src/Pcl.Native/window-agent.m" -o "$project_root/artifacts/native/libpclwindow.dylib"
codesign --force --sign - "$project_root/artifacts/native/libpclwindow.dylib"

# 固定版本与发布者校验和；已存在的合格构件可离线复用。
agent="$project_root/artifacts/native/lwjgl-unsafe-agent-2.0.jar"
expected="6c07d3508dc090a12076876bdc227a19ce195a41c9e2c6535c3f82213b1b2015"
if [ ! -f "$agent" ]; then
    curl --fail --location --max-time 60 https://repo.maven.apache.org/maven2/org/glavo/lwjgl-unsafe-agent/2.0/lwjgl-unsafe-agent-2.0.jar -o "$agent.partial"
    echo "$expected  $agent.partial" | shasum -a 256 -c -
    mv "$agent.partial" "$agent"
fi
if [ "${PCL_SKIP_CHECKS:-0}" != "1" ]; then
    echo "$expected  $agent" | shasum -a 256 -c -
fi
