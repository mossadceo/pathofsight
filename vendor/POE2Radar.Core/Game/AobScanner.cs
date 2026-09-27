using POE2Radar.Core.Native;

namespace POE2Radar.Core.Game;

/// <summary>
/// Array-of-bytes pattern scanning over PoE.exe's executable image sections.
///
/// At startup, ScanForResolvedAddresses resolves the stored GameState pattern to a global slot.
/// </summary>
public static class AobScanner
{
    /// <summary>
    /// Find all byte offsets in <paramref name="haystack"/> where the wildcard pattern matches.
    /// A null byte in <paramref name="pattern"/> matches any byte.
    /// </summary>
    public static List<int> FindPattern(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte?> pattern)
    {
        var results = new List<int>();
        if (pattern.Length == 0 || haystack.Length < pattern.Length) return results;

        for (var i = 0; i <= haystack.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (pattern[j] is { } pb && haystack[i + j] != pb)
                {
                    match = false;
                    break;
                }
            }
            if (match) results.Add(i);
        }
        return results;
    }

    /// <summary>
    /// Read all committed executable pages within PoE.exe's main module image.
    /// Returns (absoluteAddress, bytes) for each executable section page group.
    /// </summary>
    public static List<(nint Address, byte[] Bytes)> ReadExecutableSections(ProcessHandle proc, MemoryReader reader)
    {
        var sections = new List<(nint, byte[])>();
        var moduleEnd = proc.MainModuleBase + (nint)proc.MainModuleSize;

        foreach (var mbi in proc.EnumerateRegions(proc.MainModuleBase, moduleEnd))
        {
            if (mbi.State != NativeMethods.MEM_COMMIT) continue;
            if (mbi.Type != NativeMethods.MEM_IMAGE) continue;
            if ((mbi.Protect & NativeMethods.PAGE_GUARD) != 0) continue;

            const uint execMask = NativeMethods.PAGE_EXECUTE
                                | NativeMethods.PAGE_EXECUTE_READ
                                | NativeMethods.PAGE_EXECUTE_READWRITE;
            if ((mbi.Protect & execMask) == 0) continue;

            var size = (int)mbi.RegionSize;
            var buf = new byte[size];
            var read = reader.TryReadBytes(mbi.BaseAddress, buf.AsSpan());
            if (read == 0) continue;

            sections.Add(read == size ? (mbi.BaseAddress, buf) : (mbi.BaseAddress, buf[..read]));
        }
        return sections;
    }

    /// <summary>
    /// Resolve a RIP-relative 4-byte displacement at a known position in a section buffer.
    /// Returns the absolute address in the target process that the instruction references.
    /// </summary>
    /// <param name="sectionBase">Base address of the section in the target process.</param>
    /// <param name="matchOffset">Offset of the instruction start within <paramref name="sectionBytes"/>.</param>
    /// <param name="dispOffset">Offset of the 4-byte signed displacement within the instruction (e.g., 3 for REX MOV reg,[RIP+rel32]).</param>
    /// <param name="instrLen">Total instruction length in bytes (e.g., 7 for 3-byte prefix + 4-byte displacement).</param>
    /// <param name="sectionBytes">Raw bytes of the section.</param>
    public static nint ResolveRipRelative(nint sectionBase, int matchOffset, int dispOffset, int instrLen, ReadOnlySpan<byte> sectionBytes)
    {
        var dispPos = matchOffset + dispOffset;
        if (dispPos + 4 > sectionBytes.Length) return 0;
        var displacement = BitConverter.ToInt32(sectionBytes[dispPos..]);
        var nextInstrAddr = sectionBase + matchOffset + instrLen;
        return nextInstrAddr + displacement;
    }

    /// <summary>
    /// Scan all executable sections of PoE.exe for <paramref name="pattern"/>, returning the absolute
    /// address each match's RIP-relative displacement resolves to. Used at app startup.
    /// </summary>
    public static List<nint> ScanForResolvedAddresses(ProcessHandle proc, MemoryReader reader,
        AobPatterns.Pattern pattern)
    {
        var results = new List<nint>();
        foreach (var (sectionBase, bytes) in ReadExecutableSections(proc, reader))
        {
            var matches = FindPattern(bytes, pattern.Bytes);
            foreach (var matchOffset in matches)
            {
                var addr = ResolveRipRelative(sectionBase, matchOffset, pattern.DispOffset, pattern.InstrLen, bytes);
                if (addr != 0) results.Add(addr);
            }
        }
        return results;
    }
}
