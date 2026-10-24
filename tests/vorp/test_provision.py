import argparse
import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

MODULE = Path(__file__).resolve().parents[2] / "adapters" / "vorp" / "provision.py"
spec = importlib.util.spec_from_file_location("vorp_provision", MODULE)
provision = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provision)


def args(**overrides):
    values = dict(database="vorp_test", source_identifier="steam:abcdef0123",
                  source_character=31, alias="localtest:secondary",
                  first_name="New", last_name="Tester", mariadb="mariadb")
    values.update(overrides)
    return argparse.Namespace(**values)


def columns(names, primary):
    result = {}
    for name in names:
        result[name] = dict(type="varchar(50)" if name == "identifier" else "int(11)",
                            null="YES", key="PRI" if name == primary else "",
                            default="0", extra="auto_increment" if name == "charidentifier" else "")
    for name in ("money", "gold", "rol", "xp", "gunsmith"):
        if name in result:
            result[name]["type"] = "double(11,2)"
    for name in ("inventory", "multijobs", "skills", "status", "coords", "skinPlayer", "compPlayer", "compTints"):
        if name in result:
            result[name]["type"] = "longtext"
    if "status" in result:
        result["status"]["type"] = "varchar(140)"
    if "group" in result:
        result["group"]["default"] = "user"
    if "meta" in result:
        result["meta"]["default"] = "{}"
    if "walk" in result:
        result["walk"]["default"] = "noanim"
    if "slots" in result:
        result["slots"]["default"] = "35.0"
    for name, value in (("healthouter", "500"), ("healthinner", "100"),
                        ("staminaouter", "100"), ("staminainner", "100")):
        if name in result:
            result[name]["default"] = value
    return result


class ProvisionTests(unittest.TestCase):
    def test_supported_schema_and_generated_sql(self):
        users = columns(provision.USER_COLUMNS, "identifier")
        chars = columns(provision.CHARACTER_COLUMNS, "charidentifier")
        provision.verify_schema(users, chars)
        sql = provision.make_sql(args(first_name="O'Brien"))
        self.assertIn("START TRANSACTION;", sql)
        self.assertIn("COMMIT;", sql)
        self.assertIn("`skinPlayer`,`compPlayer`,`compTints`", sql)
        self.assertIn("`inventory`", sql)
        self.assertIn("CONVERT(0x7b7d USING utf8mb4)", sql)
        self.assertIn(",'',", sql)
        self.assertNotIn("0x USING", sql)
        self.assertNotIn("O'Brien", sql)
        self.assertNotIn("`charidentifier`) SELECT", sql)
        self.assertIn("AND NOT EXISTS", sql)

    def test_unsupported_schema_refused(self):
        users = columns(provision.USER_COLUMNS, "identifier")
        chars = columns(provision.CHARACTER_COLUMNS, "charidentifier")
        chars["charidentifier"]["extra"] = ""
        with self.assertRaises(provision.Refused):
            provision.verify_schema(users, chars)
        chars["charidentifier"]["extra"] = "auto_increment"
        chars["owned_assets"] = dict(type="longtext", null="NO", key="", default="NULL", extra="")
        with self.assertRaises(provision.Refused):
            provision.verify_schema(users, chars)

    def test_invalid_identifiers_refused(self):
        for changed in ({"database": "prod;DROP"}, {"alias": "steam:shared"},
                        {"source_identifier": "license:wrong"}, {"source_character": 0},
                        {"first_name": "bad\nname"}):
            with self.subTest(changed=changed), self.assertRaises(provision.Refused):
                provision.validate_args(args(**changed))

    def test_private_defaults_must_be_loopback(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "client.cnf"
            path.write_text("[client]\nhost=127.0.0.1\nuser=local\npassword=private\n", encoding="utf-8")
            self.assertEqual(provision.validate_config(path), path.resolve())
            path.write_text("[client]\nhost=db.example.net\n", encoding="utf-8")
            with self.assertRaises(provision.Refused):
                provision.validate_config(path)
            path.write_text("[client]\nhost=localhost\n!include /other.cnf\n", encoding="utf-8")
            with self.assertRaises(provision.Refused):
                provision.validate_config(path)

    def test_preflight_checks_schema_and_rows(self):
        users = columns(provision.USER_COLUMNS, "identifier")
        chars = columns(provision.CHARACTER_COLUMNS, "charidentifier")

        def show(data):
            return "\n".join("\t".join([name, field["type"], field["null"],
                                          field["key"], field["default"], field["extra"]])
                             for name, field in data.items())

        with patch.object(provision, "cli_query", side_effect=["characters\tInnoDB\nusers\tInnoDB", show(users), show(chars), "0\t0\t1"]):
            provision.preflight(args(), Path("unused"))
        with patch.object(provision, "cli_query", side_effect=["characters\tInnoDB\nusers\tInnoDB", show(users), show(chars), "1\t0\t1"]):
            with self.assertRaises(provision.Refused):
                provision.preflight(args(), Path("unused"))
        with patch.object(provision, "cli_query", return_value="characters\tMyISAM\nusers\tInnoDB"):
            with self.assertRaises(provision.Refused):
                provision.preflight(args(), Path("unused"))


if __name__ == "__main__":
    unittest.main()
