"""Exercise release verification failures using an isolated copy of a real signed release."""
import argparse, hashlib, json, os, pathlib, shutil, subprocess, tempfile
p=argparse.ArgumentParser(description=__doc__)
for name in ('release','key','dotnet','tool','report'):
    p.add_argument('--'+name,type=pathlib.Path,required=True)
a=p.parse_args()
command=[str(a.dotnet.resolve()),str(a.tool.resolve())]
verifier=pathlib.Path(__file__).with_name('verify-unified-release.py')
checks=[]
with tempfile.TemporaryDirectory(prefix='unified-negative-') as temporary:
    root=pathlib.Path(temporary)
    wrong=root/'wrong.pem'
    subprocess.run(['openssl','genpkey','-algorithm','RSA','-pkeyopt','rsa_keygen_bits:2048','-out',str(wrong)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    def rejected(directory,key,name):
        result=subprocess.run(['python3',str(verifier),'--release',str(directory),'--key',str(key),'--dotnet',str(a.dotnet),'--tool',str(a.tool)],capture_output=True)
        if result.returncode==0: raise RuntimeError('UNEXPECTED_VERIFICATION_SUCCESS:'+name)
        checks.append(name)
    rejected(a.release,wrong,'wrong-independent-publisher-key-rejected')
    original=json.loads((a.release/'release-manifest.json').read_text())
    for name in ('asset-hash','wrong-deployment-feed'):
        stage=root/name;stage.mkdir()
        for source in a.release.iterdir():
            if source.is_file():
                if source.suffix=='.zip': os.link(source,stage/source.name)
                else: shutil.copy2(source,stage/source.name)
        manifest=json.loads(json.dumps(original))
        if name=='asset-hash': manifest['packages']['client']['sha256']='0'*64
        else:
            feed=manifest['packages']['installer']['signature']
            manifest['packages']['client']['signature']=feed
        index=stage/'release-manifest.json';index.write_text(json.dumps(manifest))
        signed=stage/'release-manifest.signed.json';signed.unlink()
        subprocess.run([*command,'--sign-document',str(index),str(a.key.resolve()),str(signed)],check=True,stdout=subprocess.DEVNULL)
        rejected(stage,a.key,name+'-rejected-even-with-valid-index-signature')
a.report.parent.mkdir(parents=True,exist_ok=True)
a.report.write_text(json.dumps({'status':'passed','checks':checks},indent=2)+'\n')
print('UNIFIED_NEGATIVE_TESTS_PASSED')
