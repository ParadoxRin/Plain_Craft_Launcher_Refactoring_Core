#!/bin/bash
# Local native Mac connectivity closeout. Inputs are an independently built Host,
# a real Java 21 executable and the pinned EasyTier distribution archive.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$root/.tooling/dotnet/dotnet}"
if [ "$#" -ne 4 ]; then echo 'Usage: test-package-connectivity.sh <absolute-host> <absolute-java21> <absolute-easytier.zip> <fresh-absolute-output>' >&2; exit 2; fi
for value in "$@"; do case "$value" in /*) ;; *) echo 'Absolute paths required' >&2; exit 2;; esac; done
host="$1"; java="$2"; archive="$3"; work="$4"
if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ] || [ -e "$work" ]; then echo 'Native Apple Silicon and fresh output required' >&2; exit 2; fi
python3 - "$archive" "$work" <<'PY'
import hashlib,pathlib,shutil,sys
source=pathlib.Path(sys.argv[1]);work=pathlib.Path(sys.argv[2])
# Same publisher archive pin as EasyTierService 2.6.4; never execute unverified input.
if hashlib.sha256(source.read_bytes()).hexdigest()!='4be1882d1aa36d31c1d6ba0596f2cf8a097e371f8da124212324b2e0f8df7e4b':raise SystemExit('EASYTIER_ARCHIVE_PIN_MISMATCH')
for name in ('link','link-guest'):
 target=work/name/'runtime/easytier-2.6.4/easytier.zip'
 target.parent.mkdir(parents=True);shutil.copy2(source,target)
PY
export DOTNET_CLI_HOME="$root/.cache/dotnet-home"
export PCL_HOST_TEST_EXE="$host"
unset PCL_HOST_UPDATE_POLICY PCL_CONTENT_POLICY
export PCL_CONTENT_ROOT="$work/content-state"
cd "$root"
for project in Pcl.Workspaces.Tests Pcl.UpdateIntegration.Tests; do
 "$sdk" build "tests/$project/$project.csproj" -c Release -m:1 -nr:false -p:RuntimeIdentifiers=osx-arm64 -p:NuGetAudit=false -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false
done
"$sdk" tests/Pcl.UpdateIntegration.Tests/bin/Release/net10.0/Pcl.UpdateIntegration.Tests.dll "$work/integration" "$host"
"$sdk" tests/Pcl.Workspaces.Tests/bin/Release/net10.0/Pcl.Workspaces.Tests.dll --external-runtime "$work/java-data" "$work/java.json" "$java"
"$sdk" tests/Pcl.Workspaces.Tests/bin/Release/net10.0/Pcl.Workspaces.Tests.dll --closeout-link "$work/link" "$work/link.json"
