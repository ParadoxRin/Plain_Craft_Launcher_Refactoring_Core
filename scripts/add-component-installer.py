"""Attach a verified Mac installer to an existing index and re-sign the index."""
import hashlib
import json
import pathlib
import plistlib
import shutil
import subprocess
import sys
import tempfile
import zipfile

release, installer, key, dotnet, tool = map(pathlib.Path, sys.argv[1:])
release = release.resolve()
index = release / 'release-manifest.json'
envelope = release / 'release-manifest.signed.json'
command = [str(dotnet.resolve()), str(tool.resolve())]
subprocess.run([*command, '--verify-document', str(envelope), str(key.resolve()), str(index)], check=True)
destination = release / installer.name
if destination.exists():
    raise SystemExit('INSTALLER_ASSET_ALREADY_EXISTS')
manifest = json.loads(index.read_text())
if manifest['runtime'] != 'osx-arm64':
    raise SystemExit('MAC_INSTALLER_PLATFORM_MISMATCH')
with zipfile.ZipFile(installer) as archive:
    plists = [name for name in archive.namelist() if name.endswith('.app/Contents/Info.plist')
              and not name.startswith('__MACOSX/')]
    if len(plists) != 1 or plistlib.loads(archive.read(plists[0]))['CFBundleShortVersionString'] != manifest['version']:
        raise SystemExit('INSTALLER_VERSION_MISMATCH')
    policy_path = plists[0].replace('Info.plist', 'MacOS/host-update-policy.json')
    if json.loads(archive.read(policy_path)) != json.loads((release / 'host-update-policy.json').read_text()):
        raise SystemExit('INSTALLER_PUBLISHER_POLICY_MISMATCH')
shutil.copy2(installer, destination)
digest = hashlib.sha256()
with destination.open('rb') as stream:
    for block in iter(lambda: stream.read(1024 * 1024), b''):
        digest.update(block)
base_url = manifest['packages']['host']['url'].rsplit('/', 1)[0]
manifest['packages']['installer'] = {'file': destination.name, 'kind': 'macos-app',
    'size': destination.stat().st_size, 'sha256': digest.hexdigest(), 'url': base_url + '/' + destination.name}
with tempfile.TemporaryDirectory(prefix='.installer-index-', dir=release) as temporary:
    stage = pathlib.Path(temporary)
    candidate = stage / index.name
    signed = stage / envelope.name
    candidate.write_text(json.dumps(manifest, indent=2) + '\n')
    subprocess.run([*command, '--sign-document', str(candidate), str(key.resolve()), str(signed)], check=True)
    subprocess.run([*command, '--verify-document', str(signed), str(key.resolve()), str(candidate)], check=True)
    candidate.replace(index)
    signed.replace(envelope)
with (release / 'SHA256SUMS').open('w') as sums:
    for path in sorted(release.iterdir()):
        if path.is_file() and path.name != 'SHA256SUMS':
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(block)
            sums.write(digest.hexdigest() + '  ' + path.name + '\n')
print('INSTALLER_INDEX_SIGNED')
