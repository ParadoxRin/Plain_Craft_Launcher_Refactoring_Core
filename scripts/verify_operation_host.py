"""验证私有 Host 的协议边界和 EOF 排空；只操作调用者提供的隔离测试目录。"""
import json
import pathlib
import subprocess
import sys

project = pathlib.Path(__file__).resolve().parents[1]
root = pathlib.Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
dotnet = project / '.tooling/dotnet/dotnet'
assembly = project / 'src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll'
results = []

def start(directory):
    return subprocess.Popen([str(dotnet), str(assembly), '--data-root', str(directory)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

def read(process):
    return json.loads(process.stdout.readline())

def send(process, request_id, method, parameters=None):
    process.stdin.write((json.dumps({'protocolVersion': 1, 'id': request_id, 'method': method, 'params': parameters or {}}) + '\n').encode())
    process.stdin.flush()
    return read(process)

process = start(root)
try:
    assert send(process, 'before', 'operation.get', {'operationId': 'unknown'})['error']['code'] == 'HANDSHAKE_REQUIRED'
    assert send(process, 'incompatible', 'host.hello', {'minimumVersion': 2, 'maximumVersion': 3})['error']['code'] == 'PROTOCOL_INCOMPATIBLE'
    assert send(process, 'hello', 'host.hello', {'minimumVersion': 1, 'maximumVersion': 1})['result']['protocolVersion'] == 1
    assert send(process, 'hello', 'host.hello', {'minimumVersion': 1, 'maximumVersion': 1})['error']['code'] == 'REQUEST_ID_REUSED_OR_LIMIT'
    results.append({'name': 'handshake-version-and-request-identity', 'status': 'passed'})
    second = start(root)
    assert read(second)['error']['code'] == 'HOST_BUSY_OR_IO_ERROR'
    second.wait(timeout=10)
    assert second.returncode == 1
    results.append({'name': 'second-host-denied-by-os-lease', 'status': 'passed'})
    cli = project / 'src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll'
    if cli.exists():
        rejected = subprocess.run([str(dotnet), str(cli), 'help', '--data-root', str(root)], capture_output=True, timeout=20)
        assert rejected.returncode == 2 and b'DATA_ROOT_BUSY' in rejected.stderr
        results.append({'name': 'legacy-cli-obeys-the-same-root-lease', 'status': 'passed'})

    process.stdin.write(b'x' * 1048577 + b'\n')
    process.stdin.flush()
    assert read(process)['error']['code'] == 'FRAME_TOO_LARGE'
    assert send(process, 'resync', 'host.hello', {'minimumVersion': 1, 'maximumVersion': 1})['result']['protocolVersion'] == 1
    results.append({'name': 'actual-host-frame-bound-and-resync', 'status': 'passed'})
    assert send(process, 'execute', 'process.run', {'executable': '/bin/sh'})['error']['code'] == 'METHOD_NOT_SUPPORTED'
    assert send(process, 'read-path', 'blob.open', {'kind': 'file', 'path': '/etc/passwd'})['error']['code'] == 'RESOURCE_KIND_UNSUPPORTED'
    results.append({'name': 'no-arbitrary-exec-or-file-read', 'status': 'passed'})
    assert send(process, 'shutdown', 'host.shutdown')['result']['accepted']
    process.wait(timeout=10)
    assert process.returncode == 0
finally:
    if process.poll() is None:
        process.terminate()
        process.wait(timeout=10)

# 独立 Host 在 stdin EOF 后继续真实安装；不会把父客户端消失当作 operation.cancel。
eof_root = pathlib.Path(sys.argv[2]).resolve()
platform = json.loads((eof_root.parent / 'host-target.json').read_text())
process = start(eof_root)
try:
    send(process, 'hello', 'host.hello', {'minimumVersion': 1, 'maximumVersion': 1})
    response = send(process, 'install', 'version.install', {'idempotencyKey': 'eof-install', 'installation': {'gameVersion': '1.21.1', 'loader': 'vanilla', 'target': platform}})
    operation = response['result']['operationId']
    process.stdin.close()
    process.wait(timeout=180)
    assert process.returncode == 0
    record = json.loads((eof_root / 'operations-v3' / (operation + '.json')).read_text())
    assert record['Snapshot']['State'] == 'succeeded'
    results.append({'name': 'eof-drains-real-install-instead-of-cancelling', 'status': 'passed', 'operationId': operation})
finally:
    if process.poll() is None:
        process.terminate()
        process.wait(timeout=10)

(root.parent / 'protocol-tests.json').write_text(json.dumps({'failures': 0, 'results': results}, indent=2))
print('PROTOCOL_TESTS_PASSED')
