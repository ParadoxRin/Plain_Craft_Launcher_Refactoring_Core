"""Regression checks for developer command failures and isolated data ownership."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ENTRY = Path(__file__).resolve().parents[1] / 'dev.py'
spec = importlib.util.spec_from_file_location('pcl_dev', ENTRY)
dev = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dev)


class EntryTests(unittest.TestCase):
    def test_invalid_explicit_sdk_does_not_fall_back(self):
        env = dict(os.environ, DOTNET='/missing/pcl-sdk-for-test/dotnet')
        result = subprocess.run([sys.executable, str(ENTRY), 'setup'], env=env,
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('DOTNET', result.stderr)

    def test_subprocess_failure_propagates(self):
        with self.assertRaises(subprocess.CalledProcessError) as failure:
            dev.run([sys.executable, '-c', 'raise SystemExit(7)'])
        self.assertEqual(failure.exception.returncode, 7)

    def test_fresh_roots_never_reuse_or_remove_data(self):
        with tempfile.TemporaryDirectory(prefix='pcl dev space ') as directory:
            with patch.object(dev, 'ROOT', Path(directory)):
                first = dev.fresh('core')
                marker = first / 'keep.txt'
                marker.write_text('existing data')
                second = dev.fresh('core')
                self.assertNotEqual(first, second)
                self.assertEqual(marker.read_text(), 'existing data')
                self.assertFalse((second / 'data').exists())
                self.assertTrue(second.is_absolute())

    def test_argument_paths_with_spaces_are_not_shell_parsed(self):
        value = 'path with spaces; literal $value'
        dev.run([sys.executable, '-c', 'import sys; assert sys.argv[1] == ' + repr(value), value])

    def test_quick_stops_at_first_failure_without_sdk(self):
        with patch.object(sys, 'argv', ['dev.py', 'test', 'quick']), \
             patch.object(dev, 'sdk', side_effect=AssertionError('quick must not resolve SDK')), \
             patch.object(dev, 'run', side_effect=subprocess.CalledProcessError(9, ['fixture'])) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                dev.main()
            self.assertEqual(run.call_count, 1)

    def test_host_diagnostics_do_not_pollute_protocol_stdout(self):
        stdout = io.StringIO()
        with patch.object(sys, 'argv', ['dev.py', 'host']), \
             patch.object(dev, 'sdk', return_value=('dotnet', '10.0.402')), \
             patch.object(dev, 'build'), patch.object(dev.subprocess, 'run'), \
             tempfile.TemporaryDirectory() as directory, \
             patch.object(dev, 'ROOT', Path(directory)), contextlib.redirect_stdout(stdout):
            dev.main()
        self.assertEqual(stdout.getvalue(), '')


if __name__ == '__main__':
    unittest.main()
