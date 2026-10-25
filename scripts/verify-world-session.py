"""Observe a real user-driven world save/re-entry session without controlling the game UI."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import time
from world_log_evidence import world_events, overworld_regions

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--version', required=True)
parser.add_argument('--profile', required=True)
parser.add_argument('--game-directory', type=Path, required=True)
parser.add_argument('--world', required=True)
parser.add_argument('--label', required=True)
parser.add_argument('--existing-world', action='store_true')
parser.add_argument('--visits', type=int, default=2, choices=[1, 2])
parser.add_argument('--cli-directory', type=Path, default=root / 'src/Pcl.Cli/bin/Release/net10.0')
parser.add_argument('--data-root', type=Path, default=root / 'runtime/apple-silicon-1.21.1')
options = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_-]+', options.label + options.world):
    parser.error('Use simple ASCII labels and world names.')
output = root / 'artifacts/verification-0.18/worlds' / options.label
output.mkdir(parents=True, exist_ok=False)
world = options.game_directory.resolve() / 'saves' / options.world
if world.exists() != options.existing_world:
    parser.error('World existence differs from the requested new/restored-world test.')
index = json.loads((options.data_root / 'accounts.json').read_text())
account = next(a for a in index['Accounts'] if a['Id'] == index['SelectedId'] and a['Kind'] == 1 and a['Remembered'])


def inventory(directory):
    """Hash only this named test world's files; never serialize account or player NBT data."""
    if not directory.exists():
        return {}
    result = {}
    for path in sorted(directory.rglob('*')):
        if path.is_symlink():
            raise RuntimeError('World evidence refuses symbolic links.')
        if path.is_file():
            result[str(path.relative_to(directory))] = dict(
                size=path.stat().st_size,
                sha256=hashlib.sha256(path.read_bytes()).hexdigest())
    return result


before = inventory(world)
began = time.time()
provenance = {name: hashlib.sha256((options.cli_directory / name).read_bytes()).hexdigest()
              for name in ('Pcl.Cli.dll', 'Pcl.Core.dll')}
(output / 'before.json').write_text(json.dumps(before, indent=2) + '\n')
(output / 'host.json').write_text(json.dumps(provenance, indent=2) + '\n')
# The user operates Minecraft. A bounded title-screen smoke timeout is a safety stop,
# never proof of a normal save/exit; timed-out sessions fail the checks below.
with (output / 'launcher.log').open('w') as log:
    process = subprocess.Popen([
        str(root / '.tooling/dotnet/dotnet'), str(options.cli_directory / 'Pcl.Cli.dll'),
        'launch', '--game-version', options.version, '--profile', options.profile,
        '--account', account['Id'], '--data-root', str(options.data_root),
        '--smoke-seconds', '1200', '--isolated-evidence'], cwd=root,
        stdout=log, stderr=subprocess.STDOUT)
    exit_code = process.wait()

profile_id = options.version
profile_file = options.game_directory / 'profile.json'
if profile_file.exists():
    profile_id = json.loads(profile_file.read_text())['id']
receipt = options.data_root / 'evidence' / ('launch-result-' + profile_id + '.json')
result = dict(status='failed', version=options.version, profile=options.profile,
              world=options.world, launchExit=exit_code, userVisualAcceptance='pending',
              startedAtUnix=began, completedAtUnix=time.time())
if receipt.exists() and receipt.stat().st_mtime >= began:
    launch = json.loads(receipt.read_text())
    shutil.copy2(receipt, output / 'launch.json')
    game_log = Path(launch['log']).read_text(errors='replace')
    counts = world_events(game_log)
    starts = counts['integratedServerStarts']
    stops = counts['integratedServerStops']
    errors = counts['errorEvents']
    after = inventory(world)
    chunks = overworld_regions(after)
    changed = [name for name in after if after[name] != before.get(name)]
    result.update(integratedServerStarts=starts, integratedServerStops=stops,
                  errorEvents=errors, levelDatSaved='level.dat' in after,
                  regionFiles=len(chunks), changedFiles=len(changed),
                  normalGameExit=launch['exitCode'] == 0 and not launch['stoppedBySmokeTest'],
                  microsoftVerified=launch['accountKind'] == 'microsoft' and launch['accountLogin'] == 'passed',
                  javaMajor=launch['javaMajor'], architecture=launch['javaArchitecture'])
    passed = (exit_code == 0 and result['normalGameExit'] and result['microsoftVerified']
              and starts >= options.visits and stops >= options.visits and chunks and 'level.dat' in changed
              and errors == 0)
    result['requiredVisitsInThisProcess'] = options.visits
    result['status'] = 'automatic_checks_passed' if passed else 'needs_review'
    (output / 'after.json').write_text(json.dumps(after, indent=2) + '\n')
(output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result, indent=2), flush=True)
raise SystemExit(0 if result['status'] == 'automatic_checks_passed' else 1)
