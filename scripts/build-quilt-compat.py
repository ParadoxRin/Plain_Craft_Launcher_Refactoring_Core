"""Build or verify the small Java compatibility bridges with a supplied JDK and Quilt Loader.

The checked-in bytecode targets Java 8. Only the dependency parser API is linked;
no game or mod implementation is included. --check compares reproducible bytes.
"""
import argparse
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('javac', type=Path)
parser.add_argument('quilt_loader', type=Path)
parser.add_argument('--check', action='store_true')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
folder = root / 'src/Pcl.Core/Adapters/Compatibility/Java'
with tempfile.TemporaryDirectory(prefix='pcl-quilt-build-') as temporary:
    subprocess.run([str(args.javac.resolve()), '--release', '8', '-Xlint:-options', '-g:none',
                    '-cp', str(args.quilt_loader.resolve()), '-d', temporary,
                    *map(str, sorted(folder.glob('*.java')))], check=True)
    for compiled in sorted(Path(temporary).glob('*.class')):
        target = folder / compiled.name
        if args.check:
            if target.read_bytes() != compiled.read_bytes():
                raise SystemExit('Bytecode differs from source: ' + compiled.name)
        else:
            target.write_bytes(compiled.read_bytes())
print('Java 8 compatibility bridge bytecode verified' if args.check else 'Java bridges built')
