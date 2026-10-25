# ModSync

ModSync checks a connecting player's installed mods against the server's mod
policy. It can identify missing, outdated, or unapproved mods and offer the
player a confirmation dialog to synchronize approved Thunderstore packages.

Both the server and each player must install the same ModSync version. The
server must also run BepInEx and provide the approved package policy.

## Server policy

After the first server start, configure packages under:

- `BepInEx/config/ModSync/required_mods`
- `BepInEx/config/ModSync/optional_mods`

Place each approved package in its own directory named
`Owner-Package-Version`, containing the package's mod DLLs. Required packages
must be present on every client; optional packages are allowed when present.
ModSync itself is added to the required policy automatically.

## Client synchronization

When a policy mismatch can be repaired, ModSync shows the player which
Thunderstore packages would be installed or removed and asks for confirmation.
The player must restart Valheim after synchronization and reconnect. Package
changes are applied to the active r2modman profile; keep a backup of profiles
and review the confirmation before accepting.

The server and client need compatible ModSync versions and the same policy.
This mod does not include third-party Thunderstore mods.

## Source

Source code and build instructions: <https://github.com/smurfhub/valheim>
