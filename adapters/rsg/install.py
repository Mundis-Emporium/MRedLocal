"""Install or roll back the pinned RSG local identity patch."""

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
MANIFEST = json.loads((HERE / "manifest.json").read_text(encoding="utf-8"))
HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


class Refused(Exception):
    pass


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_paths(target):
    if target.is_symlink() or (getattr(target.lstat(), 'st_file_attributes', 0) & 0x400):
        raise Refused('reparse-point target directory')
    target = target.resolve(strict=True)
    if not target.is_dir():
        raise Refused("--target must be the resource parent directory containing rsg-core and rsg-multicharacter")
    for entry in MANIFEST["files"]:
        rel = Path(entry["path"])
        if rel.is_absolute() or ".." in rel.parts or "\\" in entry["path"]:
            raise Refused("unsafe manifest path")
        path = target / rel
        for part in [path, *path.parents]:
            if part == target.parent:
                break
            if part.exists() and (part.is_symlink() or (getattr(part.lstat(), 'st_file_attributes', 0) & 0x400)):
                raise Refused(f"symlinked target path: {path}")
        if path.exists() and not path.is_file():
            raise Refused(f"target is not a regular file: {path}")
        yield entry, path


def parse_patch():
    lines = (HERE / "rsg.patch").read_text(encoding="utf-8").splitlines(keepends=True)
    sections = {}
    i = 0
    while i < len(lines):
        if not lines[i].startswith("--- ") or i + 1 >= len(lines) or not lines[i + 1].startswith("+++ "):
            raise Refused("malformed patch header")
        old = lines[i][4:].strip()
        new = lines[i + 1][4:].strip()
        if not new.startswith("b/") or (old != "/dev/null" and old != "a/" + new[2:]):
            raise Refused("unexpected patch path")
        rel = new[2:]
        if rel in sections:
            raise Refused("duplicate patch path")
        i += 2
        hunks = []
        while i < len(lines) and lines[i].startswith("@@ "):
            match = HUNK.match(lines[i])
            if not match:
                raise Refused("malformed patch hunk")
            old_start, old_count, new_start, new_count = match.groups()
            i += 1
            body = []
            while i < len(lines) and not lines[i].startswith(("@@ ", "--- ")):
                if not lines[i].startswith((" ", "+", "-")):
                    raise Refused("malformed patch line")
                body.append(lines[i])
                i += 1
            hunks.append((int(old_start), int(old_count or 1), int(new_start), int(new_count or 1), body))
        if not hunks:
            raise Refused("empty patch section")
        sections[rel] = (old == "/dev/null", hunks)
    return sections


def patched(before, is_new, hunks):
    crlf = before is not None and b'\r\n' in before
    original = [] if is_new else before.decode("utf-8").replace('\r\n', '\n').splitlines(keepends=True)
    result = []
    cursor = 0
    for old_start, old_count, new_start, new_count, body in hunks:
        index = old_start - 1 if old_start else 0
        if index < cursor or index > len(original):
            raise Refused("patch hunk offset mismatch")
        result.extend(original[cursor:index])
        if len(result) != (new_start - 1 if new_start else 0):
            raise Refused("patch output offset mismatch")
        consumed = produced = 0
        for line in body:
            tag, content = line[0], line[1:]
            if tag in " -":
                if index + consumed >= len(original) or original[index + consumed] != content:
                    raise Refused("patch context mismatch")
                consumed += 1
            if tag in " +":
                result.append(content)
                produced += 1
        if consumed != old_count or produced != new_count:
            raise Refused("patch hunk length mismatch")
        cursor = index + consumed
    result.extend(original[cursor:])
    result = ''.join(result)
    return (result.replace('\n', '\r\n') if crlf else result).encode('utf-8')


