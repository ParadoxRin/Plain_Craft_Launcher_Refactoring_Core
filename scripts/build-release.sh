#!/bin/bash
# Publish the current Mac reference client with mapped paths and scoped license notices.
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$project_root/.tooling/dotnet/dotnet}"
export DOTNET_CLI_HOME="$project_root/.cache/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$project_root/release.json")"
build="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["build"])' "$project_root/release.json")"
# Prevent About, assembly and archive versions from silently drifting apart.
python3 - "$project_root" "$version" <<'PY'
import pathlib,sys,xml.etree.ElementTree as ET
root=pathlib.Path(sys.argv[1])
actual=ET.parse(root/'src/Pcl.Desktop/Pcl.Desktop.csproj').findtext('.//Version')
if actual != sys.argv[2]: raise SystemExit('Desktop version does not match release.json')
PY
artifact_dir="$project_root/artifacts"
publish="$artifact_dir/publish-$version-osx-arm64"
app="$artifact_dir/Plain Craft Launcher Refactoring $version.app"
"$sdk" publish "$project_root/src/Pcl.Desktop/Pcl.Desktop.csproj" -c Release -r osx-arm64 --self-contained true -m:1 -nr:false \
  -p:NuGetAudit=false -p:UsedAvaloniaProducts= -p:PublishTrimmed=false -p:UseAppHost=true -p:UseSharedCompilation=false \
  -p:PathMap="$project_root=/src/Plain-Craft-Launcher-Refactoring" -p:DebugType=None -p:DebugSymbols=false \
  -p:MicrosoftApplicationConfig="$project_root/src/Pcl.Core/Authentication/microsoft-application.json" -o "$publish"
find "$publish" -type f -name '*.pdb' -delete
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
