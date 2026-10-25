"""Verify the built self-contained app and render its own visual tree for manual UI review."""
from pathlib import Path
import json, os, shutil, subprocess, zipfile
root = Path(__file__).resolve().parent.parent
artifacts = root / 'artifacts'
archive = artifacts / 'PCL-MacPort-Preview-arm64.zip'
with zipfile.ZipFile(archive) as package:
    assert package.testzip() is None
extracted = artifacts / 'resource-extracted-check'
extracted.mkdir(exist_ok=True)
subprocess.run(['/usr/bin/ditto', '-x', '-k', str(archive), str(extracted)], check=True)
app = extracted / 'PCL Mac Port Preview.app'
subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)], check=True)
env = os.environ.copy()
env.pop('DOTNET_ROOT', None)
env.pop('DOTNET_ROOT_ARM64', None)
for mode, name in [('--smoke-versions', 'versions-ui'), ('--smoke-modpacks', 'modpack-ui'), ('--smoke-resources', 'resource-ui'), ('--smoke-managed', 'managed-ui'), ('--smoke-ui', 'home-ui'), ('--smoke-modpacks', 'fresh-modpack-ui'), ('--smoke-accounts', 'account-ui'), ('--smoke-microsoft-ui', 'microsoft-ui')]:
    data = root / ('artifacts/account-ui-data' if name in ('account-ui', 'microsoft-ui') else 'artifacts/fresh-ui-data' if name == 'fresh-modpack-ui' else 'runtime/apple-silicon-1.21.1')
    with (artifacts / (name + '.log')).open('w') as log:
        subprocess.run([str(app / 'Contents/MacOS/PCL-MacPort'), mode, '--data-root', str(data)],
                       check=True, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90)
    evidence = data / 'evidence'
    for source, suffix in [('modern-ui-render.png', '.png'), ('desktop-result.json', '.json')]:
        shutil.copy2(evidence / source, artifacts / 'evidence' / (name + suffix))
    if name == 'versions-ui':
        for filename in ('version-manager.png', 'version-ui-flow.json'): shutil.copy2(evidence / filename, artifacts / 'evidence' / filename)
    if name == 'account-ui': shutil.copy2(evidence / 'account-ui-flow.json', artifacts / 'evidence' / 'account-ui-flow.json')
    print('VERIFIED', name, flush=True)
(artifacts / 'evidence/resource-package-check.json').write_text(json.dumps({
    'zip_crc': 'passed', 'signature_after_extraction': 'passed',
    'self_contained_extracted_app': 'opened_without_DOTNET_ROOT',
    'screens': ['versions-ui', 'version-manager', 'modpack-ui', 'resource-ui', 'managed-ui', 'home-ui', 'fresh-modpack-ui', 'account-ui', 'microsoft-ui'],
    'visual_inspection': 'requires_manual_review_of_generated_images'
}, indent=2) + '\n')
