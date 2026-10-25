"""驱动正式包的实际导出窗口；等 UI/Host 退出后直接执行 .command，不通过启动器启动游戏。"""
from pathlib import Path
import json,os,signal,subprocess,sys,time
app,data,out=map(lambda s:Path(s).resolve(),sys.argv[1:4])
out.mkdir(parents=True,exist_ok=True)
report=out/'ui.json'
def alive(pid):
    try: os.kill(pid,0);return True
    except ProcessLookupError:return False
launcher=game=None
try:
    with (out/'ui.log').open('w') as log:
        launcher=subprocess.Popen([str(app/'Contents/MacOS/PCL-MacPort'),'--data-root',str(data),'--offline-script-smoke','--offline-script-report',str(report)],stdout=log,stderr=subprocess.STDOUT)
        launcher.wait(timeout=150)
    ui=json.loads(report.read_text())
    assert ui['status']=='passed' and launcher.returncode==0,ui
    end=time.monotonic()+15
    while alive(ui['hostPid']) and time.monotonic()<end:time.sleep(.2)
    assert not alive(ui['hostPid']),'Host still running'
    checks=['export-ui-and-host-exited-before-script-launch']
    with (out/'standalone-game.log').open('w') as log:
        game=subprocess.Popen([ui['script']],stdout=log,stderr=subprocess.STDOUT)
        end=time.monotonic()+90
        while time.monotonic()<end:
            text=(out/'standalone-game.log').read_text(errors='replace')
            if 'atlas' in text or game.poll() is not None:break
            time.sleep(.25)
        assert game.poll() is None and 'atlas' in text,'standalone renderer did not initialize'
        checks.append('standalone-script-initializes-real-minecraft-1.21.1-renderer')
        time.sleep(5)
        assert game.poll() is None and not alive(ui['hostPid'])
        checks.append('game-remains-alive-without-launcher-or-host')
        game.terminate();game.wait(timeout=20)
        checks.append('owned-test-game-cleaned-up')
    (out/'standalone.json').write_text(json.dumps(dict(status='passed',checks=checks,gameExitCode=game.returncode,manualWorldAcceptance=False),indent=2))
except Exception as e:
    (out/'standalone.json').write_text(json.dumps(dict(status='failed',error=str(e)),indent=2));raise
finally:
    for process in (game,launcher):
        if process and process.poll() is None:
            process.terminate()
            try:process.wait(timeout=10)
            except subprocess.TimeoutExpired:process.kill();process.wait()
