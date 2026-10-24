import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ADAPTER = REPO / "adapters" / "vorp"
SCRIPT = ADAPTER / "character_fix.py"
META = json.loads((ADAPTER / "character-manifest.json").read_text(encoding="utf-8"))
SOURCE = os.environ.get("VORP_CHARACTER_SOURCE")


@unittest.skipUnless(SOURCE, "set VORP_CHARACTER_SOURCE to supported vorp_character/server/server.lua")
class CharacterFixTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.target = self.root / "vorp_character"
        self.file = self.target / "server" / "server.lua"
        self.file.parent.mkdir(parents=True)
        self.file.write_bytes(Path(SOURCE).read_bytes())
        self.backup = self.root / "character-backup"

    def tool(self, action, success=True):
        result = subprocess.run([sys.executable, str(SCRIPT), action, "--target", str(self.target),
                                 "--backup", str(self.backup)], text=True, capture_output=True)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
        return result

    def test_apply_reuse_rollback(self):
        original = self.file.read_bytes()
        self.tool("check")
        self.tool("apply")
        lua = shutil.which("lua")
        if lua:
            env = os.environ.copy()
            env["VORP_CHARACTER_ROOT"] = self.target.as_posix()
            test = Path(__file__).with_name("local_character_source_reuse.lua")
            result = subprocess.run([lua, str(test)], text=True, capture_output=True, env=env)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("character source reuse test passed", result.stdout)
        self.tool("rollback")
        self.assertEqual(self.file.read_bytes(), original)

    def test_changed_source_refused(self):
        self.file.write_bytes(self.file.read_bytes() + b"\n-- local change\n")
        before = self.file.read_bytes()
        self.tool("apply", success=False)
        self.assertEqual(self.file.read_bytes(), before)
        self.assertFalse(self.backup.exists())


if __name__ == "__main__":
    unittest.main()
