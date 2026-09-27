# Handoff

## Current state

- Repository: `mossadceo/pathofsight`, branch `main`. At handoff, `main` matches `origin/main` at `6b66516` and the working tree is clean apart from this handoff file.
- [v0.2.0 release](https://github.com/mossadceo/pathofsight/releases/tag/v0.2.0) is published with English notes, a self-contained Windows x64 ZIP, and its SHA-256 file. The tag points to `6b66516`.
- Version `0.2.0` is reflected in the project, TUI, web page, and diagnostics. The last `build.ps1` run passed all 303 console checks and produced `dist/pathofsight-win-x64.zip`. The EXE is about 160 MiB because it bundles .NET, WPF, and ASP.NET Core; the ZIP is about 70 MiB.
- There is no pending implementation request. The most recent request was to create this handoff file.

## Where to continue

- Read `AGENTS.md` first. `README.md` describes current behavior; `VALIDATION.md` records tested behavior and live-client gaps. Keep the map-reveal/account-ban warning visible.
- Main code: `src/pathofsight/`; pinned memory reader and pathfinding: `vendor/POE2Radar.Core/`; checks: `tests/pathofsight.Tests/`; read-only live diagnostic: `tools/pathofsight.Probe/`.
- The v0.2.0 work added editable profiles and TUI settings, temporary browser filters, routing modes, Rare/Magic visibility, and selected distant POI reads. The preceding cleanup removed unused upstream readers and AOB research code. See commits `5f01e1c` and `78e14a3` for the changes rather than duplicating them here.
- Whole-map POI and monster visibility is not guaranteed: the game client may not expose their positions before approach. See the dated live observations in `VALIDATION.md` before changing memory reads or making stronger claims.

## Suggested skills

- Call the Skill tool for `ponytail:ponytail` before coding; it is active in this conversation and favors the smallest working change.
- Call the Skill tool for `diagnosing-bugs` when investigating a reported regression or performance problem.
