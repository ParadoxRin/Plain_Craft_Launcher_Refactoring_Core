"""Collect notices from exact published dependency manifests, never the entire cache."""
import json
import pathlib
import shutil
import sys
import xml.etree.ElementTree as ET


def collect_dependencies(root, manifests, legal):
    cache = root / '.cache/nuget'
    dependencies = {}
    for manifest in sorted(manifests):
        for identity, library in json.loads(manifest.read_text())['libraries'].items():
            if library['type'] not in ('package', 'runtimepack'):
                continue
            package_id, version = identity.rsplit('/', 1)
            if library['type'] == 'runtimepack':
                package_id = package_id.removeprefix('runtimepack.')
            if any(x in ('', '.', '..') or '/' in x or '\\' in x for x in (package_id, version)):
                raise ValueError('INVALID_DEPENDENCY_IDENTITY')
            dependencies[(package_id.lower(), version.lower())] = (package_id, version)
    if not manifests:
        raise ValueError('PUBLISHED_DEPENDENCY_MANIFEST_REQUIRED')
    inventory = []
    for key, (package_id, version) in sorted(dependencies.items()):
        package = cache.joinpath(*key)
        nuspecs = sorted(package.glob('*.nuspec'))
        if len(nuspecs) != 1:
            raise ValueError('EXACT_DEPENDENCY_METADATA_MISSING: ' + package_id + '/' + version)
        metadata = ET.parse(nuspecs[0]).getroot()
        license_node = next((n for n in metadata.iter() if n.tag.split('}')[-1] == 'license'), None)
        documents = {p for p in package.rglob('*') if p.is_file() and
                     ('license' in p.name.lower() or 'notice' in p.name.lower()) and
                     p.suffix.lower() in ('.txt', '.md', '', '.html')}
        if license_node is not None and license_node.get('type') == 'file':
            declared = package / (license_node.text or '')
            if not declared.resolve().is_relative_to(package.resolve()) or not declared.is_file():
                raise ValueError('DECLARED_LICENSE_FILE_MISSING_OR_UNSAFE: ' + package_id)
            documents.add(declared)
        if not documents and license_node is None:
            raise ValueError('DEPENDENCY_LICENSE_MISSING: ' + package_id)
        target = legal / 'Dependencies' / key[0] / key[1]
        target.mkdir(parents=True, exist_ok=True)
        # Preserve authors, copyright, exact SPDX expression and upstream provenance.
        shutil.copy2(nuspecs[0], target / nuspecs[0].name)
        for source in sorted(documents):
            if not source.resolve().is_relative_to(package.resolve()):
                raise ValueError('UNSAFE_LICENSE_PATH')
            destination = target / source.relative_to(package)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
        inventory.append({'id': package_id, 'version': version,
                          'license': license_node.text if license_node is not None else 'bundled documents'})
    (legal / 'dependency-inventory.json').write_text(json.dumps(inventory, indent=2) + '\n')


def prepare(root, directory):
    legal = directory / 'Legal'
    legal.mkdir(exist_ok=False)
    notices = [root / name for name in ('PCL-LICENCE.txt', 'LICENSE.md', 'LICENSE-MIT.txt')]
    notices += sorted((root / 'docs').glob('*-NOTICE.txt'))
    notices += sorted((root / 'docs').glob('*-LICENSE.txt'))
    notices += [root / 'docs/THIRD-PARTY-NOTICES.md']
    for path in notices:
        shutil.copy2(path, legal / path.name)
    if (directory / 'Pcl.Core.dll').exists():
        shutil.copy2(root / 'vendor/MeloongCore/LICENSE.txt', legal / 'MeloongCore-LICENSE.txt')
    collect_dependencies(root, list(directory.rglob('*.deps.json')), legal)
    shutil.copy2(root / 'release.json', directory / 'release.json')


if __name__ == '__main__':
    for argument in sys.argv[2:]:
        prepare(pathlib.Path(sys.argv[1]), pathlib.Path(argument))
