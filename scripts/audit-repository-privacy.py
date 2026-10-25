#!/usr/bin/env python3
"""Scan published Git history without printing matched private values.

This is a bounded pattern audit, not proof that all possible secrets are absent.
License contacts and example/fixture values still require human classification.
"""
import argparse
import io
import json
import re
import subprocess
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--refs', nargs='+', default=['main', 'refs/tags/v0.36.1'])
parser.add_argument('--forbidden-file', help='Local JSON array of exact private strings; never commit this file')
args = parser.parse_args()


def git(*parts):
    return subprocess.check_output(['git', *parts])


patterns = {
    'personal_home': re.compile(rb'/' + rb'Users/[^/\s\x00<>]+/'),
    'cloud_project_identifier': re.compile(rb'g-p-[0-9a-f]{24,}'),
    'github_token': re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})'),
    'jwt_signature': re.compile(rb'eyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}'),
    'private_key': re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
}
forbidden = []
if args.forbidden_file:
    with open(args.forbidden_file) as source:
        forbidden = [value.encode() for value in json.load(source) if value]
findings = []


def scan(data, location):
    for label, pattern in patterns.items():
        if pattern.search(data):
            findings.append({'location': location, 'kind': label})
    if any(value in data for value in forbidden):
        findings.append({'location': location, 'kind': 'forbidden_private_value'})


# Inspect every unique reachable object; old branches/tags require explicit inclusion.
objects = git('rev-list', '--objects', *args.refs).decode().splitlines()
blob_count = 0
for row in objects:
    oid, _, path = row.partition(' ')
    kind = git('cat-file', '-t', oid).strip()
    if kind == b'blob':
        blob_count += 1
        data = git('cat-file', 'blob', oid)
        scan(data, path or oid)
        if path == 'src/Pcl.Core/Authentication/microsoft-application.json':
            if json.loads(data).get('clientId') != '':
                findings.append({'location': path, 'kind': 'nonempty_tracked_oauth_identity'})
        if data.startswith(b'PK'):
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                for entry in archive.infolist():
                    scan(archive.read(entry), path + '!' + entry.filename)
    elif kind in (b'commit', b'tag'):
        data = git('cat-file', kind.decode(), oid)
        scan(data, oid)
        for email in re.findall(rb'^(?:author|committer|tagger) .* <([^>]+)>', data, re.M):
            if not email.lower().endswith(b'@users.noreply.github.com'):
                findings.append({'location': oid, 'kind': 'non_noreply_git_email'})
print(json.dumps({'refs': args.refs, 'reachableObjects': len(objects), 'blobsScanned': blob_count,
                  'findings': findings, 'scope': 'patterns and optional supplied strings; not exhaustive'},
                 ensure_ascii=False, indent=2))
raise SystemExit(1 if findings else 0)
