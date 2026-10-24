"""Prepare one fresh local VORP alias account and appearance-only character."""

import argparse
import configparser
import queue
import re
import subprocess
import threading
from pathlib import Path

USER_COLUMNS = (
    "identifier group warnings banned banneduntil char max_jobs".split()
)
CHARACTER_COLUMNS = (
    "identifier steamname charidentifier group money gold rol xp healthouter "
    "healthinner staminaouter staminainner hours LastLogin inventory slots job "
    "joblabel multijobs meta firstname lastname character_desc gender age "
    "nickname skinPlayer compPlayer compTints jobgrade coords status isdead "
    "skills walk gunsmith ammo discordid lastjoined"
).split()
LOOPBACK = {"127.0.0.1", "::1", "localhost"}


class Refused(Exception):
    pass


def validate_config(path):
    path = path.resolve(strict=True)
    content = path.read_text(encoding="utf-8-sig")
    if re.search(r"(?im)^\s*!include(?:dir)?\b", content):
        raise Refused("defaults file includes another file")
    config = configparser.ConfigParser(interpolation=None, strict=True)
    config.read_string(content)
    hosts = [config[section]["host"].strip().strip('"').lower()
             for section in ("client", "mariadb", "mysql")
             if section in config and "host" in config[section]]
    if not hosts or any(host not in LOOPBACK for host in hosts):
        raise Refused("defaults file must specify only a loopback database host")
    return path


def text_value(value, label, maximum=50):
    if not value or len(value) > maximum or any(ord(c) < 32 for c in value):
        raise Refused(f"invalid {label}")
    return value


def validate_args(args):
    if not re.fullmatch(r"[A-Za-z0-9_]{1,64}", args.database):
        raise Refused("invalid database name")
    if not re.fullmatch(r"steam:[0-9A-Fa-f]+", args.source_identifier):
        raise Refused("source identifier must be a real steam: hex identifier")
    if not re.fullmatch(r"localtest:[A-Za-z0-9_]+", args.alias) or len(args.alias) > 50:
        raise Refused("alias must be localtest: followed by letters, numbers, or underscore, at most 50 chars")
    if args.source_character < 1 or args.source_character > 2147483647:
        raise Refused("invalid source character ID")
    text_value(args.first_name, "first name")
    text_value(args.last_name, "last name")


def sql_text(value):
    # Hex literals avoid dependence on the session's quote and backslash modes.
    if value == "":
        return "''"
    return "CONVERT(0x" + value.encode("utf-8").hex() + " USING utf8mb4)"


def parse_columns(output):
    result = {}
    for row in output.splitlines():
        cells = row.split("\t")
        if len(cells) != 6 or cells[0] in result:
            raise Refused("unexpected SHOW COLUMNS output")
        result[cells[0]] = dict(zip(("type", "null", "key", "default", "extra"), cells[1:]))
    return result


def verify_schema(users, characters):
    if list(users) != USER_COLUMNS or list(characters) != CHARACTER_COLUMNS:
        raise Refused("unsupported users or characters column layout")
    if users["identifier"]["key"] != "PRI" or characters["charidentifier"]["key"] != "PRI":
        raise Refused("unsupported primary keys")
    if "auto_increment" not in characters["charidentifier"]["extra"].lower():
        raise Refused("character ID must be auto increment")
    if not re.match(r"^int\b", characters["charidentifier"]["type"].lower()):
        raise Refused("unsupported character ID type")
    for table, column in ((users, "identifier"), (characters, "identifier")):
        if not re.fullmatch(r"varchar\((?:50|60|255)\)", table[column]["type"].lower()):
            raise Refused("unsupported identifier type")
    for column in ("money", "gold", "rol", "xp", "gunsmith"):
        if not re.match(r"^(?:decimal|double|float|int|bigint)\b", characters[column]["type"].lower()):
            raise Refused(f"unsupported currency or experience type: {column}")
    for column in ("inventory", "multijobs", "skills", "coords", "skinPlayer", "compPlayer", "compTints"):
        if not re.match(r"^(?:longtext|mediumtext|text)\b", characters[column]["type"].lower()):
            raise Refused(f"unsupported character data type: {column}")
    if not re.fullmatch(r"varchar\(140\)", characters["status"]["type"].lower()):
        raise Refused("unsupported status type")
    for table, column, expected in ((users, "group", "user"), (characters, "group", "user"),
                                    (characters, "meta", "{}"), (characters, "walk", "noanim")):
        if table[column]["default"] != expected:
            raise Refused(f"unsupported default for {column}")
    for column, expected in (("hours", 0), ("slots", 35), ("jobgrade", 0),
                             ("healthouter", 500), ("healthinner", 100),
                             ("staminaouter", 100), ("staminainner", 100)):
        try:
            if float(characters[column]["default"]) != expected:
                raise Refused(f"unsupported default for {column}")
        except ValueError as exc:
            raise Refused(f"unsupported default for {column}") from exc
    supplied = {"identifier", "steamname", "group", "money", "gold", "rol", "xp", "inventory",
                "job", "joblabel", "multijobs", "firstname", "lastname", "character_desc", "gender",
                "age", "skinPlayer", "compPlayer", "compTints", "coords", "status", "isdead",
                "skills", "gunsmith", "ammo"}
    for column, spec in characters.items():
        if column not in supplied and column != "charidentifier" and spec["null"] == "NO" \
                and spec["default"] == "NULL":
            raise Refused(f"required character column has no supported value: {column}")


