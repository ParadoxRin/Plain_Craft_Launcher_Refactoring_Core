"""Package separate publish directories. The release index is not an update trust root."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import stat
import tempfile
import zipfile


def files(directory):
    found, identities = [], set()
    for path in sorted(directory.rglob('*')):
        if path.is_symlink():
            raise ValueError('SYMLINK_NOT_SUPPORTED')
        if path.is_dir():
            continue
        if not path.is_file():
            raise ValueError('SPECIAL_FILE_NOT_SUPPORTED')
        relative = path.relative_to(directory).as_posix()
        if relative.casefold() in identities:
            raise ValueError('CASE_COLLISION')
        identities.add(relative.casefold())
        found.append((relative, path))
    if not found:
        raise ValueError('EMPTY_PUBLISH_DIRECTORY')
    return found


def archive(directory, destination):
    with zipfile.ZipFile(destination, 'x', zipfile.ZIP_DEFLATED) as zipped:
        for relative, path in files(directory):
            info = zipfile.ZipInfo(relative, (2020, 1, 1, 0, 0, 0))
            info.create_system = 3
            mode = 0o755 if path.stat().st_mode & stat.S_IXUSR else 0o644
            info.external_attr = (stat.S_IFREG | mode) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with path.open('rb') as source, zipped.open(info, 'w', force_zip64=True) as target:
                shutil.copyfileobj(source, target)
    with destination.open('rb') as stream:
        hasher = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            hasher.update(block)
        digest = hasher.hexdigest()
    return {'file': destination.name, 'size': destination.stat().st_size, 'sha256': digest}


def package(client, host, probe, release, output, base_url=None):
    client, host, output = (p.resolve() for p in (client, host, output))
    for source in (client, host):
        if output == source or output in source.parents or source in output.parents:
            raise ValueError('OUTPUT_MUST_BE_SEPARATE')
        files(source)
    if client == host or client in host.parents or host in client.parents:
        raise ValueError('PUBLISH_DIRECTORIES_MUST_BE_SEPARATE')
    if (client / 'host').exists():
        raise ValueError('CLIENT_CONTAINS_BUNDLED_HOST')
    forbidden = {'pcl.host.dll', 'pcl.core.dll', 'pcl.application.dll', 'pcl.domain.dll',
                 'pcl.ports.dll', 'pcl.legacy.dll', 'pclcs.dll', 'meloongcore.dll'}
    if any(path.name.casefold() in forbidden or path.name.casefold().startswith(('pcl.host', 'architectureguard'))
           for _, path in files(client)):
        raise ValueError('CLIENT_CONTAINS_CORE')
    build = probe['build']
    rid, version = build['runtime'], release['version']
    if not re.fullmatch(r'(osx|linux|win)-(arm64|x64)', rid):
        raise ValueError('UNSUPPORTED_RUNTIME')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version) or build['version'] != version:
        raise ValueError('RELEASE_HOST_VERSION_MISMATCH')
    if build['sequence'] != release.get('hostSequence', build['sequence']):
        raise ValueError('RELEASE_HOST_SEQUENCE_MISMATCH')
    # Reference Desktop currently supports protocol/epoch 1. Do not derive its
    # requirements from a potentially incompatible candidate Host.
    required_capabilities = ['desktop.contract-only.v1']
    if probe['protocolVersion'] != 1 or build['dataEpoch'] != 1 or not set(required_capabilities) <= set(probe['capabilities']):
        raise ValueError('INCOMPATIBLE_HOST')
    entrypoint = 'Pcl.Host.exe' if rid.startswith('win-') else 'Pcl.Host'
    desktop = 'PCL-MacPort.exe' if rid.startswith('win-') else 'PCL-MacPort'
    for source, required in ((client, (desktop, 'Pcl.Client.dll', 'Pcl.Contract.dll', 'Legal')),
                             (host, (entrypoint, 'Pcl.Host.dll', 'Pcl.Contract.dll', 'Legal'))):
        if any(not (source / name).exists() for name in required):
            raise ValueError('INCOMPLETE_PUBLISH_DIRECTORY')
    if base_url:
        from urllib.parse import urlsplit
        parsed = urlsplit(base_url)
        if parsed.scheme != 'https' or not parsed.netloc or parsed.query or parsed.fragment or parsed.username:
            raise ValueError('HTTPS_BASE_URL_REQUIRED')
    if output.exists():
        raise ValueError('OUTPUT_ALREADY_EXISTS')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.components-', dir=output.parent) as temp:
        stage = pathlib.Path(temp)
        prefix, parts = f'pcl-{version}-{rid}', {}
        for name, directory in (('client', client), ('host', host)):
            parts[name] = archive(directory, stage / f'{prefix}-{name}.zip')
        full = stage / 'full'
        shutil.copytree(client, full)
        shutil.copytree(host, full / 'host')
        parts['full'] = archive(full, stage / f'{prefix}-full.zip')
        shutil.rmtree(full)
        if base_url:
            for item in parts.values():
                item['url'] = base_url.rstrip('/') + '/' + item['file']
        parts['client'].update(version=version, clientSequence=release.get('build', 0), bundledHost=False,
                               hostCompatibility={'protocol': 1, 'dataEpoch': 1,
                                                  'requiredCapabilities': required_capabilities})
        parts['host'].update(build=build, protocol=probe['protocolVersion'],
                             capabilities=probe['capabilities'], entryPoint=entrypoint)
        parts['full'].update(clientVersion=version, hostBuild=build)
        manifest = {'format': 1, 'version': version, 'channel': release['channel'], 'runtime': rid,
                    'packages': parts, 'trust': 'inventory-only; use a signed Host feed for automatic updates'}
        if 'coreCommit' in release:
            manifest['core'] = {'version': release['coreVersion'], 'commit': release['coreCommit'],
                                'contentIntegration': release.get('contentIntegration', {})}
        (stage / 'release-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        stage.rename(output)
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('client', 'host', 'probe', 'release', 'output'):
        parser.add_argument('--' + name, required=True, type=pathlib.Path)
    parser.add_argument('--base-url')
    args = parser.parse_args()
    print(json.dumps(package(args.client, args.host, json.loads(args.probe.read_text()),
                             json.loads(args.release.read_text()), args.output, args.base_url), indent=2))
