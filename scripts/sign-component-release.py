"""Generate a signed release index, Host feed, and clients with a pinned policy.

Run only on a trusted publisher machine, after native platform signing. Private
keys remain outside all publish directories and are never copied into outputs.
"""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import shutil
import subprocess
import tempfile
from urllib.parse import urlsplit

parser = argparse.ArgumentParser(description=__doc__)
for name in ('work', 'release', 'key', 'dotnet', 'tool', 'output'):
    parser.add_argument('--' + name, type=pathlib.Path, required=True)
parser.add_argument('--base-url', required=True, help='Immutable HTTPS asset directory for this release')
parser.add_argument('--feed-url', required=True, help='Stable HTTPS Host feed endpoint for this platform/channel')
parser.add_argument('--client-feed-url', help='Stable feed for standalone client ZIPs')
parser.add_argument('--app-feed-url', help='Stable feed for complete Mac app updates')
parser.add_argument('--content-release', type=pathlib.Path)
parser.add_argument('--content-tool', type=pathlib.Path, help='Built Pcl.ContentPack.dll, required with --content-release')
args = parser.parse_args()
args.work, args.output, args.key = (p.resolve() for p in (args.work, args.output, args.key))
for directory in (args.work, args.output):
    if args.key == directory or directory in args.key.parents:
        raise SystemExit('SIGNING_KEY_MUST_BE_OUTSIDE_ARTIFACTS')
if args.output.exists() or args.output == args.work or args.output in args.work.parents:
    raise SystemExit('FRESH_SEPARATE_OUTPUT_REQUIRED')
if bool(args.client_feed_url) != bool(args.app_feed_url):
    raise SystemExit('BOTH_CLIENT_FEEDS_REQUIRED')
if args.client_feed_url and len({args.feed_url, args.client_feed_url, args.app_feed_url}) != 3:
    raise SystemExit('CLIENT_FEEDS_MUST_BE_DISTINCT')
for url in filter(None, (args.base_url, args.feed_url, args.client_feed_url, args.app_feed_url)):
    parsed = urlsplit(url)
    if parsed.scheme != 'https' or not parsed.netloc or parsed.username or parsed.query or parsed.fragment:
        raise SystemExit('HTTPS_PUBLISH_URL_REQUIRED')
release = json.loads(args.release.read_text())
probe = json.loads((args.work / 'host-probe.json').read_text())
rid = probe['build']['runtime']
entry = 'Pcl.Host.exe' if rid.startswith('win-') else 'Pcl.Host'
package_name = f"pcl-{release['version']}-{rid}-host.zip"
feed_name = f'pcl-host-feed-{rid}.json'
spec = importlib.util.spec_from_file_location('packages', pathlib.Path(__file__).with_name('package-components.py'))
packages = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packages)

def tool(*arguments):
    subprocess.run([str(args.dotnet.resolve()), str(args.tool.resolve()), *map(str, arguments)], check=True)

