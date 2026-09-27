# Repository Guidelines

## Project Structure & Module Organization

`src/pathofsight/` contains the Windows x64 app: game-data coordination in `MapService.cs`, POI and settings models in `Model.cs`, the WPF overlay, local HTTP server, terminal UI, and embedded `dashboard.html` and `Art/eye.txt`. `vendor/POE2Radar.Core/` holds the pinned upstream memory reader, terrain data, and pathfinding code; preserve its license and explain any local changes. `tests/pathofsight.Tests/` contains the executable check harness and anonymized terrain fixtures. `tools/pathofsight.Probe/` is a read-only live-client diagnostic tool. See `README.md` for usage and `VALIDATION.md` for verified behavior and gaps.

## Build, Test, and Development Commands

- `./build.ps1`: build, run checks, publish a self-contained Windows x64 EXE, and create `dist/pathofsight-win-x64.zip` plus its SHA-256 file. Close an EXE running from `dist/pathofsight-win-x64/` first; Windows locks that publish target.
- `.\.tools\dotnet\dotnet.exe run --project tests/pathofsight.Tests -c Release`: run checks without packaging. `build.ps1` installs the pinned SDK under `.tools` if needed.
- `.\.tools\dotnet\dotnet.exe run --project src/pathofsight -c Release -- --demo`: run the app with simulated map data, without reading the game.

## Coding Style & Naming Conventions

Use .NET 10 C# conventions: four-space indentation, `PascalCase` for public types and members, `camelCase` for locals, and `_camelCase` for private fields. Nullable reference types and warnings-as-errors are enabled. Keep changes focused; reuse the existing POI, route, and snapshot flow. There is no separate formatter or linter configuration; a clean Release build is the style gate.

## Testing Guidelines

The tests are a console harness using `Check(condition, "behavior")`, not xUnit. Add a focused check in `tests/pathofsight.Tests/Program.cs` for each changed behavior, especially map-only boss selection, route failures, and instance resets. Use small synthetic grids or anonymized fixtures. Live-client checks supplement the harness; record their scope and remaining limitations in `VALIDATION.md`.

## Commit & Pull Request Guidelines

Recent commits use short imperative subjects, such as `Clarify map reveal and account ban risk` and `Route waystone maps to boss spawn markers`. Keep commits focused. There is no PR template: describe the behavior changed, checks run, relevant issue if one exists, and screenshots for overlay or browser-map changes. Note any unverified game versions or layouts.

## Security & Configuration

This is map-reveal software that reads PoE2 process memory and carries account-ban risk; keep the warning in `README.md` visible. Keep the local API bound to `127.0.0.1`, retain its request checks, and exclude account data, character names, and process addresses from fixtures and published diagnostics.
