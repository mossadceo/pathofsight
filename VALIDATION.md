# Compatibility and validation

Path Of Sight is an early Windows x64 release. Its automated checks cover map data, landmark handling, pathfinding, settings, local API protections, and terminal layout. A live Path of Exile 2 client has been used to verify terrain reading, nearby landmarks, routing to a checkpoint, and the Tab-map overlay in one campaign area.

Compatibility across game versions, zones, display scales, and all landmark types is not yet established. An update to Path of Exile 2 may require changes to memory signatures or offsets. Geometric landmarks are approximate until confirmed by a game object.

The repository includes an anonymized terrain fixture at `tests/pathofsight.Tests/fixtures/scorched-entry.json` for pathfinding checks. It contains terrain cells and relative positions, without account, character, or process-address data.
