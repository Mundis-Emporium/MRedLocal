"""Install or roll back the pinned vorp_character source reuse cleanup."""

import argparse
import json
from pathlib import Path

from install import Refused, atomic_write, digest, parse_patch, patched

HERE = Path(__file__).resolve().parent
META = json.loads((HERE / "character-manifest.json").read_text(encoding="utf-8"))


def locations(target, backup=None):
    target = target.resolve(strict=True)
    if target.name != META["resource"] or not target.is_dir():
        raise Refused("target must be a vorp_character resource directory")
    file = target / META["path"]
    if file.is_symlink() or file.parent.is_symlink():
        raise Refused("symlinked character source refused")
    if backup is not None:
        backup = backup.resolve(strict=False)
        if backup == target or target in backup.parents or backup in target.parents:
            raise Refused("backup must be outside vorp_character")
    return target, file, backup


def expected_patch(original):
    sections = parse_patch(HERE / "vorp-character-source-reuse.patch")
    if set(sections) != {META["path"]}:
        raise Refused("unexpected character patch paths")
    created, hunks = sections[META["path"]]
    if created:
        raise Refused("character patch cannot create a file")
    result = patched(original, created, hunks)
    if digest(result) != META["after_sha256"]:
        raise Refused("character patch output hash mismatch")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("check", "apply", "rollback"))
    parser.add_argument("--target", type=Path, required=True)
    parser.add_argument("--backup", type=Path)
    args = parser.parse_args()
    try:
        target, file, backup = locations(args.target, args.backup)
        if args.action != "check" and backup is None:
            raise Refused("--backup is required for apply and rollback")
        data = file.read_bytes()
        if args.action in ("check", "apply"):
            if digest(data) != META["before_sha256"]:
                raise Refused("unsupported or changed vorp_character/server/server.lua")
            result = expected_patch(data)
            if args.action == "check":
                print("supported vorp_character source; ready to apply")
                return
            if backup.exists():
                raise Refused("backup directory already exists")
            backup.mkdir(parents=True)
            atomic_write(backup / "server.lua.before", data)
            record = {"target": str(target), "manifest": META}
            atomic_write(backup / "rollback.json", (json.dumps(record, indent=2) + "\n").encode())
            atomic_write(file, result)
            print(f"character cleanup installed; backup: {backup}")
            return
        if digest(data) != META["after_sha256"]:
            raise Refused("installed character source changed; refusing rollback")
        record = json.loads((backup / "rollback.json").read_text(encoding="utf-8"))
        if record != {"target": str(target), "manifest": META}:
            raise Refused("character backup manifest does not match target")
        original = (backup / "server.lua.before").read_bytes()
        if digest(original) != META["before_sha256"]:
            raise Refused("character backup file changed")
        atomic_write(file, original)
        print("character cleanup rolled back; backup retained")
    except (Refused, OSError, ValueError, UnicodeError) as exc:
        parser.exit(2, f"refused: {exc}\n")


if __name__ == "__main__":
    main()
