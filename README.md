# Combat Text

A mod for [*The Last Stand: Aftermath*](https://www.nexusmods.com/thelaststandaftermath) that shows enemy damage feedback the game leaves out. Each damaged zombie gets a small health bar above its head, hidden while its health is full. Each hit or fire tick spawns a floating number showing the health actually lost, so you can tell how close a zombie is to death and how much each attack takes.

An armored zombie shows its armor as blue segments under the health bar, one per armor part, from the first hit. A segment empties as its part breaks, and a broken-shield icon flashes once above the bar when the last plate goes, so you know the zombie is now open to health damage. The number is the real health delta across the hit, not the raw damage, so a fully resisted hit draws nothing. Fire ticks merge inside a short window so the screen stays readable. Drawing is IMGUI from an injected overlay, gated off while the game is paused, a menu is open, or the mission is not running.

While a zombie burns, bleeds, or is stunned, an icon for each effect shows in a row to the right of the bar. Each icon greys from the top as the effect's time runs out, so a half-grey icon means half the time is spent. An icon appears only after its effect lasts a short delay, so a zombie that dies right after the first tick never flashes one.

Everything is configurable in `BepInEx\config\com.ivmakk.tlsa.combattext.cfg`: turn the bars or the numbers off, set the number lifetime and the fire-tick merge window, size the status and armor-break icons, set the icon show delay, and scale the whole overlay on top of the game's HUD scale. Edit the file, then restart the game to apply the change.

## Install

1. Install [BepInEx 6 (IL2CPP)](https://www.nexusmods.com/thelaststandaftermath/mods/1) for The Last Stand: Aftermath. Start the game once so BepInEx finishes setup, then quit.
2. Extract this mod's zip into the game folder (the folder with the game .exe). The DLL lands in `BepInEx\plugins`. Full path examples:
   - Steam: `C:\Program Files (x86)\Steam\steamapps\common\The Last Stand Aftermath\BepInEx\plugins\CombatText.dll`
   - Epic: `C:\Program Files\Epic Games\The Last Stand Aftermath\BepInEx\plugins\CombatText.dll`
3. Start the game. Hit a zombie and a health bar and a damage number appear.

Not working? Open `BepInEx\LogOutput.log` and look for the `CombatText loaded` line.

## Uninstall

Delete `CombatText.dll` from the `BepInEx\plugins` folder.

## Build

This is a BepInEx 6 IL2CPP plugin. It compiles against the game's IL2CPP interop assemblies, so a working game install with BepInEx 6 set up is required. Those assemblies are game-derived and are not part of this repo.

```
dotnet build src/CombatText.csproj -c Release
```

`Directory.Build.props` sets `GameDir` to the default Steam install path. If the game lives elsewhere, override it without editing the file: set a `GameDir` environment variable, or pass `-p:GameDir=...` on the build. The output DLL is at `src\bin\Release\CombatText.dll`.

## Package

Add `-p:Package=true` to a Release build to also produce the ready-to-install zip at `dist\CombatText-<version>.zip`, laid out as `BepInEx\plugins\CombatText.dll` so a user extracts it at the game root. A plain build skips this step.

```
dotnet build src/CombatText.csproj -c Release -p:Package=true
```

## License

Licensed under the GNU General Public License v3.0. Copyright (C) 2026 ivmakk. See [LICENSE](LICENSE).

You may reuse and modify this mod, but you must keep it open under the same license and give credit. Do not reupload it without credit.
