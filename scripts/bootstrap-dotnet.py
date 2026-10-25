"""Install the pinned official SDK privately; compatible with macOS Python 3.9."""
import hashlib, json, pathlib, platform, posixpath, tarfile, urllib.request

root=pathlib.Path(__file__).resolve().parent.parent
version=json.loads((root/'global.json').read_text())['sdk']['version']
if platform.system()!='Darwin' or platform.machine() not in ('arm64','aarch64'):
    raise SystemExit('This bootstrap currently targets Apple Silicon macOS.')
meta=json.load(urllib.request.urlopen('https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'))
sdks=[s for release in meta['releases'] for s in release.get('sdks',[release['sdk']])]
sdk=next(s for s in sdks if s['version']==version)
item=next(f for f in sdk['files'] if f['rid']=='osx-arm64' and f['name'].endswith('.tar.gz'))
tools=root/'.tooling';tools.mkdir(exist_ok=True)
archive=tools/'dotnet-sdk-osx-arm64.tar.gz'
if not archive.exists() or hashlib.sha512(archive.read_bytes()).hexdigest().lower()!=item['hash'].lower():
    urllib.request.urlretrieve(item['url'],archive)
if hashlib.sha512(archive.read_bytes()).hexdigest().lower()!=item['hash'].lower():raise SystemExit('SDK hash mismatch')
with tarfile.open(archive) as tar:
    for member in tar.getmembers():
        name=posixpath.normpath(member.name)
        if name.startswith('/') or name=='..' or name.startswith('../'):raise SystemExit('Unsafe archive path')
        if member.issym() or member.islnk():
            target=posixpath.normpath(posixpath.join(posixpath.dirname(name),member.linkname))
            if target.startswith('/') or target=='..' or target.startswith('../'):raise SystemExit('Unsafe archive link')
    tar.extractall(tools/'dotnet')
(tools/'sdk-provenance.json').write_text(json.dumps({'version':version,'url':item['url'],'sha512':item['hash']},indent=2))
print('Private SDK ready:',version)
