"""Exercise the packaged Host and game in an explicitly isolated 0.37 test root.

Copies of test saves are prepared by the caller. No account secrets or personal worlds
are read. Raw logs remain local; the report contains only normalized observations.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('app', type=Path)
parser.add_argument('data', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--detached', action='store_true')
args = parser.parse_args()
data = args.data.resolve()
output = args.output.resolve()
if 'verification-0.37' not in str(data) or data.name != 'live-data':
    raise SystemExit('ISOLATED_037_ROOT_REQUIRED')
output.mkdir(parents=True, exist_ok=True)
profile = data / 'instances/PCL037-Pack'
world = profile / 'saves/PCL037_World'
if not (world / 'level.dat').is_file():
    raise SystemExit('Prepare a disposable test world copy first')

def hashes():
    return {str(p.relative_to(world)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(world.rglob('*')) if p.is_file() and p.name != 'session.lock'}

def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False

report = {'detached': args.detached, 'state': 'starting', 'checks': []}
def save():
    (output / 'result.json').write_text(json.dumps(report, indent=2) + '\n')

def check(value, label):
    if not value:
        raise RuntimeError(label)
    report['checks'].append(label)
    save()

host_log = (output / 'host.log').open('w')
process = subprocess.Popen([str(args.app.resolve() / 'Contents/MacOS/host/Pcl.Host'), '--data-root', str(data)],
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=host_log, text=True, bufsize=1)
responses = queue.Queue()
def read():
    for line in process.stdout:
        responses.put(json.loads(line))
    responses.put(None)
threading.Thread(target=read, daemon=True).start()

def rpc(method, params=None):
    request_id = uuid.uuid4().hex
    process.stdin.write(json.dumps({'protocolVersion': 1, 'id': request_id, 'method': method, 'params': params or {}}) + '\n')
    process.stdin.flush()
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        row = responses.get(timeout=max(.1, deadline - time.monotonic()))
        if row is None:
            raise RuntimeError('Host exited unexpectedly')
        if row.get('id') == request_id:
            if 'error' in row:
                raise RuntimeError(method + ': ' + row['error']['code'])
            return row['result']
    raise TimeoutError(method)

def page(method, payload=None, instance=None):
    operation = rpc('page.begin', {'idempotencyKey': uuid.uuid4().hex, 'method': method,
                                  'instanceId': instance, 'payloadJson': json.dumps(payload or {})})['operationId']
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        row = rpc('page.get', {'operationId': operation})
        if row['state'] == 'succeeded':
            return row['result']
        if row['state'] in ('failed', 'cancelled', 'interrupted'):
            raise RuntimeError(method + ': ' + str(row.get('errorCode')))
        time.sleep(.1)
    raise TimeoutError(method)

owned_game = None
try:
    rpc('host.hello', {'minimumVersion': 1, 'maximumVersion': 1})
    account = page('workspace.account.addOffline', {'name': 'PCL037_Test'})
    account_id = account['Id']
    page('workspace.instances.saveSettings', {'value': {'QuickPlayWorld': 'PCL037_World', 'GameWindowTitle': 'PCL 0.37 - Controlled Pack World', 'MaximumMemoryMb': 2048},
         'fields': ['QuickPlayWorld', 'GameWindowTitle', 'MaximumMemoryMb']}, 'PCL037-Pack')
    guide = rpc('game.guidance', {'accountId': account_id, 'culture': 'zh-CN'})
    check(guide['showSponsor'] and guide['showPurchase'], 'packaged-host-retains-launch-guidance')
    before = hashes()
    (output / 'world-before.json').write_text(json.dumps(before, indent=2) + '\n')
    command = {'idempotencyKey': uuid.uuid4().hex, 'accountId': account_id, 'instanceId': 'PCL037-Pack',
               'detached': args.detached, 'guidance': {'culture': 'zh-CN', 'sponsorRead': True, 'purchaseRead': True, 'demo': False}}
    acceptance = rpc('game.launch', command)
    check(rpc('game.launch', command)['operationId'] == acceptance['operationId'], 'launch-retry-reuses-operation')
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        row = rpc('operation.get', {'operationId': acceptance['operationId']})
        if row['state'] == 'succeeded':
            owned_game = row['result']
            break
        if row['state'] in ('failed', 'interrupted', 'cancelled'):
            raise RuntimeError('Launch failed: ' + str(row.get('errorCode')))
        time.sleep(.25)
    if owned_game is None:
        raise TimeoutError('Game start')
    details = rpc('game.details', {'gameProcessId': owned_game['gameProcessId']})
    log_path = Path(details['logFile'])
    # Keep a local-only process receipt for targeted cleanup, never publish it with source.
    (output / 'local-process.json').write_text(json.dumps({'pid': owned_game['pid'], 'log': str(log_path)}, indent=2))
    deadline = time.monotonic() + 150
    while time.monotonic() < deadline:
        log = log_path.read_text(errors='replace') if log_path.exists() else ''
        if 'joined the game' in log or '加入了游戏' in log:
            break
        if not alive(owned_game['pid']):
            raise RuntimeError('Game exited before entering test world')
        time.sleep(.5)
    check('joined the game' in log or '加入了游戏' in log, 'actual-game-joined-test-world')
    check('continuity 3.0.0+1.21' in log and 'fabric-api 0.116.17+1.21.1' in log and 'pcl037_legacy 1.0.0' in log,
          'actual-loader-loaded-public-mods-and-schema-zero-fixture')
    if args.detached:
        rpc('host.shutdown')
        process.wait(timeout=20)
        check(process.returncode == 0, 'host-clean-shutdown')
        time.sleep(5)
        check(alive(owned_game['pid']), 'game-survives-host-exit')
    report['state'] = 'waiting-for-user-save-and-exit'
    save()
    print('WORLD_READY', flush=True)
    deadline = time.monotonic() + 900
    exit_code = None
    while time.monotonic() < deadline:
        if args.detached:
            if not alive(owned_game['pid']):
                break
        else:
            state = rpc('game.get', {'gameProcessId': owned_game['gameProcessId']})
            if state['state'] == 'exited':
                exit_code = state['exitCode']
                break
        time.sleep(1)
    else:
        raise TimeoutError('User gameplay budget expired')
    log = log_path.read_text(errors='replace')
    check('Saving chunks for level' in log, 'actual-world-save-recorded')
    check('All dimensions are saved' in log or 'All chunks are saved' in log, 'actual-world-save-completed')
    if not args.detached:
        check(exit_code == 0, 'managed-game-exit-zero')
    after = hashes()
    (output / 'world-after.json').write_text(json.dumps(after, indent=2) + '\n')
    check(before != after, 'test-world-files-updated')
    report['exitCode'] = exit_code
    report['state'] = 'completed'
    save()
    print('WORLD_COMPLETED', flush=True)
except Exception as ex:
    report['state'] = 'failed'
    report['errorType'] = type(ex).__name__
    save()
    raise
finally:
    if process.poll() is None:
        if owned_game:
            try:
                rpc('game.stop', {'gameProcessId': owned_game['gameProcessId']})
            except Exception:
                pass
        try:
            rpc('host.shutdown')
            process.wait(timeout=20)
        except Exception:
            process.terminate()
    host_log.close()
