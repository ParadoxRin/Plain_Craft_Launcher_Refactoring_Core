#!/bin/bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$project_root/artifacts/native"
xcrun clang -dynamiclib -fobjc-arc -fblocks -arch arm64 -mmacosx-version-min=14.0 -framework Cocoa -Wl,-install_name,@rpath/libpclwindow.dylib "$project_root/src/Pcl.Native/window-agent.m" -o "$project_root/artifacts/native/libpclwindow.dylib"
codesign --force --sign - "$project_root/artifacts/native/libpclwindow.dylib"
