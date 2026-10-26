#!/bin/bash
# Build the private 0.39 release with empty OAuth configuration and mapped source paths.
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$project_root/.tooling/dotnet/dotnet}"
export DOTNET_CLI_HOME="$project_root/.cache/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
artifact_dir="$project_root/artifacts"
publish="$artifact_dir/publish-0.39-osx-arm64"
app="$artifact_dir/Plain Craft Launcher Refactoring 0.39.app"
"$sdk" publish "$project_root/src/Pcl.Desktop/Pcl.Desktop.csproj" -c Release -r osx-arm64 --self-contained true --ignore-failed-sources \
  -p:NuGetAudit=false -p:PublishTrimmed=false -p:UseAppHost=true -p:UseSharedCompilation=false \
  -p:PathMap="$project_root=/src/Plain-Craft-Launcher-Refactoring" -p:DebugType=None -p:DebugSymbols=false \
  -p:MicrosoftApplicationConfig="$project_root/src/Pcl.Core/Authentication/microsoft-application.json" -o "$publish"
# Old intermediate builds may leave PDBs even when this publish disables symbols.
# Remove them in both generated outputs before copying and signing, including reused app bundles.
find "$publish" -type f -name '*.pdb' -delete
test -f "$publish/host/libhostfxr.dylib"
test -x "$publish/host/Pcl.Host"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/Legal"
cp -R "$publish/." "$app/Contents/MacOS/"
find "$app/Contents/MacOS" -type f -name '*.pdb' -delete
cp "$project_root/PCL-LICENCE.txt" "$app/Contents/Resources/Legal/"
cp "$project_root/vendor/MeloongCore/LICENSE.txt" "$app/Contents/Resources/Legal/MeloongCore-LICENSE.txt"
cp "$project_root/docs/"*-NOTICE.txt "$project_root/docs/"*-LICENSE.txt "$project_root/docs/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/Legal/"
cp "$project_root/docs/0.39-Quilt依赖覆盖与验收.md" "$app/Contents/Resources/"
PCL_PACKAGE_VERSION=0.39 PCL_PACKAGE_BUILD=390 python3 "$project_root/scripts/package-macos.py" "$project_root" "$app"
python3 "$project_root/scripts/audit-release-privacy.py" "$app" --report "$artifact_dir/release-privacy-0.39.json"
python3 "$project_root/scripts/package-app-archive.py" "$app" "$artifact_dir/Plain-Craft-Launcher-Refactoring-0.39-macOS-arm64.zip" --report "$artifact_dir/release-archive-0.39.json"
echo "Built: $app"
