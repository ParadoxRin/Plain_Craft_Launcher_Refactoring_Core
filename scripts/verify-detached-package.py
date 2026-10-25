"""运行隔离应用，等启动器退出后直接观察独立游戏，随后只清理该次测试 PID。"""
import json, os, pathlib, signal, subprocess, sys, time
app, data, output = map(lambda x:pathlib.Path(x).resolve(), sys.argv[1:4])
output.mkdir(parents=True,exist_ok=True)
report=output/'packaged-detached.json'
observation=output/'packaged-detached-verification.json'
if observation.exists(): observation.rename(output/'packaged-detached-late-observation.json')
env=dict(os.environ)
env.pop('PCL_DOTNET',None);env.pop('DOTNET_ROOT',None)
def alive(pid):
    try:os.kill(pid,0);return True
    except ProcessLookupError:return False
pid=None
try:
    with (output/'packaged-detached.log').open('w') as log:
        launcher=subprocess.Popen([str(app/'Contents/MacOS/PCL-MacPort'),'--data-root',str(data),'--remote-desktop-smoke','--remote-detached','--remote-report',str(report)],stdout=log,stderr=subprocess.STDOUT,env=env)
        launcher.wait(timeout=60)
    result=json.loads(report.read_text())
    assert launcher.returncode==0 and result['status']=='started'
    receipt=json.loads((data/'evidence/detached-launch.json').read_text())
    pid=receipt['pid'];host=result['hostPid']
    deadline=time.monotonic()+45
    while time.monotonic()<deadline:
        text=pathlib.Path(receipt['log']).read_text(errors='replace')
        if 'atlas' in text or not alive(pid):break
        time.sleep(.25)
    observations=[]
    for step in range(10):
        observations.append({'hostExited':not alive(host),'gameAlive':alive(pid)})
        time.sleep(.5)
    passed=all(x['hostExited'] and x['gameAlive'] for x in observations) and 'atlas' in text
    observation.write_text(json.dumps({'status':'passed' if passed else 'failed','launcherExitCode':launcher.returncode,'rendererInitialized':'atlas' in text,'observations':observations,'pid':pid,'observationSeconds':5},indent=2))
    if not passed:sys.exit(1)
finally:
    if pid is not None and alive(pid):os.kill(pid,signal.SIGTERM)
