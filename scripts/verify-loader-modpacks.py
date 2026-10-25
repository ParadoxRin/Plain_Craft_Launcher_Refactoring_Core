from pathlib import Path
import hashlib,json,zipfile,subprocess,shutil
r=Path(__file__).resolve().parent.parent;data=r/'runtime/apple-silicon-1.21.1';ev=r/'artifacts/evidence';exe=[str(r/'.tooling/dotnet/dotnet'),str(r/'src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll')]
for loader,version,folder in [('forge','52.1.20','forge-1.21.1-52.1.20'),('neoforge','21.1.264','neoforge-1.21.1-21.1.264')]:
 receipt=json.loads((ev/('mod-'+folder+'.json')).read_text());release=receipt['plan']['Releases'][-1];f=release['File'];content=(data/'instances'/folder/'mods'/f['Name']).read_bytes()
 index={'formatVersion':1,'game':'minecraft','versionId':'fixture-1','name':'PCL '+loader+' pack integration','dependencies':{'minecraft':'1.21.1',loader:version},'files':[{'path':'mods/'+f['Name'],'hashes':{'sha1':hashlib.sha1(content).hexdigest(),'sha512':hashlib.sha512(content).hexdigest()},'downloads':[f['Url']],'fileSize':len(content)}]}
 pack=r/'artifacts/loader-research'/('pcl-'+loader+'-integration.mrpack')
 with zipfile.ZipFile(pack,'w') as z:z.writestr('modrinth.index.json',json.dumps(index))
 name='pack-integration-'+loader+'-1.21.1';base=['--data-root',str(data)]
 with (r/'artifacts'/(name+'-install.log')).open('w') as log:subprocess.run(exe+['pack-install','--file',str(pack),'--instance',name]+base,check=True,stdout=log,stderr=subprocess.STDOUT)
 shutil.copy2(data/'evidence'/(name+'-modpack-install.json'),ev/(name+'-install.json'))
 with (r/'artifacts'/(name+'-game.log')).open('w') as log:subprocess.run(exe+['launch','--profile',name,'--demo','--smoke-seconds','35']+base,check=True,stdout=log,stderr=subprocess.STDOUT)
 shutil.copy2(data/'evidence/launch-result.json',ev/(name+'-launch.json'))
 print('PACK LOADER INTEGRATION PASSED',loader,flush=True)
