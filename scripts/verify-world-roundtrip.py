"""Launch two user-operated Quick Play sessions and verify the saved world is reused exactly."""
from pathlib import Path
import argparse
import json
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('label')
parser.add_argument('--cli-directory', type=Path, required=True)
options = parser.parse_args()
base = root / 'artifacts/verification-0.18'
case = next(row for row in json.loads((base / 'world-test-instances.json').read_text()) if row['label'] == options.label)
sessions = []
for phase in ('first', 'reentry'):
    label = options.label + '-' + phase
    print('WORLD_PHASE_STARTED ' + label, flush=True)
    command = [sys.executable, str(root / 'scripts/verify-world-session.py'),
               '--version', case['gameVersion'], '--profile', case['profileId'],
               '--game-directory', case['gameDirectory'], '--world', case['world'],
               '--existing-world', '--visits', '1', '--label', label,
               '--cli-directory', str(options.cli_directory)]
    result = subprocess.run(command, cwd=root)
    sessions.append(label)
    if result.returncode:
        raise SystemExit(result.returncode)

# Compare all files before the second launch, not just mtimes or level.dat existence.
after = json.loads((base / 'worlds' / sessions[0] / 'after.json').read_text())
before = json.loads((base / 'worlds' / sessions[1] / 'before.json').read_text())
assert after == before, 'The second process did not receive the first process saved files unchanged.'
report = dict(status='automatic_checks_passed', sessions=sessions, version=case['gameVersion'],
              loader=case['loader'], sameSavedFilesAtRestart=True,
              normalExitBoth=True, worldEnteredBoth=True,
              userVisualAcceptance='pending', reentryMethod='new_game_process_official_quick_play')
(base / 'worlds' / (options.label + '-roundtrip.json')).write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2), flush=True)
