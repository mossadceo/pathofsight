# Path Of Sight

> [!WARNING]
> **MAP REVEAL / РАСКРЫТИЕ КАРТЫ — ACCOUNT SUSPENSION IS POSSIBLE / ВОЗМОЖНА БЛОКИРОВКА АККАУНТА**
> This tool reads the game process and reveals map information before you discover it in game. Using it may lead to account suspension or a permanent ban. **Use it at your own risk.** [GGG does not guarantee that third-party tools are allowed.](https://www.pathofexile.com/forum/view-thread/3637217)

Path Of Sight is a Path of Exile 2 map reveal tool for Windows. It displays unrevealed terrain, points of interest, and routes in a local browser map, with an optional overlay on the game's Tab map.

The map includes area transitions, waypoints, checkpoints, quest locations, bosses, and other landmarks when the game client exposes them. It also offers filters, labels, and display settings.

In waystone maps, a preloaded boss spawn marker appears before the boss entity is active when the client exposes one. It replaces the approximate `BossArena`/`BossRoom` terrain marker. Living Unique monsters may appear as moving markers, including Rogue Exiles and Ritual bosses; their rarity alone does not identify the boss that completes the map.

With Routing set to Atlas bosses, the automatic route uses only a preloaded map-boss spawn marker or a recognized boss-arena terrain landmark. It never switches to a Unique monster solely because that monster appears nearby. If neither early marker nor arena landmark is exposed, no automatic boss route is shown; you can still select a visible target manually. Long boss routes search across the full map grid instead of stopping after a short fixed search. A manually selected destination takes priority until you reset the selection; then the map-boss route resumes. Campaign bosses never start an automatic route. If the terrain data has no walkable connection to the target, the app reports that no path was found and retries as the character moves.

## Download

Download `pathofsight-win-x64.zip` from the [latest release](https://github.com/mossadceo/pathofsight/releases/latest), extract it, and run `pathofsight.exe`. The Windows x64 package includes the .NET runtime.

Start Path of Exile 2 and enter an area. In the terminal, press `1` to connect, `2` to open the browser map, `3` for Settings, `4` for Profiles, or `Q` to quit. The overlay appears over the game's map while the game window is active.

## Profiles and settings

Use arrows to select a row, Enter/Space to activate it, Left/Right to adjust numbers, and Esc to go back. Profiles also accept keys 1–6. Settings and Profiles replace the right column of the main menu, keeping the logo and connection status on the left. Long lists scroll within that column; narrow terminals use a compact layout. Settings screens always identify the profile being edited. Queued keys are processed without a pause between them; idle input polling is separate from periodic screen refresh.

| Profile | Visible POI by default | Routing |
| --- | --- | --- |
| Default | All supported types except Magic | Manual |
| Act Rush | Quests/rewards, transitions, NPC, Unique, arenas, waypoints/checkpoints | Off |
| Atlas Boss Rush | Map-completion boss marker, or its recognized arena fallback | Atlas bosses |
| Custom 1–3 | Same as Default | Manual |

Every profile is editable. **Reset current** restores that profile's original filters, routing, display settings, alignment and names without changing other profiles. New installations start with Default. Existing flat settings are migrated into Custom 1, keeping the previous automatic routing behavior; `settings.json.v1.bak` preserves the original file.

Settings → Active POI contains Mechanics, Act / Quest, Enemies, and World & Objects. Magic, Rare and Unique have visibility toggles; Normal monsters are excluded. Magic markers are blue and Rare yellow in the overlay and browser. Rare remains enabled in Default and Custom; Magic starts disabled in every profile and can be enabled in Enemies. Existing version-2 profiles are migrated to version 3 with Normal and Magic removed from their filters; the original file is backed up as `settings.json.before-monster-filters.bak`. Monster markers are not remembered after they leave the client's active or sleeping entity lists. The radar reads distant monsters from the client's sleeping list when their rarity and position are available; it cannot guarantee that every monster on the map is already exposed by the client. Unique monsters and map-completion bosses have separate filters: a Rogue Exile or Ritual boss does not become the Atlas route target. Unknown objects remain Other POI; mechanic classification uses metadata namespaces, not arbitrary label words. Support depends on what the client exposes, and the presence of a filter does not guarantee every variant is recognized.

The restricted reader also accepts Ritual rune objects without minimap icons and recognized stationary POIs from the sleeping list when their positions are readable. This can reveal some mechanics before approaching them; it cannot reveal an encounter whose position is absent from the inspected client data.

Routing **Off** disables all routes, **Manual** requires selecting a visible target in the web map, and **Atlas bosses** automatically selects the map boss while allowing manual override. Changing the profile or routing mode clears the previous selection. Hidden targets cannot retain a route.

TUI changes are saved in the active profile and apply to both the browser and overlay. Browser category filters and individual hiding are temporary for the current instance; they reset on changing location, switching/resetting the profile, or restarting. Returning to a previous location also starts with the profile. The browser's **Вернуть фильтры профиля** button clears temporary overrides. Editing a category in TUI removes that category's temporary override while retaining unrelated overrides. Browser appearance changes and POI names are saved in the profile.

Settings → Display controls overlay, labels, terrain, opacity and icon size. Alignment controls overlay scale and offsets. The local API exposes the active profile, effective settings and shared filter catalogue. Writes require the current profile, instance and settings revision, so an old browser tab cannot overwrite newer settings.

## Requirements and limitations

- Windows x64 and Path of Exile 2 in windowed or borderless mode. Linux is not supported.
- The tool reads the game's process memory. It does not modify game files, write to the process, or send game input.
- The browser map is served only on `127.0.0.1`. There is no telemetry, price lookup, or automatic update service.
- Game updates may affect compatibility. Some landmarks are inferred from area geometry and may not mark an exact interactable position; routes cannot account for every door or event state.
- Settings and logs are stored in `%LOCALAPPDATA%\pathofsight`. If process access is denied, run the application as administrator.

## Build from source

Run `./build.ps1` in PowerShell. The script installs .NET SDK 10.0.401 under `.tools` if needed, builds the project, runs its checks, and creates `dist/pathofsight-win-x64.zip` with a SHA-256 checksum.

Path Of Sight includes code and reference data from [POE2Radar](https://github.com/Sikaka/POE2Radar) under the MIT license. See [third-party notices](THIRD-PARTY-NOTICES.md).
