#!/bin/bash
# Build signed local candidates only; never upload assets or change online feeds.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$root/.tooling/dotnet/dotnet}"
work="${1:?Fresh absolute output directory required}"
: "${PCL_PUBLISH_KEY:?External signing key required}"
: "${PCL_PUBLISH_BASE_URL:?Immutable HTTPS asset directory required}"
: "${PCL_HOST_FEED_URL:?Stable Host feed required}"
: "${PCL_CLIENT_FEED_URL:?Standalone client feed required}"
: "${PCL_APP_FEED_URL:?Mac app feed required}"
: "${PCL_CONTENT_FEED_URL:?Signed content envelope URL required}"
case "$work" in /*) ;; *) exit 2;; esac
if [ -e "$work" ]; then echo 'Fresh output required' >&2; exit 2; fi
mkdir -p "$work"
cd "$root"
export DOTNET_CLI_HOME="$root/.cache/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
python3 scripts/verify-core-baseline.py
bash scripts/build-native.sh
for project in tools/Pcl.UpdatePack tools/Pcl.ContentPack tests/Pcl.LogProtection.Tests; do
  "$sdk" build "$project" -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:RuntimeIdentifiers=osx-arm64 -p:NuGetAudit=false
done
sequence="$(python3 -c 'import json; print(json.load(open("release.json"))["contentSequence"])')"
python3 scripts/build-content-release.py --dotnet "$sdk" --tool tools/Pcl.ContentPack/bin/Release/net10.0/Pcl.ContentPack.dll \
  --key "$PCL_PUBLISH_KEY" --sequence "$sequence" --feed-url "$PCL_CONTENT_FEED_URL" --output "$work/content"
PCL_CONTENT_POLICY="$work/content/content-update-policy.json" PCL_COMPONENT_OUTPUT="$work/components" DOTNET="$sdk" bash scripts/build-components.sh osx-arm64
python3 scripts/sign-component-release.py --work "$work/components" --release release.json --key "$PCL_PUBLISH_KEY" \
  --dotnet "$sdk" --tool tools/Pcl.UpdatePack/bin/Release/net10.0/Pcl.UpdatePack.dll --output "$work/release" \
  --base-url "$PCL_PUBLISH_BASE_URL" --feed-url "$PCL_HOST_FEED_URL" --client-feed-url "$PCL_CLIENT_FEED_URL" \
  --app-feed-url "$PCL_APP_FEED_URL" --content-release "$work/content" --content-tool tools/Pcl.ContentPack/bin/Release/net10.0/Pcl.ContentPack.dll
PCL_CONTENT_POLICY="$work/content/content-update-policy.json" PCL_HOST_UPDATE_POLICY="$work/release/host-update-policy.json" \
  PCL_CLIENT_UPDATE_POLICY="$work/release/app-update-policy.json" PCL_RELEASE_OUTPUT="$work/installer" DOTNET="$sdk" bash scripts/build-release.sh
version="$(python3 -c 'import json; print(json.load(open("release.json"))["version"])')"
python3 scripts/add-component-installer.py "$work/release" "$work/installer/Plain-Craft-Launcher-Refactoring-$version-macOS-arm64.zip" \
  "$PCL_PUBLISH_KEY" "$sdk" tools/Pcl.UpdatePack/bin/Release/net10.0/Pcl.UpdatePack.dll
python3 scripts/verify-unified-release.py --release "$work/release" --key "$PCL_PUBLISH_KEY" --dotnet "$sdk" --tool tools/Pcl.UpdatePack/bin/Release/net10.0/Pcl.UpdatePack.dll
python3 scripts/verify-component-delivery.py "$work/release" tests/Pcl.LogProtection.Tests/bin/Release/net10.0/Pcl.LogProtection.Tests.dll "$sdk" "$work/verified"
echo UNIFIED_SIGNED_LOCAL_CANDIDATE_VERIFIED