def read_state(target, field):
    state = {}
    for entry, path in safe_paths(target):
        exists = path.is_file()
        data = path.read_bytes() if exists else None
        expected = entry[field]
        if (digest(data) if data is not None else None) != expected:
            raise Refused(f"unsupported or changed file: {entry['path']}")
        state[entry["path"]] = data
    if MANIFEST["required_version"] not in (target / MANIFEST["version_file"]).read_text(encoding="utf-8"):
        raise Refused("unsupported RSG version")
    for entry in MANIFEST.get('read_only', []) if field == 'before_sha256' else []:
        rel = Path(entry['path'])
        if rel.is_absolute() or '..' in rel.parts or '\\' in entry['path']:
            raise Refused('unsafe read-only manifest path')
        path = target / rel
        for part in [path, *path.parents]:
            if part == target.parent:
                break
            if part.exists() and (part.is_symlink() or (getattr(part.lstat(), 'st_file_attributes', 0) & 0x400)):
                raise Refused(f'reparse-point source dependency: {path}')
        if not path.is_file() or digest(path.read_bytes()) != entry['sha256']:
            raise Refused(f"unsupported or changed dependency: {entry['path']}")
    return state


def atomic_write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary = tempfile.mkstemp(prefix=".local-identity-", dir=path.parent)
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def put_all(target, desired, original):
    changed = []
    try:
        for entry, path in safe_paths(target):
            rel = entry["path"]
            changed.append((path, rel))
            if desired[rel] is None:
                path.unlink()
            else:
                atomic_write(path, desired[rel])
    except Exception:
        for path, rel in reversed(changed):
            if original[rel] is None:
                path.unlink(missing_ok=True)
            else:
                atomic_write(path, original[rel])
        raise


def backup_path(target, backup):
    target = target.resolve(strict=True)
    backup = backup.resolve(strict=False)
    if backup == target or target in backup.parents or backup in target.parents:
        raise Refused("backup must be separate from the resource parent directory")
    return backup


def prepare(target):
    original = read_state(target, "before_sha256")
    patch = parse_patch()
    expected = {entry["path"] for entry in MANIFEST["files"]}
    if set(patch) != expected:
        raise Refused("patch and manifest paths differ")
    desired = {}
    for entry in MANIFEST["files"]:
        rel = entry["path"]
        is_new, hunks = patch[rel]
        if is_new != (original[rel] is None):
            raise Refused(f"patch creation mismatch: {rel}")
        desired[rel] = patched(original[rel], is_new, hunks)
        if digest(desired[rel]) != entry["after_sha256"]:
            raise Refused(f"patch output hash mismatch: {rel}")
    return original, desired


def apply(target, backup):
    original, desired = prepare(target)
    backup = backup_path(target, backup)
    if backup.exists():
        raise Refused("backup path already exists")
    backup.mkdir(parents=True)
    try:
        for rel, data in original.items():
            if data is not None:
                atomic_write(backup / rel, data)
        record = {"format": 1, "target": str(target.resolve(strict=True)), "files": MANIFEST["files"]}
        atomic_write(backup / "rollback.json", (json.dumps(record, indent=2) + "\n").encode("utf-8"))
        put_all(target, desired, original)
    except Exception:
        # Keep a completed backup for manual recovery if a write fails.
        raise
    print(f"installed; rollback backup: {backup}")


def rollback(target, backup):
    current = read_state(target, "after_sha256")
    backup = backup_path(target, backup)
    record = json.loads((backup / "rollback.json").read_text(encoding="utf-8"))
    if record.get("format") != 1 or record.get("target") != str(target.resolve(strict=True)) or record.get("files") != MANIFEST["files"]:
        raise Refused("backup manifest does not match target and adapter")
    original = {}
    for entry in MANIFEST["files"]:
        rel = entry["path"]
        data = (backup / rel).read_bytes() if entry["before_sha256"] is not None else None
        if (digest(data) if data is not None else None) != entry["before_sha256"]:
            raise Refused(f"backup file changed: {rel}")
        original[rel] = data
    put_all(target, original, current)
    print("rolled back; backup retained")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("check", "apply", "rollback"))
    parser.add_argument("--target", type=Path, required=True, help="parent directory containing rsg-core and rsg-multicharacter")
    parser.add_argument("--backup", type=Path, help="new backup directory for apply, existing for rollback")
    args = parser.parse_args()
    try:
        if args.action == "check":
            prepare(args.target)
            print("supported RSG baseline; ready to apply")
        else:
            if not args.backup:
                raise Refused("--backup is required for apply and rollback")
            (apply if args.action == "apply" else rollback)(args.target, args.backup)
    except (Refused, OSError, ValueError, UnicodeError) as exc:
        parser.exit(2, f"refused: {exc}\n")


if __name__ == "__main__":
    main()
