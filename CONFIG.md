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
| `MergeTickWindow` | `0.5` | Seconds; `0` disables merging | Combines repeated damage of the same type to the same zombie into one number if it occurs within this time of the previous hit. Each merged hit restarts the number's lifetime. |
| `NumberLifetime` | `1.5` | Positive number of seconds | How long a damage number stays visible while rising and fading out. |
| `Scale` | `1.0` | Positive number | Size multiplier for bars and numbers, applied on top of the game's HUD scale. Use `1.5` for 50% larger text and bars, or `0.75` for 25% smaller. |
| `BarOffset` | `0.6` | Number in world units | Height of the health bar above the zombie's chest. Increase it to move the bar higher. |
