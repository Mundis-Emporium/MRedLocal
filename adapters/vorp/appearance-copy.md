# Copy a VORP character's appearance

The launcher normally opens the character creator. `provision.py` is optional and copies an existing character's appearance before the second profile first joins. It creates a new character with no money, inventory or owned items.

Stop the local server first. Use a MariaDB client config outside the repository, with a loopback host and your local database credentials.

```powershell
python adapters/vorp/provision.py --defaults-extra-file "../local-mariadb.cnf" --database YOUR_DATABASE --source-identifier "steam:YOUR_HEX_ID" --source-character 123 --alias "localtest:secondary" --first-name "Local" --last-name "Tester"
```

This prints SQL without applying it. After checking the source character and database, add `--apply` to run it. The tool refuses changed table layouts and profiles that already contain a character.
