#!/bin/bash
# Publish the current Mac reference client with mapped paths and scoped license notices.
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$project_root/.tooling/dotnet/dotnet}"
python3 "$project_root/scripts/verify-core-baseline.py"
export DOTNET_CLI_HOME="$project_root/.cache/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$project_root/release.json")"
build="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["build"])' "$project_root/release.json")"
# Prevent About, assembly and archive versions from silently drifting apart.
python3 - "$project_root" "$version" <<'PY'
import json,pathlib,sys,xml.etree.ElementTree as ET
root=pathlib.Path(sys.argv[1])
for project in ('Pcl.Desktop', 'Pcl.Host'):
    actual=ET.parse(root/'src'/project/(project+'.csproj')).findtext('.//Version')
    if actual != sys.argv[2]: raise SystemExit(project+' version does not match release.json')
sequence=ET.parse(root/'src/Pcl.Host/Pcl.Host.csproj').findtext('.//HostUpdateSequence')
if int(sequence) != json.loads((root/'release.json').read_text())['hostSequence']:
    raise SystemExit('Host sequence does not match release.json')
PY
bash "$project_root/scripts/build-native.sh"
artifact_dir="${PCL_RELEASE_OUTPUT:-$project_root/artifacts}"
publish="$artifact_dir/publish-$version-osx-arm64"
app="$artifact_dir/Plain Craft Launcher Refactoring $version.app"
if [ -e "$publish" ] || [ -e "$app" ]; then
  echo 'Release output already exists; use a fresh PCL_RELEASE_OUTPUT directory.' >&2
  exit 2
fi
mkdir -p "$artifact_dir"
"$sdk" publish "$project_root/src/Pcl.Desktop/Pcl.Desktop.csproj" -c Release -r osx-arm64 --self-contained true -m:1 -nr:false \
  -p:NuGetAudit=false -p:EnableDesktopAcceptance=false -p:UsedAvaloniaProducts= -p:PublishTrimmed=false -p:UseAppHost=true -p:UseSharedCompilation=false \
  -p:PathMap="$project_root=/src/Plain-Craft-Launcher-Refactoring" -p:DebugType=None -p:DebugSymbols=false \
  -p:MicrosoftApplicationConfig="$project_root/src/Pcl.Core/Authentication/microsoft-application.json" -o "$publish"
# Composition belongs to the installer pipeline, never to the Desktop project.
"$sdk" publish "$project_root/src/Pcl.Host/Pcl.Host.csproj" -c Release -r osx-arm64 --self-contained true -m:1 -nr:false \
  -p:NuGetAudit=false -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false \
  -p:PathMap="$project_root=/src/Plain-Craft-Launcher-Refactoring" \
  -p:MicrosoftApplicationConfig="$project_root/src/Pcl.Core/Authentication/microsoft-application.json" -o "$publish/host"
"$sdk" build "$project_root/scripts/ArchitectureGuard/ArchitectureGuard.csproj" -c Release -m:1 -nr:false -p:NuGetAudit=false
"$sdk" "$project_root/scripts/ArchitectureGuard/bin/Release/net10.0/ArchitectureGuard.dll" "$project_root" "$publish/PCL-MacPort.dll" "$artifact_dir/architecture-guard.json" "$publish/host"
DOTNET="$sdk" bash "$project_root/scripts/publish-client-updater.sh" "$publish" osx-arm64
if [ -n "${PCL_HOST_UPDATE_POLICY:-}" ]; then
  cp "$PCL_HOST_UPDATE_POLICY" "$publish/host-update-policy.json"
fi
find "$publish" -type f -name '*.pdb' -delete
if [ -n "${PCL_CONTENT_POLICY:-}" ]; then
  cp "$PCL_CONTENT_POLICY" "$publish/host/content-update-policy.json"
fi
test -f "$publish/host/libhostfxr.dylib"
test -x "$publish/host/Pcl.Host"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/Legal"
cp -R "$publish/." "$app/Contents/MacOS/"
find "$app/Contents/MacOS" -type f -name '*.pdb' -delete
cp "$project_root/PCL-LICENCE.txt" "$project_root/LICENSE.md" "$project_root/LICENSE-MIT.txt" "$app/Contents/Resources/Legal/"
cp "$project_root/vendor/MeloongCore/LICENSE.txt" "$app/Contents/Resources/Legal/MeloongCore-LICENSE.txt"
cp "$project_root/docs/"*-NOTICE.txt "$project_root/docs/"*-LICENSE.txt "$project_root/docs/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/Legal/"
cp "$project_root/docs/开发方式与责任.md" "$project_root/release.json" "$app/Contents/Resources/"
PCL_PACKAGE_VERSION="$version" PCL_PACKAGE_BUILD="$build" python3 "$project_root/scripts/package-macos.py" "$project_root" "$app"
python3 "$project_root/scripts/audit-release-privacy.py" "$app" --report "$artifact_dir/release-privacy-$version.json"
python3 "$project_root/scripts/package-app-archive.py" "$app" "$artifact_dir/Plain-Craft-Launcher-Refactoring-$version-macOS-arm64.zip" --report "$artifact_dir/release-archive-$version.json"
echo "Built: $app"
