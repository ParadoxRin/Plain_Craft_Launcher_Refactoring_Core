"""Exercise baseline/overlay rejection against an isolated Git repository."""
import hashlib
import json
import pathlib
import shutil
import subprocess
import tempfile
import unittest

class BaselineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.file = self.root / 'src/Pcl.Core/McmodCatalog.cs'
        self.file.parent.mkdir(parents=True)
        self.file.write_text('baseline\n')
        (self.root / 'scripts').mkdir()
        shutil.copy2(pathlib.Path(__file__).with_name('verify-core-baseline.py'), self.root / 'scripts')
        (self.root / 'release.json').write_text('{"version":"0.42.0"}')
        self.git('init', '-q')
        self.git('add', '.')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'baseline')
        self.baseline = self.git('rev-parse', 'HEAD').strip()
        self.release = {'version': '0.42.1', 'coreVersion': '0.42.0', 'coreCommit': self.baseline}
        self.save()

    def tearDown(self): self.temp.cleanup()
    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], text=True)
    def save(self): (self.root / 'release.json').write_text(json.dumps(self.release))
    def check(self, expected, *args):
        result = subprocess.run(['python3', str(self.root / 'scripts/verify-core-baseline.py'), *args], capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, expected, result.stdout + result.stderr)
    def overlay(self):
        self.file.write_text('integrated\n')
        self.release['contentIntegration'] = {'src/Pcl.Core/McmodCatalog.cs': {
            'baselineSha256': hashlib.sha256(b'baseline\n').hexdigest(),
            'sha256': hashlib.sha256(b'integrated\n').hexdigest()}}
        self.save()

    def test_original_and_pinned_overlay(self):
        self.check(True)
        self.overlay()
        self.check(True)
        self.file.write_text('unexpected mutation\n')
        self.check(False)

    def test_base_hash_and_untracked_core(self):
        self.overlay()
        self.release['contentIntegration']['src/Pcl.Core/McmodCatalog.cs']['baselineSha256'] = '0' * 64
        self.save()
        self.check(False)
        self.overlay()
        (self.file.parent / 'Unexpected.cs').write_text('untracked')
        self.check(False)

    def test_new_upstream_core_requires_rebase(self):
        self.file.write_text('new upstream\n')
        self.git('add', 'src')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'upstream')
        self.overlay()
        self.check(True)
        self.check(False, '--upstream-ref', 'HEAD')

if __name__ == '__main__': unittest.main()
