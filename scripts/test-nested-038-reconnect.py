"""Verify a packaged Host retains structured dependency failures across independent sessions."""
import argparse
import json
from pathlib import Path
import queue
import subprocess
import threading
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('app', type=Path)
parser.add_argument('data', type=Path)
parser.add_argument('report', type=Path)
args = parser.parse_args()
operation_id = json.loads((args.report.parent / 'nested.json').read_text())['operationId']
if 'verification-0.38' not in str(args.data) or args.data.name != 'diagnostic-ui-data':
    raise SystemExit('ISOLATED_DIAGNOSTIC_ROOT_REQUIRED')


def snapshot():
    process = subprocess.Popen([str(args.app.resolve() / 'Contents/MacOS/host/Pcl.Host'), '--data-root', str(args.data.resolve())],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    rows = queue.Queue()
    def read():
        for line in process.stdout:
            rows.put(json.loads(line))
    threading.Thread(target=read, daemon=True).start()
    def rpc(method, params=None):
        identifier = uuid.uuid4().hex
        process.stdin.write(json.dumps({'protocolVersion': 1, 'id': identifier, 'method': method, 'params': params or {}}) + '\n')
        process.stdin.flush()
        while True:
            row = rows.get(timeout=30)
            if row.get('id') == identifier:
                if 'error' in row:
                    raise RuntimeError(row['error']['code'])
                return row['result']
    try:
        rpc('host.hello', {'minimumVersion': 1, 'maximumVersion': 1})
        row = rpc('page.get', {'operationId': operation_id})
        failures = [row] if row.get('state') == 'failed' and row.get('dependencyFailure') else []
        if not failures:
            raise RuntimeError('No persisted dependency failure')
        result = {r['operationId']: r['dependencyFailure'] for r in failures}
        if str(args.data.resolve()) in json.dumps(result):
            raise RuntimeError('Host path in public diagnostic')
        rpc('host.shutdown')
        process.wait(timeout=20)
        if process.returncode != 0:
            raise RuntimeError('Host shutdown failed')
        return result
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=15)

first = snapshot()
second = snapshot()
if first != second:
    raise RuntimeError('Diagnostic changed across Host restarts')
args.report.parent.mkdir(parents=True, exist_ok=True)
args.report.write_text(json.dumps({'status': 'passed', 'checks': ['packaged-host-reloads-structured-receipts', 'second-host-retains-identical-diagnostics', 'no-host-root-in-public-diagnostics', 'both-hosts-exit-zero'], 'failureCount': len(first)}, indent=2) + '\n')
