# Path Of Sight

> [!WARNING]
> **MAP REVEAL / РАСКРЫТИЕ КАРТЫ — ВОЗМОЖНА БЛОКИРОВКА АККАУНТА PATH OF EXILE 2.**
> This tool reads the game process and reveals map information before you discover it in game. Using it may lead to account suspension or a permanent ban. **Use it at your own risk.** [GGG does not guarantee that third-party tools are allowed.](https://www.pathofexile.com/forum/view-thread/3637217)

Path Of Sight is a Path of Exile 2 map reveal tool for Windows. It displays unrevealed terrain, points of interest, and routes in a local browser map, with an optional overlay on the game's Tab map.

The map includes area transitions, waypoints, checkpoints, quest locations, bosses, and other landmarks when the game client exposes them. It also offers filters, labels, and display settings.

In waystone maps, a preloaded boss spawn marker appears before the boss entity is active when the client exposes one. It replaces the approximate `BossArena`/`BossRoom` terrain marker. A hostile unique boss appears as a separate moving marker once the client creates its entity; the marker disappears on confirmed death or when the entity leaves the loaded list. Where the client exposes neither a spawn marker nor a recognizable arena tile, the boss can appear only after it spawns.

When a waystone map exposes a boss target, the route to it starts automatically and updates as the character moves. Long boss routes search across the full map grid instead of stopping after a short fixed search. A manually selected destination takes priority until you reset the selection; then the boss route resumes. Campaign bosses never start an automatic route. If the terrain data has no walkable connection to the target, the app reports that no path was found and retries as the character moves.

## Download

Download `pathofsight-win-x64.zip` from the [latest release](https://github.com/mossadceo/pathofsight/releases/latest), extract it, and run `pathofsight.exe`. The Windows x64 package includes the .NET runtime.

Start Path of Exile 2 and enter an area. In the terminal, press `1` to connect, `2` to open the browser map, or `Q` to quit. The overlay appears over the game's map while the game window is active.

## Requirements and limitations

- Windows x64 and Path of Exile 2 in windowed or borderless mode. Linux is not supported.
- The tool reads the game's process memory. It does not modify game files, write to the process, or send game input.
- The browser map is served only on `127.0.0.1`. There is no telemetry, price lookup, or automatic update service.
- Game updates may affect compatibility. Some landmarks are inferred from area geometry and may not mark an exact interactable position; routes cannot account for every door or event state.
- Settings and logs are stored in `%LOCALAPPDATA%\pathofsight`. If process access is denied, run the application as administrator.

## Build from source

Run `./build.ps1` in PowerShell. The script installs .NET SDK 10.0.401 under `.tools` if needed, builds the project, runs its checks, and creates `dist/pathofsight-win-x64.zip` with a SHA-256 checksum.

Path Of Sight includes code and reference data from [POE2Radar](https://github.com/Sikaka/POE2Radar) under the MIT license. See [third-party notices](THIRD-PARTY-NOTICES.md).
