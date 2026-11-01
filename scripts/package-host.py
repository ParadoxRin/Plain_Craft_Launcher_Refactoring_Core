"""Archive a native Host publication independently from the desktop build."""
import importlib.util
import json
import pathlib
import sys

spec = importlib.util.spec_from_file_location('components', pathlib.Path(__file__).with_name('package-components.py'))
components = importlib.util.module_from_spec(spec)
spec.loader.exec_module(components)
work, release_path = map(pathlib.Path, sys.argv[1:3])
rid = sys.argv[3]
release = json.loads(release_path.read_text())
probe = json.loads((work / 'host-probe.json').read_text())
if (probe['build']['runtime'], probe['build']['version'], probe['build']['sequence']) != (rid, release['version'], release['hostSequence']):
    raise SystemExit('HOST_RELEASE_IDENTITY_MISMATCH')
output = work / 'packages'
output.mkdir()
entry = 'Pcl.Host.exe' if rid.startswith('win-') else 'Pcl.Host'
if not (work / 'host' / entry).is_file():
    raise SystemExit('HOST_ENTRYPOINT_MISSING')
item = components.archive(work / 'host', output / f"pcl-{release['version']}-{rid}-host.zip")
item.update(build=probe['build'], protocol=probe['protocolVersion'], capabilities=probe['capabilities'], entryPoint=entry)
manifest = {'format': 1, 'version': release['version'], 'channel': release['channel'], 'runtime': rid,
            'core': {'version': release['coreVersion'], 'commit': release['coreCommit']},
            'packages': {'host': item}, 'trust': 'inventory-only; use a signed Host feed for automatic updates'}
(output / 'release-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('HOST_PACKAGE_CREATED')
