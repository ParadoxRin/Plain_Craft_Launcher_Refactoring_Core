#!/bin/bash
# Real macOS GUI acceptance against disposable signed packages; never a production signing key.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$root/.tooling/dotnet/dotnet}"
work="${1:?Supply a fresh absolute output directory}"
case "$work" in /*) ;; *) echo 'Absolute output directory required' >&2; exit 2;; esac
if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ] || [ -e "$work" ]; then
  echo 'Native Apple Silicon Mac and fresh output required' >&2; exit 2
fi
mkdir -p "$work"
keydir="$(mktemp -d "${TMPDIR:-/tmp}/pcl-client-update.XXXXXXXX")"
trap 'rm -rf "$keydir"' EXIT
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$keydir/private.pem"
openssl pkey -in "$keydir/private.pem" -pubout -out "$keydir/public.pem"
python3 - "$keydir/public.pem" "$work/fixture-policy.json" <<'PY'
import json,sys
with open(sys.argv[2],'w') as output:
    json.dump({'FeedUrl':'https://updates.example/client/preview/osx-arm64.json',
               'PublicKeyPem':open(sys.argv[1]).read(),'Channel':'preview'},output,indent=2)
PY
unset PCL_HOST_UPDATE_POLICY PCL_CONTENT_POLICY PCL_CONTENT_ROOT PCL_HOST_EXECUTABLE PCL_HOST_ASSEMBLY
export PCL_CONTENT_ROOT="$work/content-state"
export DOTNET_CLI_HOME="$root/.cache/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$root"
python3 scripts/verify-core-baseline.py
"$sdk" build tests/Pcl.ClientUpdate.Tests/Pcl.ClientUpdate.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
"$sdk" build tools/Pcl.UpdatePack/Pcl.UpdatePack.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
bash scripts/build-native.sh
PCL_RELEASE_OUTPUT="$work/build" PCL_CLIENT_UPDATE_POLICY="$work/fixture-policy.json" DOTNET="$sdk" bash scripts/build-release.sh
version="$(python3 -c 'import json;print(json.load(open("release.json"))["version"])')"
app="$work/build/Plain Craft Launcher Refactoring $version.app"
python3 scripts/test-client-update-fixtures.py --app "$app" --output "$work/fixtures" --key "$keydir/private.pem" \
  --dotnet "$sdk" --signer tools/Pcl.UpdatePack/bin/Release/net10.0/Pcl.UpdatePack.dll
"$sdk" tests/Pcl.ClientUpdate.Tests/bin/Release/net10.0/Pcl.ClientUpdate.Tests.dll "$app" "$work/fixtures" "$keydir/private.pem" "$work/run"
