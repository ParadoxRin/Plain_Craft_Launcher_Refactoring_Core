"""Exercise current Host Java selection and real game rendering in isolated roots.

Cases are supplied as version:Java-major. Games are stopped at their menus;
this probe is not a world-save or visual acceptance test.
"""
import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('root', type=Path)
parser.add_argument('report', type=Path)
parser.add_argument('cases', nargs='+')
parser.add_argument('--jvm-argument', default='')
parser.add_argument('--hold-seconds', type=int, default=10)
args = parser.parse_args()
root = args.root.resolve()
report = args.report.resolve()
if not (root / '.closeout-fifth').is_file():
    raise SystemExit('ISOLATED_ROOT_REQUIRED')
repo = Path.cwd()
responses = queue.Queue()
rows = []
host_log = report.with_suffix('.log').open('w')
# A release test must execute the packaged self-contained Host, not a Debug DLL.
host_command = [os.environ['PCL_HOST_TEST_EXE']] if os.environ.get('PCL_HOST_TEST_EXE') else [
    str(repo / '.tooling/dotnet/dotnet'), str(repo / 'src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll')]
host = subprocess.Popen(host_command + ['--data-root', str(root)],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=host_log, text=True, bufsize=1)

def read():
    for line in host.stdout:
        responses.put(json.loads(line))
    responses.put(None)
threading.Thread(target=read, daemon=True).start()

def rpc(method, params=None):
    request_id = uuid.uuid4().hex
    host.stdin.write(json.dumps({'protocolVersion': 1, 'id': request_id,
                                'method': method, 'params': params or {}}) + '\n')
    host.stdin.flush()
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        row = responses.get(timeout=max(.1, deadline - time.monotonic()))
        if row is None:
            raise RuntimeError('Host exited')
        if row.get('id') == request_id:
            if 'error' in row:
                raise RuntimeError(method + ': ' + row['error']['code'])
            return row['result']
    raise TimeoutError(method)

def wait_operation(method, operation):
    until = time.monotonic() + 180
    while time.monotonic() < until:
        row = rpc(method, {'operationId': operation})
        if row['state'] == 'succeeded':
            return row['result']
        if row['state'] in ('failed', 'cancelled', 'interrupted'):
            raise RuntimeError(str(row.get('errorCode')))
        time.sleep(.2)
    raise TimeoutError(method)

def page(method, payload=None, instance=None):
    accepted = rpc('page.begin', {'idempotencyKey': uuid.uuid4().hex,
        'method': method, 'instanceId': instance, 'payloadJson': json.dumps(payload or {})})
    return wait_operation('page.get', accepted['operationId'])

owned = None
try:
    rpc('host.hello', {'minimumVersion': 1, 'maximumVersion': 1})
    account = page('workspace.account.addOffline', {'name': 'PCL_Acceptance'})
    for case in args.cases:
        version, expected = case.split(':')
        row = {'version': version, 'expectedJava': int(expected), 'status': 'running',
               'worldPlay': 'not_tested', 'termination': 'owned-process-stop-at-menu'}
        rows.append(row)
        page('workspace.instances.saveSettings', {'value': {'QuickPlayWorld': '',
             'MaximumMemoryMb': 1536, 'JvmArguments': args.jvm_argument},
             'fields': ['QuickPlayWorld', 'MaximumMemoryMb', 'JvmArguments']}, version)
        command = {'idempotencyKey': uuid.uuid4().hex, 'accountId': account['Id'],
            'instanceId': version, 'detached': False,
            'guidance': {'culture': 'zh-CN', 'sponsorRead': True, 'purchaseRead': True, 'demo': False}}
        accepted = rpc('game.launch', command)
        owned = wait_operation('operation.get', accepted['operationId'])
        details = rpc('game.details', {'gameProcessId': owned['gameProcessId']})
        log = Path(details['logFile'])
        until = time.monotonic() + 120
        rendered = False
        while time.monotonic() < until:
            content = log.read_text(errors='replace') if log.exists() else ''
            rendered = ('gui.png-atlas' in content or 'textures/atlas/blocks.png' in content
                        or ('Created:' in content and 'textures-atlas' in content))
            if rendered:
                break
            if rpc('game.get', {'gameProcessId': owned['gameProcessId']})['state'] == 'exited':
                raise RuntimeError('Game exited before renderer initialization')
            time.sleep(.5)
        if not rendered:
            raise RuntimeError('Renderer initialization timeout')
        # Stay at the actual menu after initialization; an atlas line alone can precede a native crash.
        until = time.monotonic() + args.hold_seconds
        while time.monotonic() < until:
            if rpc('game.get', {'gameProcessId': owned['gameProcessId']})['state'] == 'exited':
                raise RuntimeError('Game crashed during menu observation')
            time.sleep(.5)
        preflight = json.loads((root / 'evidence' / (version + '-preflight.json')).read_text())
        if preflight['MajorVersion'] != int(expected) or preflight['Architecture'] != 'aarch64':
            raise RuntimeError('Java selection mismatch')
        row.update(rendered=True, menuObservationSeconds=args.hold_seconds, observedJava=preflight['MajorVersion'],
                   architecture=preflight['Architecture'])
        rpc('game.stop', {'gameProcessId': owned['gameProcessId']})
        until = time.monotonic() + 20
        while time.monotonic() < until:
            state = rpc('game.get', {'gameProcessId': owned['gameProcessId']})
            if state['state'] == 'exited':
                break
            time.sleep(.1)
        if state['state'] != 'exited':
            raise RuntimeError('Owned game did not stop')
        row.update(status='passed', exitCode=state['exitCode'])
        owned = None
        report.write_text(json.dumps({'cases': rows}, indent=2) + '\n')
        print(version, 'passed', flush=True)
except Exception as error:
    if rows:
        rows[-1].update(status='failed', error=str(error))
    raise
finally:
    if host.poll() is None:
        if owned:
            rpc('game.stop', {'gameProcessId': owned['gameProcessId']})
        rpc('host.shutdown')
        host.wait(timeout=20)
    host_log.close()
    report.write_text(json.dumps({'status': 'passed' if rows and all(r['status'] == 'passed' for r in rows) else 'failed',
                                 'cases': rows}, indent=2) + '\n')