def cli_command(args, config_path):
    return [args.mariadb, "--defaults-extra-file=" + str(config_path),
            "--host=127.0.0.1", "--protocol=TCP", "--database=" + args.database,
            "--batch", "--raw", "--skip-column-names", "--unbuffered"]


def cli_query(args, config_path, sql):
    try:
        result = subprocess.run(cli_command(args, config_path), input=sql, capture_output=True, text=True,
                                encoding="utf-8", timeout=30)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise Refused("MariaDB CLI did not complete") from exc
    if result.returncode:
        raise Refused("MariaDB CLI refused the query; inspect the local database log")
    return result.stdout.rstrip("\r\n")


def preflight(args, config_path):
    engines = cli_query(args, config_path,
                        "SELECT TABLE_NAME, ENGINE FROM information_schema.TABLES "
                        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('users','characters') ORDER BY TABLE_NAME;")
    if engines.splitlines() != ["characters\tInnoDB", "users\tInnoDB"]:
        raise Refused("users and characters must both use InnoDB")
    users = parse_columns(cli_query(args, config_path, "SHOW COLUMNS FROM `users`;"))
    characters = parse_columns(cli_query(args, config_path, "SHOW COLUMNS FROM `characters`;"))
    verify_schema(users, characters)
    alias = sql_text(args.alias)
    owner = sql_text(args.source_identifier)
    counts = cli_query(args, config_path,
                       "SELECT (SELECT COUNT(*) FROM `users` WHERE `identifier` = " + alias + "), "
                       "(SELECT COUNT(*) FROM `characters` WHERE `identifier` = " + alias + "), "
                       "(SELECT COUNT(*) FROM `characters` WHERE `identifier` = " + owner +
                       " AND `charidentifier` = " + str(args.source_character) + ");")
    if counts != "0\t0\t1":
        raise Refused("alias already exists or source character is missing or ambiguous")


def make_inserts(args):
    alias = sql_text(args.alias)
    owner = sql_text(args.source_identifier)
    first = sql_text(args.first_name)
    last = sql_text(args.last_name)
    empty = sql_text("{}")
    blank = sql_text("")
    display = sql_text("Local test")
    users = f"""INSERT INTO `users` (`identifier`,`group`,`warnings`,`banned`,`banneduntil`,`char`,`max_jobs`)
SELECT {alias},'user',0,0,0,1,1
FROM `characters`
WHERE `identifier` = {owner} AND `charidentifier` = {args.source_character}
AND NOT EXISTS (SELECT 1 FROM `characters` WHERE `identifier` = {alias});"""
    characters = f"""INSERT INTO `characters` (`identifier`,`steamname`,`group`,`money`,`gold`,`rol`,`xp`,
`inventory`,`job`,`joblabel`,`multijobs`,`firstname`,`lastname`,`character_desc`,
`gender`,`age`,`skinPlayer`,`compPlayer`,`compTints`,`coords`,`status`,`isdead`,
`skills`,`gunsmith`,`ammo`)
SELECT {alias},{display},'user',0,0,0,0,
{empty},'unemployed','Unemployed',{empty},{first},{last},{blank},
`gender`,`age`,`skinPlayer`,`compPlayer`,`compTints`,{empty},{empty},0,
{empty},0,NULL
FROM `characters`
WHERE `identifier` = {owner} AND `charidentifier` = {args.source_character}
AND NOT EXISTS (SELECT 1 FROM `characters` WHERE `identifier` = {alias});"""
    return users, characters


def make_sql(args):
    users, characters = make_inserts(args)
    return "START TRANSACTION;\n" + users + "\n" + characters + "\nCOMMIT;\n"


def apply_transaction(args, config_path):
    users, characters = make_inserts(args)
    alias = sql_text(args.alias)
    owner = sql_text(args.source_identifier)
    try:
        process = subprocess.Popen(cli_command(args, config_path), stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                   text=True, encoding="utf-8", bufsize=1)
    except OSError as exc:
        raise Refused("MariaDB CLI did not start") from exc
    lines = queue.Queue()

    def read_output():
        for line in process.stdout:
            lines.put(line.rstrip("\r\n"))
        lines.put(None)

    threading.Thread(target=read_output, daemon=True).start()

    def step(sql, marker):
        try:
            process.stdin.write(sql + "\n")
            process.stdin.flush()
        except (OSError, BrokenPipeError) as exc:
            raise Refused("MariaDB transaction stopped") from exc
        while True:
            try:
                row = lines.get(timeout=30)
            except queue.Empty as exc:
                raise Refused("MariaDB transaction timed out") from exc
            if row is None:
                raise Refused("MariaDB transaction ended before commit")
            if row.startswith(marker + "\t"):
                return row.split("\t")[1:]

    committed = False
    try:
        # Lock the source row and any current alias rows before the final checks.
        checks = step(
            "START TRANSACTION; "
            "SELECT `charidentifier` FROM `characters` WHERE `identifier` = " + owner +
            " AND `charidentifier` = " + str(args.source_character) + " FOR UPDATE; "
            "SELECT `identifier` FROM `users` WHERE `identifier` = " + alias + " FOR UPDATE; "
            "SELECT `charidentifier` FROM `characters` WHERE `identifier` = " + alias + " FOR UPDATE; "
            "SELECT 'LOCAL_CHECK', (SELECT COUNT(*) FROM `users` WHERE `identifier` = " + alias + "), "
            "(SELECT COUNT(*) FROM `characters` WHERE `identifier` = " + alias + "), "
            "(SELECT COUNT(*) FROM `characters` WHERE `identifier` = " + owner +
            " AND `charidentifier` = " + str(args.source_character) + ");", "LOCAL_CHECK")
        if checks != ["0", "0", "1"]:
            raise Refused("rows changed after preflight")
        if step(users + " SELECT 'LOCAL_USER', ROW_COUNT();", "LOCAL_USER") != ["1"]:
            raise Refused("alias user insert did not create exactly one row")
        if step(characters + " SELECT 'LOCAL_CHARACTER', ROW_COUNT();", "LOCAL_CHARACTER") != ["1"]:
            raise Refused("alias character insert did not create exactly one row")
        verify = step(
            "SELECT 'LOCAL_VERIFY', (SELECT COUNT(*) FROM `users` WHERE `identifier` = " + alias + "), "
            "(SELECT COUNT(*) FROM `characters` WHERE `identifier` = " + alias + "), "
            "(SELECT MIN(`charidentifier`) FROM `characters` WHERE `identifier` = " + alias + ");",
            "LOCAL_VERIFY")
        if len(verify) != 3 or verify[:2] != ["1", "1"] or not verify[2].isdigit():
            raise Refused("transaction verification failed")
        process.stdin.write("COMMIT;\n")
        process.stdin.close()
        if process.wait(timeout=30) != 0:
            raise Refused("MariaDB commit was not confirmed; inspect local database")
        committed = True
        return verify[2]
    finally:
        if not committed and process.poll() is None:
            try:
                process.stdin.write("ROLLBACK;\n")
                process.stdin.close()
                process.wait(timeout=5)
            except (OSError, ValueError, subprocess.TimeoutExpired):
                process.kill()
                process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mariadb", default="mariadb", help="MariaDB CLI executable")
    parser.add_argument("--defaults-extra-file", type=Path, required=True,
                        help="private client config with a loopback host")
    parser.add_argument("--database", required=True)
    parser.add_argument("--source-identifier", required=True, help="owner's real steam: identifier")
    parser.add_argument("--source-character", required=True, type=int, help="existing source character ID")
    parser.add_argument("--alias", required=True, help="new localtest: identifier")
    parser.add_argument("--first-name", required=True)
    parser.add_argument("--last-name", required=True)
    parser.add_argument("--apply", action="store_true", help="execute after all checks; default is dry run")
    args = parser.parse_args()
    try:
        validate_args(args)
        config_path = validate_config(args.defaults_extra_file)
        preflight(args, config_path)
        sql = make_sql(args)
        if not args.apply:
            print("Schema and row checks passed. Dry run SQL follows. No database rows changed.\n")
            print(sql)
            return
        char_id = apply_transaction(args, config_path)
        print("Created separate local alias user and fresh character ID " + char_id)
    except (Refused, OSError, UnicodeError, ValueError, configparser.Error) as exc:
        parser.exit(2, f"refused: {exc}\n")


if __name__ == "__main__":
    main()
