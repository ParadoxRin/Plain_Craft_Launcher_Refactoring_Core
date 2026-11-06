"""Exact-version licensing regressions using a cache with unrelated packages."""
import importlib.util
import json
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('legal', pathlib.Path(__file__).with_name('prepare-component-legal.py'))
legal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(legal)


class Legal(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.output = self.root / 'Legal'
        self.output.mkdir()
        self.manifest = self.root / 'app.deps.json'
        self.manifest.write_text(json.dumps({'libraries': {'Example/2.0': {'type': 'package'}}}))
        for name in ('example/1.0', 'example/2.0', 'unrelated/8.0'):
            package = self.root / '.cache/nuget' / name
            package.mkdir(parents=True)
            (package / 'package.nuspec').write_text('<package><metadata><license type="expression">MIT</license></metadata></package>')
            (package / 'LICENSE.txt').write_text(name)
        self.package = self.root / '.cache/nuget/example/2.0'

    def collect(self):
        legal.collect_dependencies(self.root, [self.manifest], self.output)

    def test_exact_versions_only_and_repeatable(self):
        self.collect()
        self.assertEqual([p.relative_to(self.output).as_posix() for p in self.output.rglob('LICENSE.txt')],
                         ['Dependencies/example/2.0/LICENSE.txt'])
        first = (self.output / 'dependency-inventory.json').read_bytes()
        self.collect()
        self.assertEqual(first, (self.output / 'dependency-inventory.json').read_bytes())

    def test_nested_declared_license(self):
        (self.package / 'package.nuspec').write_text('<package><metadata><license type="file">legal/terms</license></metadata></package>')
        (self.package / 'legal').mkdir()
        (self.package / 'legal/terms').write_text('license')
        self.collect()
        self.assertTrue((self.output / 'Dependencies/example/2.0/legal/terms').is_file())

    def test_missing_exact_dependency_fails(self):
        (self.package / 'package.nuspec').unlink()
        with self.assertRaisesRegex(ValueError, 'EXACT_DEPENDENCY_METADATA_MISSING'):
            self.collect()

    def test_unsafe_declared_license_fails(self):
        (self.package / 'package.nuspec').write_text('<package><metadata><license type="file">../../../../app.deps.json</license></metadata></package>')
        with self.assertRaisesRegex(ValueError, 'DECLARED_LICENSE_FILE_MISSING_OR_UNSAFE'):
            self.collect()

    def test_runtimepack_uses_exact_runtime_package(self):
        self.manifest.write_text(json.dumps({'libraries': {'runtimepack.Example/2.0': {'type': 'runtimepack'}}}))
        self.collect()
        self.assertTrue((self.output / 'Dependencies/example/2.0/LICENSE.txt').is_file())

    def test_manifest_required(self):
        with self.assertRaisesRegex(ValueError, 'PUBLISHED_DEPENDENCY_MANIFEST_REQUIRED'):
            legal.collect_dependencies(self.root, [], self.output)


if __name__ == '__main__':
    unittest.main()
