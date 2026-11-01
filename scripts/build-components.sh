#!/bin/bash
# Independent deployment directories; existing .app packaging remains available.
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$project_root/.tooling/dotnet/dotnet}"
python3 "$project_root/scripts/verify-core-baseline.py"
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|osx-x64|linux-x64|linux-arm64|win-x64|win-arm64) ;; *) echo 'Unsupported RID' >&2; exit 2;; esac
version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$project_root/release.json")"
python3 - "$project_root" "$version" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
root = pathlib.Path(sys.argv[1])
for project in ('Pcl.Desktop', 'Pcl.Host'):
    if ET.parse(root / 'src' / project / (project + '.csproj')).findtext('.//Version') != sys.argv[2]:
        raise SystemExit(project + ' version does not match release.json')
import json
release = json.loads((root / 'release.json').read_text())
sequence = ET.parse(root / 'src/Pcl.Host/Pcl.Host.csproj').findtext('.//HostUpdateSequence')
if int(sequence) != release['hostSequence']:
    raise SystemExit('Host sequence does not match release.json')
PY
work="${PCL_COMPONENT_OUTPUT:-$project_root/artifacts/components-$version-$rid}"
case "$rid" in osx-*|linux-*) command -v cc >/dev/null || { echo 'Unix Host builds require a native C11 compiler (cc)' >&2; exit 2; };; esac
if [ -e "$work" ]; then echo "Output already exists: $work" >&2; exit 2; fi
mkdir -p "$work"
export DOTNET_CLI_HOME="$project_root/.cache/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
publish() {
    "$sdk" publish "$1" -c Release -r "$rid" --self-contained true -m:1 -nr:false \
        -p:NuGetAudit=false -p:UsedAvaloniaProducts= -p:PublishTrimmed=false \
        -p:UseAppHost=true -p:UseSharedCompilation=false -p:BundleHost=false \
        -p:PathMap="$project_root=/src/Plain-Craft-Launcher-Refactoring" \
        -p:DebugType=None -p:DebugSymbols=false \
        -p:MicrosoftApplicationConfig="$project_root/src/Pcl.Core/Authentication/microsoft-application.json" -o "$2"
}
publish "$project_root/src/Pcl.Desktop/Pcl.Desktop.csproj" "$work/client"
DOTNET="$sdk" bash "$project_root/scripts/publish-client-updater.sh" "$work/client" "$rid"
publish "$project_root/src/Pcl.Host/Pcl.Host.csproj" "$work/host"
if [ -n "${PCL_CONTENT_POLICY:-}" ]; then
  cp "$PCL_CONTENT_POLICY" "$work/host/content-update-policy.json"
fi
python3 "$project_root/scripts/prepare-component-legal.py" "$project_root" "$work/client" "$work/host"
entry="$work/host/Pcl.Host"
if [[ "$rid" == win-* ]]; then entry="$entry.exe"; fi
# Execute on the target platform, never infer metadata from the build machine.
"$entry" --update-probe > "$work/host-probe.json"
python3 "$project_root/scripts/package-components.py" --client "$work/client" --host "$work/host" \
    --probe "$work/host-probe.json" --release "$project_root/release.json" --output "$work/packages" "${@:2}"
