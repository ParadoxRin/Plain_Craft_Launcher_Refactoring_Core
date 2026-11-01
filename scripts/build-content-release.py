"""Create a signed, platform-independent wiki/mapping release with source notices."""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
for name in ('dotnet', 'tool', 'key', 'output'):
    parser.add_argument('--' + name, type=pathlib.Path, required=True)
parser.add_argument('--rows', type=pathlib.Path, help='Omit to export the exact built-in PCLCS index')
parser.add_argument('--sequence', type=int, required=True)
parser.add_argument('--min-host', default='0.42.1')
parser.add_argument('--max-host', default='0.42.99')
parser.add_argument('--channel', default='preview')
parser.add_argument('--feed-url', help='Stable HTTPS URL serving the signed content envelope')
parser.add_argument('--provenance', default='PCL 6b9a50df911c9d68b69b24c979efba477fb14b67 / PCLCS Resource/WikiEntries.txt')
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parent.parent
output, key = args.output.resolve(), args.key.resolve()
if output.exists() or output == key or output in key.parents or args.sequence <= 0:
    raise SystemExit('FRESH_OUTPUT_AND_SEPARATE_KEY_REQUIRED')
if args.rows and (args.rows.resolve() == output or output in args.rows.resolve().parents):
    raise SystemExit('ROWS_MUST_BE_OUTSIDE_OUTPUT')
command = [str(args.dotnet.resolve()), str(args.tool.resolve())]
def run(*arguments):
    subprocess.run([*command, *map(str, arguments)], check=True)

output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='.content-', dir=output.parent) as temporary:
    stage = pathlib.Path(temporary)
    rows = args.rows.resolve() if args.rows else stage / 'rows.json'
    if not args.rows:
        run('export', rows)
    delivery = stage / 'delivery'
    delivery.mkdir()
    package = delivery / f'pcl-wiki-project-index-{args.sequence}.json'
    run('sign', rows, args.sequence, args.min_host, args.max_host, args.channel, args.provenance, key, package)
    policy = stage / 'content-update-policy.json'
    run('policy', key, args.channel, policy)
    if args.feed_url:
        from urllib.parse import urlsplit
        url = urlsplit(args.feed_url)
        if url.scheme != 'https' or not url.netloc or url.username or url.fragment or url.query:
            raise SystemExit('HTTPS_CONTENT_FEED_REQUIRED')
        trust = json.loads(policy.read_text())
        trust['FeedUrl'] = args.feed_url
        policy.write_text(json.dumps(trust, indent=2) + '\n')
    run('verify', package, policy, args.min_host)
    for source, target in [('PCL-LICENCE.txt', 'PCL-LICENCE.txt'), ('LICENSE.md', 'LICENSE.md'),
                           ('vendor/PCLCS/README.integration.md', 'PCLCS-integration.md')]:
        shutil.copy2(root / source, delivery / target)
    spec = importlib.util.spec_from_file_location('packages', root / 'scripts/package-components.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    archive = module.archive(delivery, stage / f'pcl-wiki-project-index-{args.sequence}.zip')
    inventory = {'format': 1, 'kind': 'wiki-project-index', 'runtime': 'any', 'sequence': args.sequence,
                 'minHostVersion': args.min_host, 'maxHostVersion': args.max_host, 'channel': args.channel,
                 'provenance': args.provenance, 'archive': archive,
                 'package': {'file': package.name, 'size': package.stat().st_size,
                             'sha256': hashlib.sha256(package.read_bytes()).hexdigest()}}
    final = stage / 'release'
    final.mkdir()
    for source in [*delivery.iterdir(), stage / archive['file'], policy]:
        shutil.copy2(source, final / source.name)
    (final / 'content-manifest.json').write_text(json.dumps(inventory, indent=2) + '\n')
    final.rename(output)
print('CONTENT_RELEASE_CREATED', output)
