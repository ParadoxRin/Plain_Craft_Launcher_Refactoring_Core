"""Verify archive boundaries, executable modes, metadata and failure isolation."""
import hashlib
import importlib.util
import json
import pathlib
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('packages', pathlib.Path(__file__).with_name('package-components.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Packages(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.client, self.host = self.root / 'client', self.root / 'host'
        for directory, names in ((self.client, ['PCL-MacPort', 'Pcl.Client.dll', 'Pcl.Contract.dll']),
                                 (self.host, ['Pcl.Host', 'Pcl.Host.dll', 'Pcl.Contract.dll'])):
            directory.mkdir()
            (directory / 'Legal').mkdir()
            (directory / 'Legal/NOTICE.txt').write_text('fixture notice')
            for name in names:
                (directory / name).write_bytes(name.encode())
        (self.host / 'Pcl.Host').chmod(0o755)
        self.probe = {'build': {'runtime': 'osx-arm64', 'version': '0.42.0', 'sequence': 42000, 'dataEpoch': 1},
                      'protocolVersion': 1, 'capabilities': ['desktop.contract-only.v1']}
        self.release = {'version': '0.42.0', 'channel': 'preview'}

    def run_package(self, output='packages'):
        return module.package(self.client, self.host, self.probe, self.release, self.root / output)

    def test_contents_and_integrity(self):
        manifest = self.run_package()
        self.assertEqual(set(manifest['packages']), {'client', 'host'})
        self.assertEqual(manifest, json.loads((self.root / 'packages/release-manifest.json').read_text()))
        for name, part in manifest['packages'].items():
            path = self.root / 'packages' / part['file']
            self.assertEqual(part['size'], path.stat().st_size)
            self.assertEqual(part['sha256'], hashlib.sha256(path.read_bytes()).hexdigest())
            with zipfile.ZipFile(path) as zipped:
                contents = zipped.namelist()
                self.assertIn('Legal/NOTICE.txt', contents)
                if name == 'client':
                    self.assertFalse(any(x.startswith('host/') or x == 'Pcl.Host.dll' for x in contents))
                if name == 'host':
                    self.assertTrue((zipped.getinfo('Pcl.Host').external_attr >> 16) & 0o100)
        self.run_package('second')
        self.assertEqual((self.root / 'packages/release-manifest.json').read_bytes(),
                         (self.root / 'second/release-manifest.json').read_bytes())
        with self.assertRaisesRegex(ValueError, 'OUTPUT_ALREADY_EXISTS'):
            self.run_package()

    def test_reject_mixed_client(self):
        (self.client / 'Pcl.Core.dll').write_bytes(b'forbidden')
        with self.assertRaisesRegex(ValueError, 'CLIENT_CONTAINS_CORE'):
            self.run_package()
        self.assertFalse((self.root / 'packages').exists())
        (self.client / 'Pcl.Core.dll').unlink()
        for name in ('Pcl.Host', 'Pcl.Host.runtimeconfig.json', 'ArchitectureGuard.deps.json'):
            (self.client / name).write_bytes(b'build-only')
            with self.assertRaisesRegex(ValueError, 'CLIENT_CONTAINS_CORE'):
                self.run_package()
            (self.client / name).unlink()

    def test_reject_link(self):
        (self.host / 'private-link').symlink_to(self.root / 'outside')
        with self.assertRaisesRegex(ValueError, 'SYMLINK_NOT_SUPPORTED'):
            self.run_package()

    def test_reject_version_and_overlap(self):
        self.probe['build']['version'] = '0.41.0'
        with self.assertRaisesRegex(ValueError, 'VERSION_MISMATCH'):
            self.run_package()
        with self.assertRaisesRegex(ValueError, 'OUTPUT_MUST_BE_SEPARATE'):
            module.package(self.client, self.host, self.probe, self.release, self.client / 'output')

    def test_release_sequence_and_core_provenance(self):
        self.release.update(hostSequence=42100, coreVersion='0.42.0', coreCommit='a' * 40)
        with self.assertRaisesRegex(ValueError, 'SEQUENCE_MISMATCH'):
            self.run_package()
        self.probe['build']['sequence'] = 42100
        manifest = self.run_package()
        self.assertEqual(set(manifest['packages']), {'client', 'host'})
        self.assertEqual(manifest['core']['commit'], 'a' * 40)
        self.assertEqual(manifest['packages']['host']['build']['sequence'], 42100)

    def test_reject_non_https_asset_url(self):
        with self.assertRaisesRegex(ValueError, 'HTTPS_BASE_URL_REQUIRED'):
            module.package(self.client, self.host, self.probe, self.release, self.root / 'packages', 'http://example.test/assets')

    def test_reject_incompatible_host(self):
        for field, value in (('protocolVersion', 2), ('capabilities', [])):
            old = self.probe[field]
            self.probe[field] = value
            with self.assertRaisesRegex(ValueError, 'INCOMPATIBLE_HOST'):
                self.run_package()
            self.probe[field] = old
        self.probe['build']['dataEpoch'] = 2
        with self.assertRaisesRegex(ValueError, 'INCOMPATIBLE_HOST'):
            self.run_package()


if __name__ == '__main__':
    unittest.main()
