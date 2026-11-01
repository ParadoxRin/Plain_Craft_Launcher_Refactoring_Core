#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${DOTNET:-$root/.tooling/dotnet/dotnet}"
output="${1:?Fresh absolute SDK output required}"
case "$output" in /*) ;; *) exit 2;; esac
if [ -e "$output" ]; then echo 'Fresh SDK output required' >&2; exit 2; fi
version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdkVersion"])' "$root/release.json")"
export DOTNET_CLI_HOME="$root/.cache/dotnet-home"
mkdir -p "$output"
for project in Pcl.Contract Pcl.Client; do
  "$sdk" pack "$root/src/$project/$project.csproj" -c Release -o "$output" \
    -p:Version="$version" -p:PackageVersion="$version" -p:NuGetAudit=false \
    -p:DirectoryBuildTargetsPath="$root/sdk/Pack.targets" -p:UseSharedCompilation=false \
    -p:PathMap="$root=/src/PCLCS" -p:DebugType=None -p:DebugSymbols=false -m:1 -nr:false
done
python3 - "$output" "$version" <<'PY'
import hashlib,json,pathlib,sys,zipfile,xml.etree.ElementTree as ET
root=pathlib.Path(sys.argv[1]); packages=[]
for name in ('Pcl.Contract','Pcl.Client'):
    path=root/(name+'.'+sys.argv[2]+'.nupkg')
    with zipfile.ZipFile(path) as archive:
        dlls=[n for n in archive.namelist() if n.endswith('.dll')]
        if dlls!=['lib/net10.0/'+name+'.dll']: raise SystemExit('SDK_PAYLOAD_BOUNDARY_FAILED')
        spec=ET.fromstring(archive.read(name+'.nuspec'))
        deps=[(e.attrib['id'],e.attrib.get('version')) for e in spec.iter() if e.tag.endswith('}dependency')]
        if [x[0] for x in deps] != ([] if name=='Pcl.Contract' else ['Pcl.Contract']): raise SystemExit('SDK_DEPENDENCY_BOUNDARY_FAILED')
    packages.append({'id':name,'file':path.name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'dependencies':deps})
(root/'sdk-manifest.json').write_text(json.dumps({'version':sys.argv[2],'protocol':1,'packages':packages},indent=2)+'\n')
PY
