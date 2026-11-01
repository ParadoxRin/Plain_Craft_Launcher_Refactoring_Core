"""Fail a generated app/ZIP release on known private-path, credential, or debug-symbol patterns.

This is a bounded gate, not a claim that arbitrary unknown secrets can be detected.
Only entry names and finding categories are printed, never the matched contents.
"""
import argparse
import json
from pathlib import Path
import re
import os
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('artifact', type=Path)
parser.add_argument('--report', type=Path)
args = parser.parse_args()
patterns = {
    'developer_home': str(Path.home()).encode(),
    'build_workspace': str(Path(__file__).resolve().parent.parent).encode(),
    'cloud_project_identifier': re.compile(rb'g-p-[0-9a-f]{24,}'),
    'github_token': re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})'),
    'jwt_signature': re.compile(rb'eyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}'),
    'private_key': re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
}
findings = []
upstream_metadata = []
count = 0

def home_scan_bytes(name, content):
    # GitHub's generic runner home coincides with upstream embedded build paths.
    # Classify only specific upstream prefixes in known dependencies; never skip
    # a whole binary or suppress credentials/current-workspace matches.
    if os.environ.get('GITHUB_ACTIONS') != 'true' or str(Path.home()) != '/Users/runner':
        return content
    library = Path(name).name
    prefix = None
    if library in ('libcoreclr.dylib', 'libclrjit.dylib', 'libmscordaccore.dylib', 'libmscordbi.dylib'):
        prefix = b'/Users/runner/work/1/s/src/runtime/'
    elif library == 'libAvaloniaNative.dylib':
        prefix = b'/Users/runner/work/Avalonia/Avalonia/native/Avalonia.Native/'
    if prefix and prefix in content:
        upstream_metadata.append({'file': name, 'kind': 'upstream_ci_build_path', 'count': content.count(prefix)})
        return content.replace(prefix, b'/upstream-build/')
    return content

def scan(name, content):
    global count
    count += 1
    if name.lower().endswith('.pdb'):
        findings.append({'file': name, 'kind': 'debug_symbols'})
    if Path(name).name in ('accounts.json', 'launch-settings.json', 'oauth-secrets.json'):
        findings.append({'file': name, 'kind': 'runtime_state'})
    for kind, pattern in patterns.items():
        inspected = home_scan_bytes(name, content) if kind == 'developer_home' else content
        found = pattern in inspected if isinstance(pattern, bytes) else pattern.search(inspected)
        if found:
            findings.append({'file': name, 'kind': kind})

if args.artifact.is_dir():
    for file in sorted(args.artifact.rglob('*')):
        if file.is_file():
            scan(str(file.relative_to(args.artifact)), file.read_bytes())
else:
    with zipfile.ZipFile(args.artifact) as archive:
        for entry in archive.infolist():
            if not entry.is_dir():
                scan(entry.filename, archive.read(entry))
report = {'filesScanned': count, 'findings': findings, 'upstreamBuildMetadata': upstream_metadata,
          'scope': 'current developer home, cloud project IDs, debug symbols, runtime state, credential patterns; not exhaustive'}
if args.report:
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
raise SystemExit(1 if findings else 0)
