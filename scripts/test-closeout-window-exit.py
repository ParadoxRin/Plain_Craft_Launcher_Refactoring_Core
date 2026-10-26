"""Observe a launcher that is expected to close itself, from outside its process.

Only the newly recorded game in the explicitly marked acceptance root may be
stopped. Reports omit process IDs, account metadata and raw command lines.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('mode', choices=['close', 'hide-close'])
parser.add_argument('root', type=Path)
parser.add_argument('fixtures', type=Path)
parser.add_argument('report', type=Path)
args = parser.parse_args()
root = args.root.resolve()
report = args.report.resolve()
if not (root / '.closeout-fifth').is_file():
    raise SystemExit('ISOLATED_ROOT_REQUIRED')
receipts = root / 'launch-operations-v1'
before = set(receipts.glob('*.json'))
logs_before = set((root / 'evidence').glob('*game-*.log'))
repo = Path.cwd()
env = dict(os.environ)
if env.get('PCL_DESKTOP_TEST_EXE'):
    app_command = [env['PCL_DESKTOP_TEST_EXE']]
    # Force the packaged launcher to locate its own Host and runtime.
    for key in ('PCL_HOST_ASSEMBLY', 'PCL_DOTNET', 'DOTNET_ROOT'):
        env.pop(key, None)
else:
    env.update(PCL_HOST_ASSEMBLY=str(repo / 'src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll'),
               PCL_DOTNET=str(repo / '.tooling/dotnet/dotnet'))
    app_command = [env['PCL_DOTNET'], str(repo / 'src/Pcl.Desktop/bin/Debug/net10.0/PCL-MacPort.dll')]
inner = report.with_suffix('.inner.json')
checks = []
result = {'status': 'running', 'mode': args.mode, 'checks': checks}
owned_pid = None

def check(value, name):
    if not value:
        raise RuntimeError(name)
    checks.append(name)

def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False

def stop_owned():
    if owned_pid is None or not alive(owned_pid):
        return
    command = subprocess.check_output(['ps', '-p', str(owned_pid), '-o', 'args='], text=True)
    if str(root) not in command:
        raise RuntimeError('Process identity changed; refusing cleanup')
    os.kill(owned_pid, signal.SIGTERM)
    until = time.monotonic() + 15
    while alive(owned_pid) and time.monotonic() < until:
        time.sleep(.1)
    check(not alive(owned_pid), 'owned-test-game-cleanup-completed')

try:
    with report.with_suffix('.log').open('w') as log:
        app = subprocess.Popen(app_command + [
            '--data-root', str(root), '--closeout-fifth',
            '--acceptance-report', str(inner), '--acceptance-files', str(args.fixtures.resolve()),
            '--acceptance-mode', args.mode], env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            check(app.wait(timeout=180) == 0, 'desktop-exits-with-code-zero')
        finally:
            if app.poll() is None:
                app.terminate()
                app.wait(timeout=15)
    new = set(receipts.glob('*.json')) - before
    check(len(new) == 1, 'one-game-launch-per-window-lifecycle')
    launch = json.loads(next(iter(new)).read_text())['Snapshot']
    check(launch['State'] == 'succeeded', 'host-launch-receipt-succeeded')
    owned_pid = launch['Result']['Pid']
    if args.mode == 'close':
        check(launch['Result']['Detached'], 'close-policy-uses-detached-game')
        time.sleep(3)
        check(alive(owned_pid), 'game-survives-desktop-exit')
        until = time.monotonic() + 90
        rendered = False
        while time.monotonic() < until and alive(owned_pid):
            rendered = any('gui.png-atlas' in p.read_text(errors='replace')
                           for p in set((root / 'evidence').glob('*game-*.log')) - logs_before)
            if rendered:
                break
            time.sleep(.3)
        check(rendered, 'detached-game-renderer-ready-after-desktop-exit')
    else:
        progress = Path(str(inner) + '.progress').read_text()
        check('JAVA-015-actual-window-state-hide-close' in progress and
              'JAVA-015-real-game-renderer-ready-hide-close' in progress,
              'launcher-hidden-until-real-game-exit')
        check(not alive(owned_pid), 'game-stopped-before-desktop-exit')
    # Inspect only whether this root's Host remains; do not emit commands or secrets.
    processes = subprocess.check_output(['ps', '-axo', 'args='], text=True)
    check(not any('Pcl.Host.dll' in line and '--data-root ' + str(root) in line
                  for line in processes.splitlines()), 'desktop-exit-releases-owned-host')
    stop_owned()
    result['status'] = 'passed'
except Exception as error:
    result.update(status='failed', errorType=type(error).__name__, failedCheck=str(error))
    raise
finally:
    stop_owned()
    report.write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result))
