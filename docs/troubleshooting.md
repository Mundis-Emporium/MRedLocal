# Troubleshooting

## Second client will not open

Finish the second RedM installation's first download and sign-in. Check its RDR2 path and select its `RedM.exe` in **Installation**.

After a startup timeout, close the second copy and use **Restore main settings** before trying again.

## Graphics backup needs restoring

Close the second game, then use **Installation → Restore main settings**.

If settings changed during the session, the launcher asks before replacing them. Compare the current file with `system.xml.primary.backup` in **Open backup folder**. Keep any changes you need before choosing **Restore saved settings**. Keep the backup until restoration succeeds.

## Saved settings cannot be read

The launcher keeps the unreadable file with an `.invalid-` suffix and loads defaults. Find it through **Installation → Open local settings folder**.

## Server does not match setup

Start the server selected in **Server setup**. Restart it after applying setup. Another server using the same port, or a config that does not load the setup include, will fail this check.

## Second player is rejected or loads the wrong character

Use `127.0.0.1` to join the main client first, then launch the second. Both connections must come from your PC. A third connection is refused.

The second character needs a separate framework profile. Removing a duplicate-account check on its own can make both clients share inventory or saves. Scripts that use Steam or license IDs directly may also share data.

## Server setup is blocked

Stop FXServer before setting up or removing the adapter. Only the [listed framework builds](frameworks.md) are accepted. Modified files need a matching adapter; do not force a patch over them.

## Second client uses too much memory

Lower its graphics settings and resolution, and enable **Give the main client priority**. Both clients still load the game and streamed assets.
