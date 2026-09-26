# Path Of Sight

Path Of Sight is a Windows tool for exploring Path of Exile 2 areas. It displays terrain, nearby points of interest, and routes in a local browser map, with an optional overlay on the game's Tab map.

The map includes area transitions, waypoints, checkpoints, quest locations, bosses, and other landmarks when the game client exposes them. It also offers filters, labels, and display settings.

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
