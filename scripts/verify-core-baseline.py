"""Fail packaging when business sources differ from the selected/latest core.

Host version/sequence are packaging metadata. Explicit content integration files
pin both baseline and current hashes; other core/native/vendor files must match.
"""
import argparse
import hashlib
import json
import pathlib
import subprocess
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
PATHS = ['src/Pcl.Core', 'src/Pcl.Host', 'src/Pcl.Application', 'src/Pcl.Domain',
         'src/Pcl.Ports', 'src/Pcl.Legacy', 'src/Pcl.Contract', 'src/SharedProtocol',
         'src/Pcl.Native', 'vendor']
# These module landing pages are developer documentation, not runtime inputs.
# Keep this exact: vendor files, nested Markdown and all other assets stay pinned.
DEVELOPER_DOCS = {f'src/{name}/README.md' for name in (
    'Pcl.Core', 'Pcl.Host', 'Pcl.Application', 'Pcl.Domain', 'Pcl.Ports', 'Pcl.Contract')}


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args])


def normalize(name, content):
    # Git's Windows checkout may convert text to CRLF. Do not mistake that
    # checkout policy for a different core; binary assets remain byte-exact.
    if b'\0' not in content:
        try:
            content.decode('utf-8')
            content = content.replace(b'\r\n', b'\n')
        except UnicodeDecodeError:
            pass
    if name == 'src/Pcl.Host/Pcl.Host.csproj':
        root = ET.fromstring(content)
        for tag in ('Version', 'HostUpdateSequence'):
            for element in root.iter(tag):
                element.text = 'PACKAGING_METADATA'
        return ET.tostring(root)
    return content


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--upstream-ref', help='Freshly fetched upstream, e.g. origin/main')
    args = parser.parse_args()
    release = json.loads((ROOT / 'release.json').read_text())
    baseline = release['coreCommit']
    git('merge-base', '--is-ancestor', baseline, 'HEAD')
    baseline_release = json.loads(git('show', baseline + ':release.json'))
    if release['coreVersion'] != baseline_release.get('coreVersion', baseline_release['version']):
        raise SystemExit('CORE_VERSION_METADATA_MISMATCH')
    policy = release.get('coreReleasePolicy', 'legacy-packaging')
    if policy == 'unified':
        # A new unified release pins a committed core with the same declared version.
        # The byte comparison below still rejects every unpinned business change.
        if release['version'] != release['coreVersion'] or baseline_release['version'] != release['coreVersion']:
            raise SystemExit('UNIFIED_CORE_VERSION_MISMATCH')
        if release.get('contentIntegration'):
            raise SystemExit('UNIFIED_CORE_OVERLAYS_NOT_ALLOWED')
    elif policy == 'legacy-packaging':
        if release['version'].split('.')[:2] != release['coreVersion'].split('.')[:2] or not release['version'].endswith('.1'):
            raise SystemExit('PACKAGING_VERSION_MUST_FOLLOW_CORE_AS_0_XX_1')
    else:
        raise SystemExit('UNKNOWN_CORE_RELEASE_POLICY')
    references = [baseline]
    if args.upstream_ref:
        includes_upstream = subprocess.run(
            ['git', '-C', str(ROOT), 'merge-base', '--is-ancestor', args.upstream_ref, baseline],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        # A unified core release intentionally changes business code over main, but
        # must include main's history. A newer/divergent upstream still gets compared.
        if policy != 'unified' or not includes_upstream:
            references.append(args.upstream_ref)
    # Data loading requires a small, reviewable adapter delta over the latest
    # business baseline. Pin both sides, so this is not a broad directory exemption.
    overlays = release.get('contentIntegration', {})
    allowed = {'src/Pcl.Core/McmodCatalog.cs', 'src/Pcl.Core/Adapters/Search/UnifiedResourceSearch.cs', 'src/Pcl.Core/Adapters/Search/BundledProjectIdentity.cs',
               'src/Pcl.Core/Adapters/Content/ContentPack.cs', 'src/Pcl.Core/Adapters/Content/ActiveContentIndex.cs',
               'src/Pcl.Host/ContentUpdates.cs', 'src/Pcl.Host/Program.cs', 'src/Pcl.Host/HostBuildInfo.cs'}
    if set(overlays) - allowed:
        raise SystemExit('UNSUPPORTED_CORE_OVERLAY')
    def digest(name, content):
        return hashlib.sha256(normalize(name, content)).hexdigest()
    current = set(git('ls-files', '-z', '--', *PATHS).decode().split('\0')) - {''} - DEVELOPER_DOCS
    if set(overlays) - current:
        raise SystemExit('UNTRACKED_CONTENT_INTEGRATION')
    untracked = set(git('ls-files', '--others', '--exclude-standard', '-z', '--', *PATHS).decode().split('\0')) - {''} - DEVELOPER_DOCS
    if untracked:
        raise SystemExit('UNTRACKED_CORE_CONTENT: commit core work before packaging')
    for reference in references:
        tracked = set(git('ls-tree', '-r', '--name-only', '-z', reference, '--', *PATHS).decode().split('\0')) - {''} - DEVELOPER_DOCS
        changed = []
        for name in sorted(current | tracked):
            path = ROOT / name
            if name in overlays:
                pin = overlays[name]
                original = digest(name, git('show', reference + ':' + name)) if name in tracked else None
                actual = digest(name, path.read_bytes()) if path.is_file() else None
                upstream_matches = original == pin['baselineSha256'] or (reference != baseline and original == pin['sha256'])
                if not upstream_matches or actual != pin['sha256'] or name not in current:
                    changed.append(name)
            elif name not in current or name not in tracked or not path.is_file():
                changed.append(name)
            elif normalize(name, path.read_bytes()) != normalize(name, git('show', reference + ':' + name)):
                changed.append(name)
        if changed:
            raise SystemExit('CORE_BASELINE_MISMATCH ' + reference + ': ' + ', '.join(changed))
    print('CORE_BASELINE_VERIFIED ' + baseline)
