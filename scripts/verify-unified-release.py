"""Verify unified index and client feeds against an independently supplied publisher key."""
import argparse, base64, datetime, hashlib, json, pathlib, subprocess, tempfile, zipfile
p=argparse.ArgumentParser(description=__doc__)
for name in ('release','key','dotnet','tool'):
    p.add_argument('--'+name,type=pathlib.Path,required=True)
a=p.parse_args()
command=[str(a.dotnet.resolve()),str(a.tool.resolve())]
root=a.release.resolve()
def verify(envelope,document):
    subprocess.run([*command,'--verify-document',str(envelope),str(a.key.resolve()),str(document)],check=True)
verify(root/'release-manifest.signed.json',root/'release-manifest.json')
manifest=json.loads((root/'release-manifest.json').read_text())
checks=['independently-pinned-release-index']
def asset(name):
    if pathlib.PurePath(name).name!=name or '/' in name or '\\' in name: raise ValueError('UNSAFE_ASSET_PATH')
    path=root/name
    if path.is_symlink(): raise ValueError('LINK_ASSET_REJECTED')
    return path
for name,item in manifest['packages'].items():
    data=asset(item['file']).read_bytes()
    if len(data)!=item['size'] or hashlib.sha256(data).hexdigest()!=item['sha256']: raise ValueError('ASSET_HASH_MISMATCH:'+name)
checks.append('all-indexed-package-hashes')
with zipfile.ZipFile(asset(manifest['packages']['host']['file'])) as archive:
    policy = json.loads(archive.read('content-update-policy.json'))
    if policy.get('FeedUrl') != manifest.get('contentFeedUrl') or not policy.get('FeedUrl'):
        raise ValueError('CONTENT_FEED_POLICY_MISMATCH')
checks.append('host-pins-content-feed')
with tempfile.TemporaryDirectory() as temporary:
    document=pathlib.Path(temporary)/'payload.json'
    for name,kind,feed_field in [('client','client-directory','clientFeedUrl'),('installer','macos-app','appFeedUrl')]:
        item=manifest['packages'][name]
        signature=item['signature']
        feed=asset(signature['file'])
        if hashlib.sha256(feed.read_bytes()).hexdigest()!=signature['sha256']: raise ValueError('FEED_HASH_MISMATCH')
        envelope=json.loads(feed.read_text())
        payload=base64.b64decode(envelope['Payload'],validate=True)
        document.write_bytes(payload)
        verify(feed,document)
        update=json.loads(payload)
        if update['Kind']!=kind or update['Size']!=item['size'] or update['Sha256']!=item['sha256'] or update['PackageUrl']!=item['url']:
            raise ValueError('CLIENT_FEED_INDEX_MISMATCH')
        if update['Release']['Version']!=manifest['version'] or update['Release']['Sequence']!=manifest['packages']['client']['clientSequence']:
            raise ValueError('CLIENT_FEED_VERSION_MISMATCH')
        if datetime.datetime.fromisoformat(update['ExpiresUtc'].replace('Z','+00:00'))<=datetime.datetime.now(datetime.timezone.utc):
            raise ValueError('EXPIRED_CLIENT_FEED')
        with zipfile.ZipFile(asset(item['file'])) as archive:
            base=update['ArchiveRoot']+'/Contents/MacOS/' if kind=='macos-app' else ''
            policy=json.loads(archive.read(base+'client-update-policy.json'))
            if policy['FeedUrl']!=manifest[feed_field] or policy['Channel']!=update['Channel']:
                raise ValueError('CLIENT_POLICY_FEED_MISMATCH')
            # Verify using the actual embedded key as well as the independent publisher pin.
            embedded=pathlib.Path(temporary)/'embedded.pem';embedded.write_text(policy['PublicKeyPem'])
            subprocess.run([*command,'--verify-document',str(feed),str(embedded),str(document)],check=True)
        checks.append(name+'-feed-policy-version-and-bytes')
if manifest['clientFeedUrl']==manifest['appFeedUrl']: raise ValueError('DISTINCT_DEPLOYMENT_FEEDS_REQUIRED')
(root/'unified-verification.json').write_text(json.dumps({'status':'passed','checks':checks},indent=2)+'\n')
print('UNIFIED_RELEASE_VERIFIED')
