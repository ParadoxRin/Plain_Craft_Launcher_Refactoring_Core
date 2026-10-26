"""Download a public resource pack through the real Host into a new isolated root.

The metadata input is saved directly from Modrinth. Host independently fetches the
version again; the test compares the installed file with the supplied public hash.
This probe does not claim game compatibility for that downloaded pack.
"""
import argparse
import hashlib
import json
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid

parser = argparse.ArgumentParser(description=__doc__)
for name in ('dotnet', 'host', 'metadata', 'output'):
    parser.add_argument(name, type=Path)
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
data = output / 'download-data'
if data.exists():
    raise SystemExit('Use a fresh output directory to prove an uncached download')
metadata = json.loads(args.metadata.read_text())
file = next((f for f in metadata['files'] if f['primary']), metadata['files'][0])
report = {'status': 'running', 'checks': []}
responses = queue.Queue()
with (output / 'host.log').open('w') as log:
    process = subprocess.Popen(
        [str(args.dotnet.resolve()), str(args.host.resolve()), '--data-root', str(data)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, bufsize=1)

    # Drain protocol output independently so notifications cannot block the Host.
    def read():
        for line in process.stdout:
            responses.put(json.loads(line))
        responses.put(None)
    threading.Thread(target=read, daemon=True).start()

    def rpc(method, params=None):
        request_id = uuid.uuid4().hex
        process.stdin.write(json.dumps({'protocolVersion': 1, 'id': request_id,
                                       'method': method, 'params': params or {}}) + '\n')
        process.stdin.flush()
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            row = responses.get(timeout=max(.1, deadline - time.monotonic()))
            if row is None:
                raise RuntimeError('Host exited before responding')
            if row.get('id') == request_id:
                if 'error' in row:
                    raise RuntimeError(method + ': ' + row['error']['code'])
                return row['result']
        raise TimeoutError(method)

    def page(method, payload):
        operation = rpc('page.begin', {'idempotencyKey': uuid.uuid4().hex,
                        'method': method, 'instanceId': '1.21.1',
                        'payloadJson': json.dumps(payload)})['operationId']
        deadline = time.monotonic() + 180
        while time.monotonic() < deadline:
            row = rpc('page.get', {'operationId': operation})
            if row['state'] == 'succeeded':
                return row['result']
            if row['state'] in ('failed', 'cancelled', 'interrupted'):
                raise RuntimeError(method + ': ' + str(row.get('errorCode')))
            time.sleep(.2)
        raise TimeoutError(method)

    def check(condition, label):
        if not condition:
            raise RuntimeError(label)
        report['checks'].append(label)

    try:
        rpc('host.hello', {'minimumVersion': 1, 'maximumVersion': 1})
        page('workspace.browser.install', {'kind': 2, 'releaseId': metadata['id']})
        installed = page('workspace.browser.list', {})
        item = next(i for i in installed if i['VersionId'] == metadata['id'])
        check(item['ProjectId'] == metadata['project_id'], 'host-records-public-resource-release')
        target = data / 'minecraft' / 'resourcepacks' / item['FileName']
        content = target.read_bytes()
        check(len(content) == file['size'], 'download-matches-public-size')
        check(hashlib.sha1(content).hexdigest() == file['hashes']['sha1'],
              'download-matches-public-sha1')
        page('workspace.browser.activate', {'projectId': metadata['project_id']})
        options = (data / 'minecraft/options.txt').read_text()
        check(item['FileName'] in options, 'downloaded-pack-activation-persists-options')
        rpc('host.shutdown')
        process.wait(timeout=20)
        check(process.returncode == 0, 'host-clean-exit')
        report.update(status='passed', projectId=metadata['project_id'],
                      versionId=metadata['id'], filename=item['FileName'],
                      bytes=len(content), sha1=file['hashes']['sha1'],
                      gameTested=False)
    except Exception as error:
        report.update(status='failed', errorType=type(error).__name__)
        raise
    finally:
        # Persist normalized evidence only; never include machine paths or account data.
        (output / 'result.json').write_text(json.dumps(report, indent=2) + '\n')
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=15)
print(json.dumps(report))
