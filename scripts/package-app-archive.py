#!/usr/bin/env python3
"""Archive a signed macOS app without losing generic .NET code signatures.

macOS stores signatures of non-Mach-O files in extended attributes. AppleDouble
entries preserve those attributes, while explicit UTF-8 names prevent ditto's
unflagged Unicode paths from becoming mojibake in other ZIP readers.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('app', type=Path)
parser.add_argument('archive', type=Path)
parser.add_argument('--report', type=Path)
args = parser.parse_args()
app = args.app.resolve()
archive = args.archive.resolve()
if app.suffix != '.app' or not (app / 'Contents/Info.plist').is_file():
    raise SystemExit('A complete app bundle is required')
archive.parent.mkdir(parents=True, exist_ok=True)
subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)], check=True)

with tempfile.TemporaryDirectory(prefix='pcl-signed-archive-') as temporary:
    work = Path(temporary)
    raw = work / 'raw.zip'
    normalized = work / 'normalized.zip'
    subprocess.run(['/usr/bin/ditto', '-c', '-k', '--sequesterRsrc', '--keepParent', str(app), str(raw)], check=True)
    fixed_names = 0
    metadata_files = 0
    with zipfile.ZipFile(raw) as source, zipfile.ZipFile(normalized, 'w') as target:
        for entry in source.infolist():
            content = source.read(entry)
            if not entry.flag_bits & 0x800:
                try:
                    name = entry.filename.encode('cp437').decode('utf8')
                except (UnicodeEncodeError, UnicodeDecodeError):
                    name = entry.filename
                if name != entry.filename:
                    entry.filename = name
                    fixed_names += 1
            if entry.filename.startswith('__MACOSX/') and not entry.is_dir():
                metadata_files += 1
            target.writestr(entry, content)
    unpacked = work / 'unpacked'
    subprocess.run(['/usr/bin/ditto', '-x', '-k', str(normalized), str(unpacked)], check=True)
    restored = unpacked / app.name
    subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(restored)], check=True)
    files = [path for path in app.rglob('*') if path.is_file()]
    if any(path.is_symlink() for path in app.rglob('*')):
        raise SystemExit('Symlink bundles require a separate archive verification policy')
    for original in files:
        copy = restored / original.relative_to(app)
        if not copy.is_file() or hashlib.sha256(copy.read_bytes()).digest() != hashlib.sha256(original.read_bytes()).digest():
            raise SystemExit('Restored bundle content mismatch')
        if original.stat().st_mode & 0o777 != copy.stat().st_mode & 0o777:
            raise SystemExit('Restored bundle permissions mismatch')
    # Replace the output only after signature, Unicode paths, contents and modes pass.
    archive.write_bytes(normalized.read_bytes())
report = {
    'status': 'passed',
    'extractedSignatureVerified': True,
    'appFilesVerified': len(files),
    'appleDoubleMetadataFiles': metadata_files,
    'utf8NamesCorrected': fixed_names,
    'sha256': hashlib.sha256(archive.read_bytes()).hexdigest(),
    'extraction': 'macOS ditto / Archive Utility with AppleDouble metadata',
}
if args.report:
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
