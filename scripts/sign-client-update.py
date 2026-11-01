"""Sign a platform-verified client ZIP. Public trust policy must already be in the client."""
import argparse
import datetime
import hashlib
import json
import pathlib
import plistlib
import subprocess
import tempfile
import zipfile

p=argparse.ArgumentParser(description=__doc__)
for name in ('archive','key','dotnet','tool','output'):
    p.add_argument('--'+name,type=pathlib.Path,required=True)
p.add_argument('--kind',choices=['macos-app','client-directory'],required=True)
p.add_argument('--url',required=True)
p.add_argument('--channel',default='preview')
a=p.parse_args()
from urllib.parse import urlsplit
url=urlsplit(a.url)
if url.scheme!='https' or not url.netloc or url.username or url.fragment:
    raise SystemExit('HTTPS_CLIENT_URL_REQUIRED')
if a.output.exists(): raise SystemExit('FRESH_OUTPUT_REQUIRED')
with zipfile.ZipFile(a.archive) as archive:
    if a.kind=='macos-app':
        plists=[n for n in archive.namelist() if n.endswith('.app/Contents/Info.plist') and not n.startswith('__MACOSX/')]
        if len(plists)!=1: raise SystemExit('ONE_APP_REQUIRED')
        bundle=plists[0].split('/')[0]
        base=bundle+'/Contents/MacOS/'
        info=plistlib.loads(archive.read(plists[0]))
    else:
        bundle=''
        base=''
        info=None
    release=json.loads(archive.read(base+'client-release.json'))
    policy=json.loads(archive.read(base+'client-update-policy.json'))
    if policy['Channel']!=a.channel or release['Runtime']!='osx-arm64': raise SystemExit('CLIENT_POLICY_OR_RUNTIME_MISMATCH')
    if info and (info['CFBundleIdentifier']!='local.pcl.macport.preview' or info['CFBundleVersion']!=str(release['Sequence']) or info['CFBundleShortVersionString']!=release['Version']):
        raise SystemExit('CLIENT_BUNDLE_VERSION_MISMATCH')
manifest={'Format':1,'Channel':a.channel,'Release':release,'Kind':a.kind,'ArchiveRoot':bundle,
          'PackageUrl':a.url,'Size':a.archive.stat().st_size,'Sha256':hashlib.sha256(a.archive.read_bytes()).hexdigest(),
          'ExpiresUtc':(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(days=30)).isoformat()}
a.output.parent.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(prefix='.client-sign-',dir=a.output.parent) as temporary:
    stage=pathlib.Path(temporary)
    document=stage/'manifest.json'
    document.write_text(json.dumps(manifest,indent=2)+'\n')
    pin=stage/'public.pem'
    pin.write_text(policy['PublicKeyPem'])
    envelope=stage/'client-feed.json'
    command=[str(a.dotnet.resolve()),str(a.tool.resolve())]
    subprocess.run([*command,'--sign-document',str(document),str(a.key.resolve()),str(envelope)],check=True)
    subprocess.run([*command,'--verify-document',str(envelope),str(pin),str(document)],check=True)
    envelope.rename(a.output)
print('SIGNED_CLIENT_FEED_CREATED')
