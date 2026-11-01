"""Attach source notices and dependency licenses to each component."""
import pathlib
import shutil
import sys

root = pathlib.Path(sys.argv[1])
notices = [root / name for name in ('PCL-LICENCE.txt', 'LICENSE.md', 'LICENSE-MIT.txt')]
notices += list((root / 'docs').glob('*-NOTICE.txt'))
notices += list((root / 'docs').glob('*-LICENSE.txt'))
notices += [root / 'docs/THIRD-PARTY-NOTICES.md']
for argument in sys.argv[2:]:
    directory = pathlib.Path(argument)
    legal = directory / 'Legal'
    legal.mkdir()
    for path in notices:
        shutil.copy2(path, legal / path.name)
    shutil.copy2(root / 'vendor/MeloongCore/LICENSE.txt', legal / 'MeloongCore-LICENSE.txt')
    for package in (root / '.cache/nuget').glob('*/*'):
        for path in package.iterdir():
            if path.is_file() and ('license' in path.name.lower() or 'notice' in path.name.lower()) and path.suffix.lower() in ('.txt', '.md', ''):
                destination = legal / 'Dependencies' / package.parent.name / package.name
                destination.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, destination / path.name)
    shutil.copy2(root / 'release.json', directory / 'release.json')
