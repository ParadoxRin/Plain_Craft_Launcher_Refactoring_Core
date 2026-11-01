"""Export the verified backend source closure without any client implementation."""
import argparse, hashlib, json, pathlib, shutil, subprocess
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--output', type=pathlib.Path, required=True)
a = p.parse_args()
root = pathlib.Path(__file__).resolve().parent.parent
out = a.output.resolve()
if out.exists():
    raise SystemExit('FRESH_CORE_WORKSPACE_REQUIRED')
subprocess.run(['python3', str(root/'scripts/verify-core-baseline.py')], check=True)
scopes = ['src/'+name for name in ('Pcl.Host','Pcl.Application','Pcl.Domain','Pcl.Ports',
          'Pcl.Core','Pcl.Legacy','Pcl.Contract','SharedProtocol','Pcl.Native')]
scopes += ['vendor', 'Directory.Build.props', 'global.json', 'release.json',
           'LICENSE', 'LICENSE.md', 'LICENSE-MIT.txt', 'PCL-LICENCE.txt',
           'docs/THIRD-PARTY-NOTICES.md', 'scripts/build-native.sh']
paths = subprocess.check_output(['git','-C',str(root),'ls-files','-z','--',*scopes]).decode().split('\0')
manifest = {}
for name in filter(None, paths):
    source = root/name
    if source.is_file():
        target = out/name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        manifest[name] = hashlib.sha256(source.read_bytes()).hexdigest()
# This entry point is generated so exports also work before the local changes are committed.
shutil.copy2(root/'PCLCS.Core.slnx', out/'PCLCS.Core.slnx')
for source in (root/'docs').glob('*-*.txt'):
    if source.name.endswith(('-NOTICE.txt','-LICENSE.txt')):
        shutil.copy2(source, out/'docs'/source.name)
(out/'source-manifest.json').write_text(json.dumps({'coreCommit':json.loads((root/'release.json').read_text())['coreCommit'],'sha256':manifest},indent=2)+'\n')
(out/'README.md').write_text('PCLCS independent backend workspace\n\nBuild PCLCS.Core.slnx with .NET 10 and the native C toolchain.\nNo Desktop, Client transport SDK, CLI, or Git metadata is included.\nContract remains the shared protocol source; changes require coordinated SDK compatibility tests.\nRun scripts/build-native.sh before producing Mac game-runtime distributions.\n')
print('INDEPENDENT_CORE_WORKSPACE_EXPORTED', out)
