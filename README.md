# OreFix for Starship EVO

A small BepInEx 5 plugin that fixes ghost and floating ore scanner markers in **Starship EVO**.

> ✅ **Tested in-game and working (v1.0.0, October 2026). Game updates may break it, so back up your saves first.**

## What it fixes

After you mine ore, the ore scanner sometimes keeps showing markers that should be gone. You get "ghost" markers for ore you already took, and markers floating in the air where you dug the terrain away. OreFix removes those stale markers.

**Technical root cause:** `OctreeNodeResources.CheckIfDropRock` gets `OctreeNodeData` **by value** and then calls `OreScannerResources.UpdateData(ref nodeData.oreScannerDataId, index)`. When the mined ore is the head of the node's marker list, the new head id is written to the local copy and lost. The node keeps pointing at the removed marker.

Patches (each one can be turned off in the config):

| Key | What it does |
|---|---|
| `FixMinedOreMarkerRemoval` | Transpiler that writes the marker-list head change back to the real node. It also runs again after `UpdateDensitiesOnNode`. |
| `CleanOrphanMarkersOnScan` | On scan, releases orphan/ghost markers that no longer match ore. |
| `RemoveMarkersInAir` | On scan, removes markers whose voxel is now empty (air). |
| `SnapMarkersToVoxel` | Moves markers onto their ore voxel. |

## Requirements

- Windows
- Starship EVO on Steam (app 711980)
- [BepInEx 5.4.x **x64**](https://github.com/BepInEx/BepInEx/releases) (for example `BepInEx_win_x64_5.4.23.x.zip`). BepInEx 6 is not supported.

## Install

1. In Steam, right-click **Starship EVO** > **Manage** > **Browse local files**. This opens the game folder (the one with `Starship EVO.exe`).
2. Extract the BepInEx 5 x64 zip into that folder. `BepInEx\`, `winhttp.dll` and `doorstop_config.ini` must end up next to `Starship EVO.exe`.
3. Start the game once, wait for the main menu, then quit. BepInEx creates its folders (`BepInEx\plugins`, `BepInEx\config`, and so on).
4. Download `OreFix-v1.0.0.zip` from this repo's [Releases](../../releases) page and extract it into the game folder. You should then have `BepInEx\plugins\OreFix\OreFix.dll`.
5. Start the game.

## Check that it works

Open `BepInEx\LogOutput.log` in the game folder and look for:

- `OreFix 1.0.0 loaded` (the plugin loaded)
- `transpiler replaced 1 UpdateData call(s)`. **If it says 0, the main fix is not active**, probably because a game update changed the code.
- While mining: `[OreFix] Mined ore voxel …`, sometimes with `(vanilla would have lost this head change)`
- While scanning: `[OreFix] Scan validation: … orphan/ghost released=N air(floating) removed=M`

## Configuration

The config file is created after the first launch with the mod:
`BepInEx\config\com.jammytoast.starshipevo.orefix.cfg`

```ini
[Fixes]
FixMinedOreMarkerRemoval = true
CleanOrphanMarkersOnScan = true
RemoveMarkersInAir = true
SnapMarkersToVoxel = true

[Debug]
VerboseLogging = true
```

Set a value to `false` to turn that fix off. Restart the game after you change the file.

## Uninstall

**To remove only OreFix:** delete the `BepInEx\plugins\OreFix` folder. You can also delete `BepInEx\config\com.jammytoast.starshipevo.orefix.cfg`.

**To remove BepInEx completely:** in the game folder, delete
- the `BepInEx` folder
- `winhttp.dll`
- `doorstop_config.ini`
- `.doorstop_version` and `changelog.txt`, if they exist

If you want, you can then run Steam > right-click Starship EVO > **Properties** > **Installed Files** > **Verify integrity of game files**.

## Game updates

The game is in Early Access and changes often. An update can break this mod, so check the log line `transpiler replaced 1 UpdateData call(s)` after each update. If something breaks, remove the mod (see above).

## Building from source

The game's DLLs are **not** in this repo. You need your own copy of the game.

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (6 or newer).
2. Build:
   ```
   dotnet build -c Release
   ```
   If the game is not in the default Steam folder (`C:\Program Files (x86)\Steam\steamapps\common\Starship EVO`), pass the path:
   ```
   dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Starship EVO"
   ```
   References are loaded from `$(GameDir)\Starship EVO_Data\Managed`.
3. The output is `bin\Release\netstandard2.0\OreFix.dll`. Copy it to `BepInEx\plugins\OreFix\`.

## Reporting issues

Open an issue here and attach your `BepInEx\LogOutput.log`. If the fix works for you, please also report the original bug to the developer at https://github.com/tsunamayo/Starship-EVO/issues so it can be fixed in the game itself.

## License

[MIT](LICENSE) © 2026 JamMyTo4st
