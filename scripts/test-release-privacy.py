"""The runner-home exception must not hide current workspace paths or secrets."""
import json, os, pathlib, subprocess, tempfile
script = pathlib.Path(__file__).with_name('audit-release-privacy.py').resolve()
env = {**os.environ, 'HOME': '/Users/runner', 'GITHUB_ACTIONS': 'true'}
with tempfile.TemporaryDirectory() as tmp:
    target = pathlib.Path(tmp)
    file = target/'libcoreclr.dylib'
    upstream = b'/Users/runner/work/1/s/src/runtime/src/coreclr/test.cpp\0'
    def check(data, passed, category=None):
        file.write_bytes(data)
        result = subprocess.run(['python3', str(script), str(target)], env=env, capture_output=True, text=True)
        report = json.loads(result.stdout)
        assert (result.returncode == 0) == passed, report
        if category: assert any(x['kind'] == category for x in report['findings']), report
    check(upstream, True)
    check(upstream+b'/Users/runner/work/PrivateProject/build/file.cpp', False, 'developer_home')
    check(upstream+str(script.parent.parent).encode(), False, 'build_workspace')
    check(upstream+b'ghp_'+b'x'*40, False, 'github_token')
    file.rename(target/'application.dll')
    result = subprocess.run(['python3', str(script), str(target)], env=env, capture_output=True, text=True)
    assert result.returncode != 0
print('RELEASE_PRIVACY_TESTS_PASSED: upstream classification, project path, workspace, token, nondependency rejection')