args.output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='.signed-components-', dir=args.output.parent) as temporary:
    stage = pathlib.Path(temporary)
    signed_host = stage / 'signed-host'
    tool(args.work / 'host', args.work / 'host-probe.json', entry,
         args.base_url.rstrip('/') + '/' + package_name, release['channel'], args.key, signed_host)
    public_key = (signed_host / 'public-key.pem').read_text()
    policy = {'FeedUrl': args.feed_url, 'PublicKeyPem': public_key, 'Channel': release['channel'],
              'MinimumSequence': release['hostSequence'], 'DataEpoch': 1, 'Protocol': 1,
              'RequiredCapabilities': ['desktop.contract-only.v1', 'host.update.prepare.v1'],
              'ApplyWhenIdle': True, 'CheckIntervalSeconds': 21600}
    client = stage / 'client'
    shutil.copytree(args.work / 'client', client)
    (client / 'host-update-policy.json').write_text(json.dumps(policy, indent=2) + '\n')
    client_policy = None
    if args.client_feed_url:
        client_policy = {'FeedUrl': args.client_feed_url, 'PublicKeyPem': public_key, 'Channel': release['channel']}
        (client / 'client-update-policy.json').write_text(json.dumps(client_policy, indent=2) + '\n')
    result = stage / 'release'
    manifest = packages.package(client, args.work / 'host', probe, release, result, args.base_url)
    # The update feed and release index must describe exactly the SAME ZIP.
    shutil.copy2(signed_host / 'host.zip', result / package_name)
    shutil.copy2(signed_host / 'feed.json', result / feed_name)
    shutil.copy2(signed_host / 'public-key.pem', result / 'publisher-public-key.pem')
    host_bytes = result / package_name
    with host_bytes.open('rb') as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    manifest['packages']['host'].update(size=host_bytes.stat().st_size, sha256=digest.hexdigest(),
        signature={'file': feed_name, 'algorithm': 'RSA-PSS-SHA256',
                   'sha256': hashlib.sha256((result / feed_name).read_bytes()).hexdigest()})
    manifest['trust'] = 'RSA-PSS-SHA256; verify using an independently pinned publisher key'
    manifest['hostFeedUrl'] = args.feed_url
    manifest['publisherPublicKeySha256'] = hashlib.sha256(public_key.encode()).hexdigest()
    if args.content_release:
        if not args.content_tool:
            raise SystemExit('CONTENT_VERIFIER_REQUIRED')
        content = args.content_release.resolve()
        inventory = json.loads((content / 'content-manifest.json').read_text())
        asset = inventory['package']
        if pathlib.PurePath(asset['file']).name != asset['file'] or '/' in asset['file'] or '\\' in asset['file']:
            raise SystemExit('CONTENT_ASSET_PATH_INVALID')
        source = content / asset['file']
        if source.is_symlink() or source.stat().st_size != asset['size'] or hashlib.sha256(source.read_bytes()).hexdigest() != asset['sha256']:
            raise SystemExit('CONTENT_ASSET_HASH_INVALID')
        trusted_policy = args.work / 'host/content-update-policy.json'
        content_feed = json.loads(trusted_policy.read_text()).get('FeedUrl')
        if content_feed:
            parsed = urlsplit(content_feed)
            if parsed.scheme != 'https' or not parsed.netloc or parsed.username or parsed.fragment or parsed.query or content_feed in (args.feed_url, args.client_feed_url, args.app_feed_url):
                raise SystemExit('DISTINCT_HTTPS_CONTENT_FEED_REQUIRED')
            manifest['contentFeedUrl'] = content_feed
        subprocess.run([str(args.dotnet.resolve()), str(args.content_tool.resolve()), 'verify', str(source),
                        str(trusted_policy), release['version']], check=True)
        shutil.copy2(source, result / asset['file'])
        manifest['packages']['content'] = {**asset, 'runtime': 'any', 'kind': 'wiki-project-index',
            'url': args.base_url.rstrip('/') + '/' + asset['file'],
            'trust': 'embedded RSA-PSS signature verified against Host content policy'}
        bundle = inventory['archive']
        if pathlib.PurePath(bundle['file']).name != bundle['file'] or '/' in bundle['file'] or '\\' in bundle['file']:
            raise SystemExit('CONTENT_BUNDLE_PATH_INVALID')
        source_bundle = content / bundle['file']
        if source_bundle.is_symlink() or source_bundle.stat().st_size != bundle['size'] or hashlib.sha256(source_bundle.read_bytes()).hexdigest() != bundle['sha256']:
            raise SystemExit('CONTENT_BUNDLE_HASH_INVALID')
        shutil.copy2(source_bundle, result / bundle['file'])
        manifest['packages']['contentBundle'] = {**bundle, 'runtime': 'any', 'kind': 'content-distribution',
            'url': args.base_url.rstrip('/') + '/' + bundle['file']}
    if client_policy:
        feed = result / 'client-feed.json'
        item = manifest['packages']['client']
        subprocess.run(['python3', str(pathlib.Path(__file__).with_name('sign-client-update.py')),
            '--archive', str(result / item['file']), '--kind', 'client-directory', '--url', item['url'],
            '--key', str(args.key), '--dotnet', str(args.dotnet), '--tool', str(args.tool),
            '--channel', release['channel'], '--output', str(feed)], check=True)
        item['signature'] = {'file': feed.name, 'algorithm': 'RSA-PSS-SHA256',
                             'sha256': hashlib.sha256(feed.read_bytes()).hexdigest()}
        manifest['clientFeedUrl'] = args.client_feed_url
        manifest['appFeedUrl'] = args.app_feed_url
        (result / 'client-update-policy.json').write_text(json.dumps(client_policy, indent=2) + '\n')
        (result / 'app-update-policy.json').write_text(json.dumps({**client_policy, 'FeedUrl': args.app_feed_url}, indent=2) + '\n')
    index = result / 'release-manifest.json'
    index.write_text(json.dumps(manifest, indent=2) + '\n')
    envelope = result / 'release-manifest.signed.json'
    tool('--sign-document', index, args.key, envelope)
    tool('--verify-document', envelope, signed_host / 'public-key.pem', index)
    (result / 'host-update-policy.json').write_text(json.dumps(policy, indent=2) + '\n')
    # Retain deployment input for the .app assembler. It signs the bundle after
    # the publisher policy has been installed; callers never modify signed apps.
    shutil.copytree(client, result / 'client-deployment')
    with (result / 'SHA256SUMS').open('w') as sums:
        for path in sorted(result.iterdir()):
            if path.is_file() and path.name != 'SHA256SUMS':
                digest = hashlib.sha256()
                with path.open('rb') as stream:
                    for block in iter(lambda: stream.read(1024 * 1024), b''):
                        digest.update(block)
                sums.write(digest.hexdigest() + '  ' + path.name + '\n')
    result.rename(args.output)
print('SIGNED_COMPONENT_RELEASE_CREATED')
