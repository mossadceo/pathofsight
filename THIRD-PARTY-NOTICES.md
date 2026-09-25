# Third-party notices

pathofsight uses code and embedded reference data from [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar), MIT license, copyright (c) 2026 POE2Radar contributors.

Pinned upstream revision: **a23cdb6bf8dfb59dee53008a86439f05b24e3a57**.
Imported subtree: `src/POE2Radar.Core`, stored in `vendor/POE2Radar.Core`.
The complete upstream license is retained in `vendor/POE2Radar.LICENSE` and included with the binary distribution.

Local changes to the imported code:

- A POI-only entity-reading mode omits monster statistics and item identities.
- Terrain allocation and packed-row bounds are validated before allocation.
- State resolution is restricted to active states to avoid showing inactive instances.
- Active state vector offset corrected from 0x08 to 0x10 using the live client on 2026-09-24; see VALIDATION.md for the executable fingerprint and limits of verification.
- A* and path smoothing reject diagonal wall-corner shortcuts.

The original overlay, input automation, pricing clients and updater are not imported. pathofsight implements its own WPF renderer using the upstream projection and terrain data; pathfinding and landmark extraction remain upstream-based.

The self-contained distribution also includes the Microsoft .NET, ASP.NET Core and Windows Desktop runtimes. Runtime licenses and third-party notices are supplied by Microsoft's runtime packages. pathofsight does not redistribute the game client.
