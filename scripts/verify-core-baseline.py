"""Fail packaging when business sources differ from the selected/latest core.

Host version/sequence are packaging metadata. All other tracked core content,
including native helpers and upstream libraries, must match the core baseline.
"""
import argparse
import json
import pathlib
import subprocess
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
PATHS = ['src/Pcl.Core', 'src/Pcl.Host', 'src/Pcl.Application', 'src/Pcl.Domain',
         'src/Pcl.Ports', 'src/Pcl.Legacy', 'src/Pcl.Contract', 'src/SharedProtocol',
         'src/Pcl.Native', 'vendor']


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
    if release['version'].split('.')[:2] != release['coreVersion'].split('.')[:2] or not release['version'].endswith('.1'):
        raise SystemExit('PACKAGING_VERSION_MUST_FOLLOW_CORE_AS_0_XX_1')
    references = [baseline] + ([args.upstream_ref] if args.upstream_ref else [])
    current = set(git('ls-files', '-z', '--', *PATHS).decode().split('\0')) - {''}
    untracked = git('ls-files', '--others', '--exclude-standard', '-z', '--', *PATHS)
    if untracked:
        raise SystemExit('UNTRACKED_CORE_CONTENT: commit core work before packaging')
    for reference in references:
        tracked = set(git('ls-tree', '-r', '--name-only', '-z', reference, '--', *PATHS).decode().split('\0')) - {''}
        changed = []
        for name in sorted(current | tracked):
            path = ROOT / name
            if name not in current or name not in tracked or not path.is_file():
                changed.append(name)
            elif normalize(name, path.read_bytes()) != normalize(name, git('show', reference + ':' + name)):
                changed.append(name)
        if changed:
            raise SystemExit('CORE_BASELINE_MISMATCH ' + reference + ': ' + ', '.join(changed))
    print('CORE_BASELINE_VERIFIED ' + baseline)
