"""Download and launch the documented Apple Silicon matrix; does not open existing worlds."""
from pathlib import Path
import argparse, json, os, shutil, subprocess

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--data-root', type=Path, default=root / 'runtime/apple-silicon-1.21.1')
parser.add_argument('--output', type=Path, default=root / 'artifacts/multi-version-check')
parser.add_argument('--seconds', type=int, default=55)
parser.add_argument('--loaders', action='store_true')
options = parser.parse_args()
if options.seconds < 20: parser.error('--seconds must be at least 20')
data, output = options.data_root.resolve(), options.output.resolve()
output.mkdir(parents=True, exist_ok=True)
cli = [os.environ.get('DOTNET', str(root / '.tooling/dotnet/dotnet')), str(root / 'src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll')]
rows = []
cases = [(v, 'vanilla', None, j) for v, j in [('1.16.5', 8), ('1.18.2', 17), ('1.20.1', 17), ('1.21.1', 21), ('26.3', 25)]]
if options.loaders:
    cases += [('1.20.1', 'fabric', '0.19.5', 17), ('1.20.1', 'forge', '47.4.28', 17), ('1.20.4', 'neoforge', '20.4.253', 17), ('26.3', 'fabric', '0.19.5', 25)]

def run(label, args):
    with (output / (label + '.log')).open('w') as log:
        return subprocess.run(cli + args + ['--data-root', str(data)], cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=1800).returncode

for version, loader, loader_version, java in cases:
    label = version + '-' + loader
    row = dict(version=version, loader=loader, loaderVersion=loader_version, expectedJava=java)
    row['installExit'] = run(label + '-install', ['install', '--game-version', version])
    profile, profile_id = 'vanilla', version
    if row['installExit'] == 0 and loader != 'vanilla':
        args = ['fabric-install'] if loader == 'fabric' else ['loader-install', '--loader', loader]
        row['loaderExit'] = run(label + '-loader', args + ['--game-version', version, '--loader-version', loader_version])
        profile = 'fabric' if loader == 'fabric' else f'{loader}-{version}-{loader_version}'
        profile_id = f'fabric-loader-{loader_version}-{version}' if loader == 'fabric' else profile
    if row['installExit'] == 0 and row.get('loaderExit', 0) == 0:
        row['launchExit'] = run(label + '-launch', ['launch', '--game-version', version, '--profile', profile, '--offline-name', 'PCL_Mac', '--smoke-seconds', str(options.seconds), '--isolated-evidence'])
        receipt = data / 'evidence' / ('launch-result-' + profile_id + '.json')
        if row['launchExit'] == 0:
            result = json.loads(receipt.read_text())
            assert result['gameVersion'] == version and result['javaMajor'] == java and result['javaArchitecture'] == 'aarch64'
            assert result['graphicsLogObserved'] and result['atlasCreationObserved'] and (result['wasAlive'] or result['exitCode'] == 0)
            shutil.copy2(receipt, output / (label + '-launch.json'))
    row['passed'] = all(row.get(key, 0) == 0 for key in ('installExit', 'loaderExit', 'launchExit')) and 'launchExit' in row
    rows.append(row)
    (output / 'matrix.json').write_text(json.dumps(rows, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(row, ensure_ascii=False), flush=True)
raise SystemExit(0 if all(x['passed'] for x in rows) else 1)
