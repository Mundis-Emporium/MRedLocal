import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ADAPTER = REPO / 'adapters/rsg'
INSTALLER = ADAPTER / 'install.py'
MANIFEST = json.loads((ADAPTER / 'manifest.json').read_text(encoding='utf-8'))
BASELINE = os.environ.get('RSG_BASELINE_DIR')


@unittest.skipUnless(BASELINE, 'set RSG_BASELINE_DIR to the unmodified [framework] directory')
class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.target = Path(self.temp.name) / '[framework]'
        self.target.mkdir()
        self.backup = Path(self.temp.name) / 'backup'
        for entry in MANIFEST['files'] + MANIFEST['read_only']:
            expected = entry.get('before_sha256', entry.get('sha256'))
            if expected is None:
                continue
            data = (Path(BASELINE) / entry['path']).read_bytes()
            self.assertEqual(hashlib.sha256(data).hexdigest(), expected, entry['path'])
            path = self.target / entry['path']
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)

    def run_tool(self, action, success=True):
        result = subprocess.run([sys.executable, str(INSTALLER), action, '--target', str(self.target),
                                 '--backup', str(self.backup)], capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)

    def snapshot(self):
        return {entry['path']: (self.target / entry['path']).read_bytes()
                if (self.target / entry['path']).exists() else None
                for entry in MANIFEST['files'] + MANIFEST['read_only']}

    def test_roundtrip_and_actual_source_lua(self):
        before = self.snapshot()
        self.run_tool('check')
        self.run_tool('apply')
        lua = shutil.which('lua')
        self.assertIsNotNone(lua, 'Lua 5.4 is required for actual-source verification')
        env = os.environ.copy()
        env['RSG_TEST_ROOT'] = self.target.as_posix()
        result = subprocess.run([lua, str(Path(__file__).with_name('local_peer_identity.lua'))],
                                capture_output=True, text=True, env=env)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('RSG actual-source identity and inventory tests passed', result.stdout)
        self.run_tool('rollback')
        self.assertEqual(self.snapshot(), before)

    def test_changed_baseline_refused_without_writes(self):
        path = self.target / 'rsg-core/server/player.lua'
        path.write_bytes(path.read_bytes() + b'\n-- changed\n')
        before = self.snapshot()
        self.run_tool('apply', False)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse(self.backup.exists())

    def test_resource_parent_may_have_another_name(self):
        renamed = self.target.with_name('resources')
        self.target.rename(renamed)
        self.target = renamed
        self.run_tool('check')
        self.run_tool('apply')
        self.run_tool('rollback')

    def test_unsupported_inventory_refused_without_writes(self):
        path = self.target / 'rsg-inventory/server/exports.lua'
        path.write_bytes(path.read_bytes() + b'\n-- changed\n')
        before = self.snapshot()
        self.run_tool('apply', False)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse(self.backup.exists())

    def test_existing_module_refused_without_writes(self):
        path = self.target / 'rsg-core/server/local_sessions/identity.lua'
        path.parent.mkdir()
        path.write_text('existing source', encoding='utf-8')
        before = self.snapshot()
        self.run_tool('apply', False)
        self.assertEqual(self.snapshot(), before)

    def test_changed_install_refuses_rollback(self):
        self.run_tool('apply')
        path = self.target / 'rsg-multicharacter/server/main.lua'
        path.write_bytes(path.read_bytes() + b'\n-- changed\n')
        before = self.snapshot()
        self.run_tool('rollback', False)
        self.assertEqual(self.snapshot(), before)

    def test_corrupt_backup_refuses_rollback(self):
        self.run_tool('apply')
        (self.backup / 'rsg-core/server/events.lua').write_bytes(b'corrupt')
        before = self.snapshot()
        self.run_tool('rollback', False)
        self.assertEqual(self.snapshot(), before)

    def test_rollback_preserves_changed_unmodified_dependency(self):
        before = self.snapshot()
        self.run_tool('apply')
        path = self.target / 'rsg-inventory/server/exports.lua'
        changed = path.read_bytes() + b'\n-- customer change\n'
        path.write_bytes(changed)
        self.run_tool('rollback')
        before['rsg-inventory/server/exports.lua'] = changed
        self.assertEqual(self.snapshot(), before)


if __name__ == '__main__':
    unittest.main()
