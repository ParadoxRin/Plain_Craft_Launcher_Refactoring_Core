"""Archive a Host publication with explicit runtime deployment requirements."""
import importlib.util
import json
import pathlib
import sys

spec = importlib.util.spec_from_file_location('components', pathlib.Path(__file__).with_name('package-components.py'))
components = importlib.util.module_from_spec(spec)
spec.loader.exec_module(components)


def package(work, release_path, rid, deployment='self-contained'):
    if deployment not in ('self-contained', 'framework-dependent'):
        raise ValueError('UNKNOWN_DEPLOYMENT')
    release = json.loads(release_path.read_text())
    probe = json.loads((work / 'host-probe.json').read_text())
    if (probe['build']['runtime'], probe['build']['version'], probe['build']['sequence']) != (rid, release['version'], release['hostSequence']):
        raise ValueError('HOST_RELEASE_IDENTITY_MISMATCH')
    host = work / 'host'
    entry = 'Pcl.Host.exe' if rid.startswith('win-') else 'Pcl.Host'
    for name in (entry, 'Pcl.Host.dll', 'Pcl.Host.deps.json', 'Pcl.Host.runtimeconfig.json', 'Legal'):
        if not (host / name).exists():
            raise ValueError('HOST_PUBLICATION_INCOMPLETE: ' + name)
    config = json.loads((host / 'Pcl.Host.runtimeconfig.json').read_text())['runtimeOptions']
    requirements = []
    if deployment == 'framework-dependent':
        framework = config.get('framework')
        if not framework or framework.get('name') != 'Microsoft.NETCore.App' or not framework.get('version', '').startswith('10.') or config.get('includedFrameworks'):
            raise ValueError('FRAMEWORK_DEPENDENT_CONFIG_REQUIRED')
        libraries = json.loads((host / 'Pcl.Host.deps.json').read_text())['libraries']
        if any(v.get('type') == 'runtimepack' for v in libraries.values()) or any((host / n).exists() for n in (
                'System.Private.CoreLib.dll', 'libcoreclr.dylib', 'libcoreclr.so', 'coreclr.dll',
                'libhostfxr.dylib', 'libhostfxr.so', 'hostfxr.dll')):
            raise ValueError('FRAMEWORK_DEPENDENT_CONTAINS_RUNTIME')
        requirements = [dict(framework)]
        (host / 'README.txt').write_text(
            'PCLCS developer Host - framework-dependent\n\n'
            f"Version: {release['version']} / {rid}\n"
            f"Requires: {framework['name']} {framework['version']} or a compatible .NET 10 patch, matching the package architecture.\n"
            'Install the .NET 10 Runtime or SDK separately. This archive contains no .NET runtime.\n'
            'Check installed runtimes: dotnet --list-runtimes\n'
            'Probe: dotnet Pcl.Host.dll --update-probe\n'
            'Connect through Pcl.Client / Pcl.Contract of the matching version.\n'
            'Desktop: set PCL_HOST_ASSEMBLY to the absolute Pcl.Host.dll path and PCL_DOTNET to the absolute dotnet executable.\n'
            'The native Pcl.Host entry point also works with a discoverable matching runtime.\n'
            'For a private runtime, configure DOTNET_ROOT (and any existing architecture-specific DOTNET_ROOT variable) to its installation directory.\n'
            'Manual developer deployment only; do not insert this package into self-contained automatic update feeds.\n', encoding='utf-8')
    elif not config.get('includedFrameworks') or config.get('framework'):
        raise ValueError('SELF_CONTAINED_CONFIG_REQUIRED')
    output = work / 'packages'
    output.mkdir()
    suffix = '-framework-dependent' if deployment == 'framework-dependent' else ''
    item = components.archive(host, output / f"pcl-{release['version']}-{rid}-host{suffix}.zip")
    item.update(build=probe['build'], protocol=probe['protocolVersion'], capabilities=probe['capabilities'], entryPoint=entry,
                deployment=deployment, runtimeRequirements=requirements)
    manifest = {'format': 1, 'version': release['version'], 'channel': release['channel'], 'runtime': rid,
                'core': {'version': release['coreVersion'], 'commit': release['coreCommit'],
                         'contentIntegration': release.get('contentIntegration', {})},
                'packages': {'host': item}, 'trust': 'inventory-only; not an automatic update feed'}
    (output / 'release-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    (output / 'SHA256SUMS').write_text(f"{item['sha256']}  {item['file']}\n")
    return manifest


if __name__ == '__main__':
    package(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3],
            sys.argv[4] if len(sys.argv) > 4 else 'self-contained')
    print('HOST_PACKAGE_CREATED')
