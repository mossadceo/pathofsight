namespace POE2Radar.Core.Game;

/// <summary>Stored byte pattern for PoE2's GameState global pointer slot.</summary>
public static class AobPatterns
{
    public sealed record Pattern(byte?[] Bytes, int DispOffset, int InstrLen, string Description);

    // The displacement after 48 39 2D resolves to the GameState slot. Later displacements
    // are wildcards because they change across game builds.
    public static readonly Pattern[] GameStateRefs =
    [
        new Pattern(
            Bytes: new byte?[] {
                0x48, 0x39, 0x2D, null, null, null, null,
                0x0F, 0x85, null, null, null, null,
                0xB9, null, null, null, null,
                0xE8, null, null, null, null,
                0x48, 0x8B, 0xF8,
                0x48, 0x89, 0x44, 0x24, 0x50
            },
            DispOffset: 3,
            InstrLen: 7,
            Description: "PoE2 GameStates global slot (GameHelper2 'Game States')")
    ];
}