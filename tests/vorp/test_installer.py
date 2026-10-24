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
ADAPTER = REPO / "adapters" / "vorp"
INSTALLER = ADAPTER / "install.py"
MANIFEST = json.loads((ADAPTER / "manifest.json").read_text(encoding="utf-8"))
BASELINE = os.environ.get("VORP_BASELINE_DIR")


def sha(data):
    return hashlib.sha256(data).hexdigest()


@unittest.skipUnless(BASELINE, "set VORP_BASELINE_DIR to the saved, unmodified VORP 3.3 source baseline")
class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.target = self.root / "vorp_core"
        self.target.mkdir()
        self.backup = self.root / "backup"
        for entry in MANIFEST["files"]:
            if entry["before_sha256"] is None:
                continue
            rel = entry["path"]
            data = (Path(BASELINE) / rel).read_bytes()
            self.assertEqual(sha(data), entry["before_sha256"], f"incorrect baseline: {rel}")
            dest = self.target / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(data)
        user_class = os.environ.get("VORP_USER_CLASS")
        if user_class:
            dest = self.target / "server" / "class" / "user.lua"
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(Path(user_class).read_bytes())

    def run_tool(self, action, success=True):
        proc = subprocess.run(
            [sys.executable, str(INSTALLER), action, "--target", str(self.target), "--backup", str(self.backup)],
            text=True, capture_output=True,
        )
        self.assertEqual(proc.returncode == 0, success, proc.stdout + proc.stderr)
        return proc

    def snapshot(self):
        return {entry["path"]: (self.target / entry["path"]).read_bytes()
                if (self.target / entry["path"]).exists() else None
                for entry in MANIFEST["files"]}

    def test_apply_rollback_and_identity_sources(self):
        before = self.snapshot()
        self.run_tool("check")
        self.run_tool("apply")
        after = self.snapshot()
        for entry in MANIFEST["files"]:
            self.assertEqual(sha(after[entry["path"]]), entry["after_sha256"])
        self.assertTrue((self.backup / "rollback.json").is_file())
        lua = shutil.which("lua")
        if lua:
            fixture = Path(__file__).with_name("local_peer_identity.lua")
            env = os.environ.copy()
            env["VORP_TEST_ROOT"] = self.target.as_posix()
            proc = subprocess.run([lua, str(fixture)], capture_output=True, text=True, env=env)
            self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
            self.assertIn("local peer identity tests passed", proc.stdout)
            if os.environ.get("VORP_USER_CLASS"):
                creator = Path(__file__).with_name("local_character_creator.lua")
                proc = subprocess.run([lua, str(creator)], capture_output=True, text=True, env=env)
                self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
                self.assertIn("normal VORP character creator identity test passed", proc.stdout)
        self.run_tool("rollback")
        self.assertEqual(self.snapshot(), before)

    def test_unknown_or_modified_baseline_refused_without_writes(self):
        (self.target / "server" / "whitelist.lua").write_bytes(b"changed")
        before = self.snapshot()
        self.run_tool("apply", success=False)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse(self.backup.exists())

    def test_existing_new_module_refused(self):
        module = self.target / "server" / "local_sessions" / "identity.lua"
        module.parent.mkdir()
        module.write_text("existing source", encoding="utf-8")
        before = self.snapshot()
        self.run_tool("apply", success=False)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse(self.backup.exists())

    def test_changed_install_refuses_rollback(self):
        self.run_tool("apply")
        path = self.target / "server" / "loadusers.lua"
        path.write_bytes(path.read_bytes() + b"\n-- changed\n")
        before = self.snapshot()
        self.run_tool("rollback", success=False)
        self.assertEqual(self.snapshot(), before)

    def test_corrupt_backup_refuses_rollback(self):
        self.run_tool("apply")
        backup_file = self.backup / "server" / "bans.lua"
        backup_file.write_bytes(b"corrupt")
        before = self.snapshot()
        self.run_tool("rollback", success=False)
        self.assertEqual(self.snapshot(), before)


if __name__ == "__main__":
    unittest.main()
