"""Recheck the timed-out install and the latest snapshot from a saved official manifest."""
from pathlib import Path
import subprocess,json,shutil
root=Path(__file__).resolve().parent.parent;out=root/'artifacts/evidence-0.8/final-additional';out.mkdir(parents=True,exist_ok=True)
data=root/'runtime/apple-silicon-1.21.1';cli=[str(root/'.tooling/dotnet/dotnet'),str(root/'src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll')]
snapshot=json.loads((root/'artifacts/evidence-0.8/official-latest.json').read_text())['latest']['snapshot'];rows=[]
for version in ['1.18.2',snapshot]:
 row={'gameVersion':version,'loader':'vanilla'}
 for stage,args in [('install',['install']),('launch',['launch','--offline-name','PCL_Mac','--smoke-seconds','35','--isolated-evidence'])]:
  with (out/(version+'-'+stage+'.log')).open('w') as log:row[stage+'Exit']=subprocess.run(cli+args+['--game-version',version,'--data-root',str(data)],stdout=log,stderr=subprocess.STDOUT,cwd=root,timeout=1800).returncode
  if row[stage+'Exit']!=0:break
  if stage=='launch':shutil.copy2(data/'evidence'/('launch-result-'+version+'.json'),out/(version+'-launch.json'))
 row['passed']=row.get('installExit')==0 and row.get('launchExit')==0;rows.append(row);(out/'results.json').write_text(json.dumps(rows,indent=2));print(json.dumps(row),flush=True)
raise SystemExit(0 if all(x['passed'] for x in rows) else 1)
