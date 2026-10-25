"""Use the selected remembered Microsoft account to test installed versions; never open worlds."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import signal
import subprocess
import time

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--data-root', type=Path, default=root / 'runtime/apple-silicon-1.21.1')
parser.add_argument('--output', type=Path, default=root / 'artifacts/verification-0.17/owned-launches')
parser.add_argument('--case', action='append', help='Run only matching case labels')
parser.add_argument('--seconds', type=int, default=40)
options = parser.parse_args()
if options.seconds < 20:
    parser.error('--seconds must be at least 20')
data = options.data_root.resolve()
output = options.output.resolve()
output.mkdir(parents=True, exist_ok=True)
index = json.loads((data / 'accounts.json').read_text())
account = next((a for a in index['Accounts'] if a['Id'] == index['SelectedId'] and a['Kind'] == 1 and a['Remembered']), None)
if account is None:
    raise SystemExit('Select and remember a real Microsoft account before running this acceptance test.')
cli = [str(root / '.tooling/dotnet/dotnet'), str(root / 'src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll')]
# Record the exact launcher binaries without copying any account or credential data.
(output / 'binary-provenance.json').write_text(json.dumps({
    'startedAtUnix': time.time(),
    'sha256': {name: hashlib.sha256((Path(cli[1]).parent / name).read_bytes()).hexdigest()
               for name in ('Pcl.Cli.dll', 'Pcl.Core.dll')},
    'observationSeconds': options.seconds,
    'worldPlay': 'not_tested',
}, indent=2) + '\n')
cases = [
    ('1.16.5', 'vanilla', 'vanilla', 8),
    ('1.18.2', 'vanilla', 'vanilla', 17),
    ('1.20.1', 'fabric', 'fabric-1.20.1', 17),
    ('1.20.1', 'forge', 'forge-1.20.1-47.4.28', 17),
    ('1.20.4', 'neoforge', 'neoforge-1.20.4-20.4.253', 17),
    ('1.21.1', 'forge', 'forge-1.21.1-52.1.20', 21),
    ('1.21.1', 'neoforge', 'neoforge-1.21.1-21.1.264', 21),
    ('26.3', 'vanilla', 'vanilla', 25),
    ('1.20.1', 'quilt', 'quilt-1.20.1', 17),
    ('1.12.2', 'vanilla', 'vanilla', 8),
]


def run(label, arguments):
    """Each test owns its process group; timeout stops only that test's launcher and child game."""
    with (output / (label + '.log')).open('w') as log:
        process = subprocess.Popen(cli + arguments + ['--data-root', str(data)], cwd=root,
                                   stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            return process.wait(timeout=900)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
            return 124


rows = []
for version, loader, profile, java in cases:
    label = version + '-' + loader
    if options.case and label not in options.case:
        continue
    row = dict(version=version, loader=loader, expectedJava=java)
    # Install only missing fixtures. Existing mod instances and user worlds are never reconfigured.
    if loader == 'quilt' and not (data / 'instances' / profile / 'profile.json').exists():
        row['installExit'] = run(label + '-install', ['version-install', '--game-version', version, '--loader', 'quilt'])
    elif version == '1.12.2' and not (data / 'minecraft/versions/1.12.2/1.12.2.json').exists():
        row['installExit'] = run(label + '-install', ['install', '--game-version', version])
    if row.get('installExit', 0) == 0:
        began = time.time()
        row['launchExit'] = run(label + '-launch', ['launch', '--game-version', version, '--profile', profile,
                                                  '--account', account['Id'], '--smoke-seconds', str(options.seconds), '--isolated-evidence'])
        profile_id = version if loader == 'vanilla' else json.loads((data / 'instances' / profile / 'profile.json').read_text())['id']
        receipt = data / 'evidence' / ('launch-result-' + profile_id + '.json')
        if row['launchExit'] == 0 and receipt.exists() and receipt.stat().st_mtime >= began:
            result = json.loads(receipt.read_text())
            row['passed'] = (result['gameVersion'] == version and result['loader'] == loader
                             and result['javaMajor'] == java and result['javaArchitecture'] == 'aarch64'
                             and result['accountKind'] == 'microsoft' and result['accountLogin'] == 'passed'
                             and result['Demo'] is False and result['graphicsLogObserved'] and result['atlasCreationObserved'])
            row['observedJava'] = result['javaMajor']
            row['architecture'] = result['javaArchitecture']
            row['profile'] = profile_id
            row['worldPlay'] = 'not_tested'
            shutil.copy2(receipt, output / (label + '-launch.json'))
    row.setdefault('passed', False)
    rows.append(row)
    (output / 'matrix.json').write_text(json.dumps(rows, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(row, ensure_ascii=False), flush=True)
raise SystemExit(0 if rows and all(row['passed'] for row in rows) else 1)
