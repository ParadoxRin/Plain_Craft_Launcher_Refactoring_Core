"""Exercise the packaged Host device flow without signing in or persisting credentials."""
import argparse
import json
from pathlib import Path
import selectors
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('host', type=Path)
parser.add_argument('data_root', type=Path)
parser.add_argument('report', type=Path)
args = parser.parse_args()
args.data_root.mkdir(parents=True, exist_ok=False)
process = subprocess.Popen([str(args.host.resolve()), '--data-root', str(args.data_root.resolve())],
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
selector = selectors.DefaultSelector()
selector.register(process.stdout, selectors.EVENT_READ)
checks = []
sequence = 0

def call(method, params=None):
    global sequence
    sequence += 1
    request_id = str(sequence)
    process.stdin.write((json.dumps(dict(protocolVersion=1, id=request_id, method=method, params=params or {})) + '\n').encode())
    process.stdin.flush()
    if not selector.select(55):
        raise TimeoutError('host response timeout')
    response = json.loads(process.stdout.readline())
    assert response.get('id') == request_id
    if 'error' in response:
        raise RuntimeError(response['error']['code'])
    return response['result']

try:
    assert call('host.hello', dict(minimumVersion=1, maximumVersion=1))['protocolVersion'] == 1
    assert not call('account.snapshot')['accounts']
    checks.append('isolated-host-has-no-account')
    login = call('account.login.begin', dict(key='release-device-check', remember=False))
    operation_id = login['operationId']
    until = time.monotonic() + 55
    while not login.get('prompt'):
        assert login['state'] not in ('failed', 'cancelled', 'succeeded'), login.get('errorCode')
        if time.monotonic() > until:
            raise TimeoutError('device challenge timeout')
        time.sleep(0.25)
        login = call('account.login.get', dict(operationId=operation_id))
    prompt = login['prompt']
    assert prompt['userCode'] and prompt['verificationUri'].startswith('https://')
    checks.append('microsoft-accepted-embedded-client-id-and-issued-device-prompt')
    assert call('account.login.begin', dict(key='release-device-check', remember=False))['operationId'] == operation_id
    checks.append('login-admission-is-idempotent')
    # Only booleans/check names enter the report; never log the short-lived code.
    for file in args.data_root.rglob('*.json'):
        content = file.read_text()
        assert prompt['userCode'] not in content
        assert 'device_code' not in content and 'refresh_token' not in content and 'access_token' not in content
    checks.append('device-prompt-and-tokens-not-written-to-receipts')
    call('account.login.cancel', dict(operationId=operation_id))
    # Cancel confirms signal delivery, not completion; observe the owned runner's terminal state.
    until = time.monotonic() + 15
    while True:
        state = call('account.login.get', dict(operationId=operation_id))['state']
        if state == 'cancelled':
            break
        assert state not in ('failed', 'succeeded')
        if time.monotonic() > until:
            raise TimeoutError('cancellation terminal state timeout')
        time.sleep(0.1)
    assert not call('account.snapshot')['accounts']
    checks.append('cancel-observes-terminal-state-without-saving-account')
    call('host.shutdown')
    assert process.wait(timeout=15) == 0
    checks.append('packaged-host-exits-cleanly')
    result = dict(status='passed', checks=checks, scope='Real Microsoft device authorization initiation and cancellation only; no user login, entitlements, refresh, or game test.')
except Exception as error:
    result = dict(status='failed', checks=checks, errorType=type(error).__name__)
    raise
finally:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
    selector.close()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + '\n')
