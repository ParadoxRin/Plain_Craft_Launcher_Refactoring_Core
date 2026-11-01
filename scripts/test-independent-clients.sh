#!/bin/bash
# Build and run an SDK-only client against a source-independent backend.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$root/.tooling/dotnet/dotnet}"
work="${1:?Fresh absolute output required}"
case "$work" in /*) ;; *) exit 2;; esac
if [ -e "$work" ]; then echo 'Fresh output required' >&2; exit 2; fi
mkdir -p "$work"
export DOTNET_CLI_HOME="$root/.cache/dotnet-home"
cd "$root"
python3 scripts/check-module-boundaries.py --report "$work/module-boundaries.json"
python3 scripts/test-module-boundaries.py
DOTNET="$sdk" bash scripts/build-client-sdk.sh "$work/sdk"
python3 scripts/export-core-workspace.py --output "$work/core"
python3 scripts/export-client-workspace.py --output "$work/clients" --sdk-feed "$work/sdk"
"$sdk" build "$work/core/PCLCS.Core.slnx" -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
"$sdk" build "$work/clients/Clients.slnx" -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:UsedAvaloniaProducts=
"$sdk" build "$work/clients/tests/Pcl.MinimalClient.Tests/Pcl.MinimalClient.Tests.csproj" -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
"$sdk" "$work/clients/tests/Pcl.MinimalClient.Tests/bin/Release/net10.0/Pcl.MinimalClient.Tests.dll" \
  "$sdk" "$work/core/src/Pcl.Host/bin/Release/net10.0/Pcl.Host.dll" "$work/contract-data" "$work/contract.json" contract
"$sdk" "$work/clients/src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll" host-status \
  --dotnet "$sdk" --host-assembly "$work/core/src/Pcl.Host/bin/Release/net10.0/Pcl.Host.dll" --data-root "$work/cli-data" > "$work/cli-hello.json"
