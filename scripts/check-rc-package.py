"""Check a manual-update release application before public distribution; never print secrets."""
import argparse, json, pathlib, plistlib
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('app', type=pathlib.Path)
a = p.parse_args()
root = pathlib.Path(__file__).resolve().parent.parent
release = json.loads((root/'release.json').read_text())
assert release['channel'] in ('rc', 'stable') and release['automaticUpdates'] is False
bundle = a.app/'Contents'
info = plistlib.loads((bundle/'Info.plist').read_bytes())
assert info['CFBundleShortVersionString'] == release['version']
assert info['CFBundleVersion'] == str(release['build'])
assert json.loads((bundle/'Resources/release.json').read_text()) == release
for name in ('PCL-MacPort', 'host/Pcl.Host'):
    path = bundle/'MacOS'/name
    assert path.is_file() and path.stat().st_mode & 0o111, name
assert b'Pcl.DetachedLog' in (bundle/'MacOS/host/Pcl.Core.dll').read_bytes(), 'MISSING_EMBEDDED_LOG_HELPER'
for path in bundle.rglob('*'):
    assert path.name not in ('host-update-policy.json', 'client-update-policy.json', 'content-update-policy.json', 'app-update-policy.json'), 'UNEXPECTED_UPDATE_POLICY'
    assert path.suffix not in ('.pdb', '.pem', '.key'), 'UNEXPECTED_SYMBOL_OR_KEY'
print('RELEASE_PACKAGE_CHECK_PASSED: version/build, executable layout, manual updates, no bundled policies/keys/symbols')
