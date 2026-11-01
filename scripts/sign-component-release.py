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
args = parser.parse_args()
args.work, args.output, args.key = (p.resolve() for p in (args.work, args.output, args.key))
for directory in (args.work, args.output):
    if args.key == directory or directory in args.key.parents:
        raise SystemExit('SIGNING_KEY_MUST_BE_OUTSIDE_ARTIFACTS')
if args.output.exists() or args.output == args.work or args.output in args.work.parents:
    raise SystemExit('FRESH_SEPARATE_OUTPUT_REQUIRED')
for url in (args.base_url, args.feed_url):
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
