#!/bin/bash
# Build the complete core without building any desktop/client project.
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$project_root/.tooling/dotnet/dotnet}"
rid="${1:?Supply the native RID}"
case "$rid" in osx-arm64|osx-x64|linux-x64|linux-arm64|win-x64|win-arm64) ;; *) exit 2;; esac
python3 "$project_root/scripts/verify-core-baseline.py"
version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$project_root/release.json")"
work="${PCL_HOST_PACKAGE_OUTPUT:-$project_root/artifacts/host-package-$version-$rid}"
if [ -e "$work" ]; then echo "Output already exists: $work" >&2; exit 2; fi
case "$rid" in osx-*|linux-*) command -v cc >/dev/null;; esac
mkdir -p "$work"
export DOTNET_CLI_HOME="$project_root/.cache/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
"$sdk" publish "$project_root/src/Pcl.Host/Pcl.Host.csproj" -c Release -r "$rid" --self-contained true -m:1 -nr:false \
  -p:NuGetAudit=false -p:PublishTrimmed=false -p:UseAppHost=true -p:UseSharedCompilation=false \
  -p:PathMap="$project_root=/src/Plain-Craft-Launcher-Refactoring" -p:DebugType=None -p:DebugSymbols=false \
  -p:MicrosoftApplicationConfig="$project_root/src/Pcl.Core/Authentication/microsoft-application.json" -o "$work/host"
if [ -n "${PCL_CONTENT_POLICY:-}" ]; then
  cp "$PCL_CONTENT_POLICY" "$work/host/content-update-policy.json"
fi
python3 "$project_root/scripts/prepare-component-legal.py" "$project_root" "$work/host"
entry="$work/host/Pcl.Host"
if [[ "$rid" == win-* ]]; then entry="$entry.exe"; fi
"$entry" --update-probe > "$work/host-probe.json"
python3 "$project_root/scripts/package-host.py" "$work" "$project_root/release.json" "$rid"
