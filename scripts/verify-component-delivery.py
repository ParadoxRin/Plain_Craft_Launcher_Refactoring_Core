"""Verify actual archives, run the shipped Host and test the shipped Core.

Usage: python3 scripts/verify-component-delivery.py <packages> <log-test.dll> <dotnet> <fresh-output>
The regression harness is copied into the extracted Host after hashes are checked;
its Core dependencies are taken exclusively from that Host package.
"""
import hashlib
import json
import pathlib
import shutil
import stat
import subprocess
import sys
import zipfile

packages, harness, dotnet, output = (pathlib.Path(p).resolve() for p in sys.argv[1:])
output.mkdir(parents=True, exist_ok=False)
manifest = json.loads((packages / 'release-manifest.json').read_text())
checks = []
for name, item in manifest['packages'].items():
    archive = packages / item['file']
    if archive.parent != packages:
        raise ValueError('ARCHIVE_PATH_INVALID')
    digest = hashlib.sha256()
    with archive.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    if archive.stat().st_size != item['size'] or digest.hexdigest() != item['sha256']:
        raise ValueError('ARCHIVE_INTEGRITY_FAILED:' + name)
    if item.get('kind') == 'wiki-project-index':
        checks.append('content-envelope-integrity')
        continue
    target = output / name
    target.mkdir()
    if item.get('kind') == 'macos-app':
        if sys.platform != 'darwin':
            raise ValueError('MAC_INSTALLER_VERIFICATION_REQUIRES_MACOS')
        subprocess.run(['/usr/bin/ditto', '-x', '-k', str(archive), str(target)], check=True)
        apps = list(target.glob('*.app'))
        if len(apps) != 1:
            raise ValueError('MAC_INSTALLER_BUNDLE_MISSING')
        subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(apps[0])], check=True)
        installer_probe = json.loads(subprocess.check_output(
            [str(apps[0] / 'Contents/MacOS/host/Pcl.Host'), '--update-probe'], text=True))
        if installer_probe['build'] != manifest['packages']['host']['build']:
            raise ValueError('INSTALLER_HOST_BUILD_MISMATCH')
        checks.append('installer-archive-integrity-and-signature')
        continue
    with zipfile.ZipFile(archive) as zipped:
        seen = set()
        for info in zipped.infolist():
            path = target / info.filename
            mode = info.external_attr >> 16
            if not path.resolve().is_relative_to(target) or info.filename.casefold() in seen or stat.S_ISLNK(mode):
                raise ValueError('UNSAFE_ARCHIVE_ENTRY')
            seen.add(info.filename.casefold())
            zipped.extract(info, target)
            path.chmod(mode & 0o777)
    checks.append(name + '-archive-integrity-and-extraction')
host = output / 'host'
entry = host / manifest['packages']['host']['entryPoint']
probe = json.loads(subprocess.check_output([str(entry), '--update-probe'], text=True))
if probe['build'] != manifest['packages']['host']['build']:
    raise ValueError('HOST_BUILD_MISMATCH')
checks.append('extracted-host-probe-matches-manifest')
if 'content' in manifest['packages']:
    content_tool = pathlib.Path(__file__).resolve().parent.parent / 'tools/Pcl.ContentPack/bin/Release/net10.0/Pcl.ContentPack.dll'
    for suffix in ('.dll', '.deps.json', '.runtimeconfig.json'):
        source = content_tool.with_suffix(suffix)
        shutil.copy2(source, host / source.name)
    subprocess.run([str(dotnet), str(host / content_tool.name), 'verify',
                    str(packages / manifest['packages']['content']['file']),
                    str(host / 'content-update-policy.json'), probe['build']['version']], check=True)
    checks.append('signed-content-policy-and-host-compatibility')
# Load the regression harness beside the delivered assemblies, without copying
# build-directory Pcl.Core.dll, any other business DLL, or dependency binaries.
for suffix in ('.dll', '.deps.json', '.runtimeconfig.json'):
    source = harness.with_suffix(suffix)
    shutil.copy2(source, host / source.name)
result = subprocess.run([str(dotnet), str(host / harness.name), str(output / 'log-regression')],
                        text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
print(result.stdout, end='')
if result.returncode:
    raise RuntimeError('DELIVERED_CORE_REGRESSION_FAILED')
checks.append('delivered-core-log-and-credential-preflight-regressions')
report = {'status': 'passed', 'scope': 'Actual ZIP extraction and delivered Core; synthetic subprocess fixtures',
          'hostBuild': probe['build'], 'checks': checks, 'regressions': result.stdout.splitlines()}
(output / 'delivery-result.json').write_text(json.dumps(report, indent=2) + '\n')
