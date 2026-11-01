"""Build signed disposable acceptance fixtures from the actual macOS app, never production assets."""
import argparse,importlib.util,json,pathlib,plistlib,shutil,subprocess
p=argparse.ArgumentParser(description=__doc__)
for name in ('app','output','key','dotnet','signer'):
    p.add_argument('--'+name,type=pathlib.Path,required=True)
a=p.parse_args()
root=pathlib.Path(__file__).resolve().parent.parent
a.output.mkdir(parents=True,exist_ok=False)
def run(*args): subprocess.run(list(map(str,args)),check=True)
baseline=json.loads((a.app/'Contents/MacOS/client-release.json').read_text())['Sequence']
for name,sequence in [('good',baseline+1),('broken',baseline+2),('hang',baseline+3)]:
    work=a.output/name
    work.mkdir()
    app=work/a.app.name
    run('/usr/bin/ditto',a.app,app)
    base=app/'Contents/MacOS'
    meta=base/'client-release.json'
    release=json.loads(meta.read_text());release['Sequence']=sequence;meta.write_text(json.dumps(release,indent=2))
    plist=app/'Contents/Info.plist'; info=plistlib.loads(plist.read_bytes());info['CFBundleVersion']=str(sequence);plist.write_bytes(plistlib.dumps(info))
    run('/usr/bin/codesign','--force','--sign','-',meta)
    if name=='broken':
        damaged=base/'PCL-MacPort.dll';damaged.write_bytes(b'Signed publisher fixture with an invalid managed executable.')
        run('/usr/bin/codesign','--force','--sign','-',damaged)
    if name=='hang':
        source=work/'hang.c';source.write_text('#include <unistd.h>\nint main(void) { sleep(120); return 0; }\n')
        run('cc',source,'-o',base/'PCL-MacPort')
        run('/usr/bin/codesign','--force','--sign','-',base/'PCL-MacPort')
    run('/usr/bin/codesign','--force','--sign','-',app)
    archive=work/'client.zip'
    run('python3',root/'scripts/package-app-archive.py',app,archive,'--report',work/'archive-report.json')
    run('python3',root/'scripts/sign-client-update.py','--archive',archive,'--key',a.key,'--dotnet',a.dotnet,
        '--tool',a.signer,'--output',work/'feed.json','--kind','macos-app','--url','https://updates.example/client/'+name+'.zip')
for source,name in [(a.app,'raw-base'),(a.output/'good'/a.app.name,'raw-client')]:
    raw=a.output/name
    run('/usr/bin/ditto',source/'Contents/MacOS',raw)
    shutil.rmtree(raw/'host')
raw_release=a.output/'raw';raw_release.mkdir()
spec=importlib.util.spec_from_file_location('components',root/'scripts/package-components.py')
components=importlib.util.module_from_spec(spec);spec.loader.exec_module(components)
components.archive(a.output/'raw-client',raw_release/'client.zip')
run('python3',root/'scripts/sign-client-update.py','--archive',raw_release/'client.zip','--key',a.key,'--dotnet',a.dotnet,
    '--tool',a.signer,'--output',raw_release/'feed.json','--kind','client-directory','--url','https://updates.example/client/raw.zip')
print('DISPOSABLE_CLIENT_FIXTURES_READY')
