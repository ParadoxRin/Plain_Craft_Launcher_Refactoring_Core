#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$root/.tooling/dotnet/dotnet}"
destination="${1:?client publish directory}"
rid="${2:?runtime}"
# Only the macOS ARM64 reference client has platform replacement acceptance.
if [ "$rid" != osx-arm64 ]; then exit 0; fi
"$sdk" publish "$root/tools/Pcl.ClientUpdater/Pcl.ClientUpdater.csproj" -c Release -r "$rid" --self-contained true \
  -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false \
  -p:PathMap="$root=/src/Plain-Craft-Launcher-Refactoring" -o "$destination/updater"
python3 - "$root/release.json" "$destination/client-release.json" "$rid" <<'PY'
import json,sys
release=json.load(open(sys.argv[1]))
with open(sys.argv[2],'w') as output:
    json.dump({'Version':release['version'],'Sequence':release['build'],'Runtime':sys.argv[3]},output,indent=2)
PY
if [ -n "${PCL_CLIENT_UPDATE_POLICY:-}" ]; then
  cp "$PCL_CLIENT_UPDATE_POLICY" "$destination/client-update-policy.json"
fi
