# Configuration

Every setting lives in `BepInEx\config\com.ivmakk.tlsa.combattext.cfg`, written the first time you run the game with the mod installed. Edit it with any text editor, then restart the game to apply the change.

## General

| Setting | Default | Values | What it does |
|---|---|---|---|
| `Verbose` | `false` | `true` / `false` | Writes extra diagnostic lines to the BepInEx log for troubleshooting. Keep off in normal play. |

## Display

| Setting | Default | Values | What it does |
|---|---|---|---|
| `ShowBars` | `true` | `true` / `false` | Shows zombie health bars and armor segments. |
| `ShowNumbers` | `true` | `true` / `false` | Shows floating numbers for health lost to damage. |
| `MergeTickWindow` | `0.1` | Seconds; `0` disables merging | Combines repeated damage of the same type to the same zombie into one number if it occurs within this time of the previous hit. Each merged hit restarts the number's lifetime. |
| `NumberLifetime` | `1.5` | Positive number of seconds | How long a damage number stays visible while rising and fading out. |
| `Scale` | `1.0` | Positive number | Size multiplier for bars and numbers, applied on top of the game's HUD scale. Use `1.5` for 50% larger text and bars, or `0.75` for 25% smaller. |
| `BarOffset` | `0.8` | Number in world units | Height of the health bar above the zombie's chest. Increase it to move the bar higher. |
| `StatusIconSize` | `16` | Number of pixels; `4` to `64` | Base size at 1080p of the status icons (fire, bleed, stun) drawn in a row after the health bar. Scaled on top of the HUD scale and `Scale`. |
| `ArmorIconSize` | `16` | Number of pixels; `4` to `64` | Base size at 1080p of the broken-shield icon that flashes when the last armor plate breaks. Scaled on top of the HUD scale and `Scale`. |
| `StatusShowDelay` | `0.1` | Seconds; `0` to `5` | How long a status effect must last before its icon appears, so a zombie that dies right after its first tick never flashes an icon. |
