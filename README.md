# ModSync

Valheim mod synchronizer. The server checks the mods of each connecting player against its mod policy and, when possible, offers the player to install or remove approved [Thunderstore](https://thunderstore.io) packages.

## Quickstart

1. Install [BepInEx](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) on the server and on every client.
2. Copy the ModSync DLL into `BepInEx/plugins` (server and clients, same version).
3. Start the server once. ModSync creates this structure:

   ```text
   BepInEx/config/ModSync/
   ├── required_mods/   # must be installed on every client
   └── optional_mods/   # allowed if the player has them
   ```

ModSync adds itself to the required policy automatically.

**Server:** fills `required_mods` and `optional_mods` with the mods it wants (see [Adding mods](#adding-mods)).

**Clients:** nothing to configure. They only need ModSync in `BepInEx/plugins`; the server's policy decides what they must have.

## Adding mods

Put each mod in `required_mods` or `optional_mods`, in its own folder named `Owner-Package-Version` (e.g. `RandyKnapp-EpicLoot-0.12.4`).

**Recommended:** extract the Thunderstore package with all its contents into that folder. ModSync scans the folder recursively for `manifest.json` and `.dll` files, so the original layout does not need to be modified.

```text
required_mods/
└── RandyKnapp-EpicLoot-0.12.4/
    ├── manifest.json
    ├── icon.png
    └── plugins/
        └── EpicLoot.dll
```

### Why keep `manifest.json`

- It is used for validations: the folder name must be `Owner-Package-Version`, or `Owner-Package` with a `manifest.json` containing a valid `version_number`.
- It tells ModSync the mod is a Thunderstore package, so clients can download it from Thunderstore when they are missing it or have an outdated version. Without it, ModSync cannot offer that repair.

## Client synchronization

On a mismatch, the player sees which packages will be installed or removed and must confirm. Afterwards, restart Valheim and reconnect. Changes are applied to the active r2modman profile, so back up your profiles first.
