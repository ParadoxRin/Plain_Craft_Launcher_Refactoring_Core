"""Opt-in live device authorization: validate navigation and resource conflicts, without signing in."""
import argparse
import json
from pathlib import Path
import selectors
import subprocess
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('host', type=Path)
p.add_argument('data_root', type=Path)
p.add_argument('report', type=Path)
a = p.parse_args()
a.data_root.mkdir(parents=True, exist_ok=False)
proc = subprocess.Popen([str(a.host.resolve()), '--data-root', str(a.data_root.resolve())],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
selector = selectors.DefaultSelector()
selector.register(proc.stdout, selectors.EVENT_READ)
sequence = 0
checks = []

class Rejected(Exception):
    pass

def call(method, params=None):
    global sequence
    sequence += 1
    request_id = str(sequence)
    proc.stdin.write((json.dumps(dict(protocolVersion=1, id=request_id, method=method, params=params or {}))+'\n').encode())
    proc.stdin.flush()
    if not selector.select(45):
        raise TimeoutError('HOST_RESPONSE_TIMEOUT')
    response = json.loads(proc.stdout.readline())
    assert response.get('id') == request_id
    if 'error' in response:
        raise Rejected(response['error']['code'])
    return response['result']

def wait(method, operation_id):
    until = time.monotonic()+30
    while time.monotonic() < until:
        row = call(method, dict(operationId=operation_id))
        if row['state'] in ('succeeded', 'failed', 'cancelled', 'interrupted'):
            return row
        time.sleep(.1)
    raise TimeoutError('OPERATION_TERMINAL_TIMEOUT')

def page(method, payload=None):
    row = call('page.begin', dict(idempotencyKey='live-'+str(sequence), method=method,
               payloadJson=json.dumps(payload or {})))
    return wait('page.get', row['operationId'])

result = dict(status='failed', checks=checks)
try:
    call('host.hello', dict(minimumVersion=1, maximumVersion=1))
    assert not call('account.snapshot')['accounts']
    login = call('account.login.begin', dict(key='background-login-live', remember=False))
    until = time.monotonic()+55
    while not login.get('prompt'):
        assert login['state'] not in ('failed', 'cancelled', 'succeeded'), login.get('errorCode')
        if time.monotonic()>until:
            raise TimeoutError('DEVICE_CHALLENGE_TIMEOUT')
        time.sleep(.1)
        login = call('account.login.get', dict(operationId=login['operationId']))
    checks.append('real-device-challenge-active-without-sign-in')
    assert page('workspace.settings.read')['state'] == 'succeeded'
    checks.append('settings-navigation-read-completes-during-login')
    assert page('workspace.settings.save', dict(changes={}))['state'] == 'succeeded'
    checks.append('unrelated-settings-write-completes-during-login')
    assert call('account.login.get', dict(operationId=login['operationId']))['state'] == 'challenge'
    checks.append('navigation-does-not-cancel-login')
    for method, params in [('account.logout', dict(accountId='missing')),
        ('page.begin', dict(idempotencyKey='conflicting-account', method='workspace.account.addOffline', payloadJson=json.dumps(dict(name='Conflict_Test'))))]:
        try:
            call(method, params)
        except Rejected as error:
            assert str(error) in ('ACCOUNT_OPERATION_BUSY', 'LOGIN_BUSY_OR_RETENTION_LIMIT', 'HOST_WRITER_ACTIVE'), str(error)
        else:
            raise AssertionError('CONFLICTING_ACCOUNT_WRITE_ACCEPTED')
    checks.append('account-resource-writes-rejected-during-login')
    call('account.login.cancel', dict(operationId=login['operationId']))
    assert wait('account.login.get', login['operationId'])['state']=='cancelled'
    assert not call('account.snapshot')['accounts']
    checks.append('cancel-terminal-without-account-side-effect')
    assert page('workspace.account.addOffline', dict(name='After_Cancel'))['state']=='succeeded'
    checks.append('account-write-resumes-after-cancel')
    call('host.shutdown')
    assert proc.wait(timeout=15)==0
    checks.append('host-normal-shutdown')
    result['status']='passed'
finally:
    if proc.poll() is None:
        proc.terminate()
        try: proc.wait(timeout=10)
        except subprocess.TimeoutExpired: proc.kill(); proc.wait()
    selector.close()
    a.report.parent.mkdir(parents=True, exist_ok=True)
    a.report.write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result))
