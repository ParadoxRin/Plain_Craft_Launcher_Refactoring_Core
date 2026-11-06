"""Exercise runtime boundary and deployment identity for developer Host packages."""
import importlib.util
import json
import pathlib
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('host_package', pathlib.Path(__file__).with_name('package-host.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class HostPackages(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.host = self.root / 'host'
        self.host.mkdir()
        (self.host / 'Legal').mkdir()
        (self.host / 'Legal/NOTICE.txt').write_text('fixture license')
        for name in ('Pcl.Host', 'Pcl.Host.dll', 'Pcl.Core.dll'):
            (self.host / name).write_text('fixture')
        (self.host / 'Pcl.Host').chmod(0o755)
        self.release = self.root / 'release.json'
        self.release.write_text(json.dumps(dict(version='0.43.1', hostSequence=43001, channel='stable', coreVersion='0.43.1', coreCommit='a'*40)))
        (self.root / 'host-probe.json').write_text(json.dumps(dict(build=dict(version='0.43.1',sequence=43001,runtime='osx-arm64'),protocolVersion=1,capabilities=[])))
        self.framework = dict(name='Microsoft.NETCore.App',version='10.0.0')
        (self.host / 'Pcl.Host.runtimeconfig.json').write_text(json.dumps(dict(runtimeOptions=dict(framework=self.framework))))
        (self.host / 'Pcl.Host.deps.json').write_text(json.dumps(dict(libraries={})))

    def package(self, mode='framework-dependent'):
        return module.package(self.root, self.release, 'osx-arm64', mode)

    def test_light_package_retains_dependencies_and_declares_runtime(self):
        item = self.package()['packages']['host']
        self.assertEqual(item['runtimeRequirements'], [self.framework])
        self.assertEqual(item['deployment'], 'framework-dependent')
        self.assertIn('-host-framework-dependent.zip', item['file'])
        with zipfile.ZipFile(self.root / 'packages' / item['file']) as z:
            self.assertIn('Pcl.Core.dll', z.namelist())
            self.assertIn('Legal/NOTICE.txt', z.namelist())
            self.assertIn(b'dotnet Pcl.Host.dll', z.read('README.txt'))
        self.assertIn(item['sha256'], (self.root / 'packages/SHA256SUMS').read_text())

    def test_runtime_files_rejected(self):
        (self.host / 'System.Private.CoreLib.dll').write_bytes(b'runtime')
        with self.assertRaisesRegex(ValueError, 'CONTAINS_RUNTIME'): self.package()

    def test_runtimepack_metadata_rejected(self):
        (self.host / 'Pcl.Host.deps.json').write_text(json.dumps(dict(libraries={'runtime/10.0.0':{'type':'runtimepack'}})))
        with self.assertRaisesRegex(ValueError, 'CONTAINS_RUNTIME'): self.package()

    def test_deployment_mismatch_rejected(self):
        with self.assertRaisesRegex(ValueError, 'SELF_CONTAINED_CONFIG_REQUIRED'): self.package('self-contained')
        (self.host / 'Pcl.Host.runtimeconfig.json').write_text(json.dumps(dict(runtimeOptions=dict(includedFrameworks=[self.framework]))))
        with self.assertRaisesRegex(ValueError, 'FRAMEWORK_DEPENDENT_CONFIG_REQUIRED'): self.package()
        item = self.package('self-contained')['packages']['host']
        self.assertEqual(item['file'], 'pcl-0.43.1-osx-arm64-host.zip')
        self.assertEqual(item['runtimeRequirements'], [])

    def test_identity_and_missing_config_rejected(self):
        with self.assertRaisesRegex(ValueError, 'HOST_RELEASE_IDENTITY_MISMATCH'):
            module.package(self.root, self.release, 'win-x64', 'framework-dependent')
        (self.host / 'Pcl.Host.runtimeconfig.json').unlink()
        with self.assertRaisesRegex(ValueError, 'HOST_PUBLICATION_INCOMPLETE'): self.package()


if __name__ == '__main__': unittest.main()
