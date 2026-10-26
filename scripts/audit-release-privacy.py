"""Fail a generated app/ZIP release on known private-path, credential, or debug-symbol patterns.

This is a bounded gate, not a claim that arbitrary unknown secrets can be detected.
Only entry names and finding categories are printed, never the matched contents.
"""
import argparse
import json
from pathlib import Path
import re
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('artifact', type=Path)
parser.add_argument('--report', type=Path)
args = parser.parse_args()
patterns = {
    'developer_home': str(Path.home()).encode(),
    'cloud_project_identifier': re.compile(rb'g-p-[0-9a-f]{24,}'),
    'github_token': re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})'),
    'jwt_signature': re.compile(rb'eyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}'),
    'private_key': re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
}
findings = []
count = 0

def scan(name, content):
    global count
    count += 1
    if name.lower().endswith('.pdb'):
        findings.append({'file': name, 'kind': 'debug_symbols'})
    if Path(name).name in ('accounts.json', 'launch-settings.json', 'oauth-secrets.json'):
        findings.append({'file': name, 'kind': 'runtime_state'})
    for kind, pattern in patterns.items():
        found = pattern in content if isinstance(pattern, bytes) else pattern.search(content)
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
report = {'filesScanned': count, 'findings': findings,
          'scope': 'current developer home, cloud project IDs, debug symbols, runtime state, credential patterns; not exhaustive'}
if args.report:
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
raise SystemExit(1 if findings else 0)
