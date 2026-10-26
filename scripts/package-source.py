#!/usr/bin/env python3
"""Export an exact committed source snapshot; never traverse runtime data or .git."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--ref', default='HEAD')
parser.add_argument('--output', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
# Resolve the revision once so every exported file belongs to the same commit.
commit = subprocess.check_output(['git', 'rev-parse', '--verify', args.ref + '^{commit}'], cwd=root, text=True).strip()
release = json.loads(subprocess.check_output(['git', 'show', commit + ':release.json'], cwd=root))
version = release['version']
target = args.output or root / 'artifacts' / ('Plain-Craft-Launcher-Refactoring-' + version + '-source.zip')
target.parent.mkdir(parents=True, exist_ok=True)
subprocess.run(['git', 'archive', '--format=zip', '--prefix=Plain-Craft-Launcher-Refactoring-' + version + '/',
                '--output=' + str(target.resolve()), commit], cwd=root, check=True)
with zipfile.ZipFile(target) as archive:
    if archive.testzip() is not None:
        raise SystemExit('Source ZIP integrity check failed')
print(json.dumps({'sourceCommit': commit, 'version': version, 'sha256': hashlib.sha256(target.read_bytes()).hexdigest(),
                  'containsGitHistory': False, 'trackedFilesOnly': True}))
