namespace POE2Radar.Core.Game;

/// <summary>
/// Live PoE2 game-state reader for the radar overlay. Resolves the top-level chain each tick
/// (GameState → InGameState → AreaInstance) and exposes the player position, the entity list,
/// the walkable terrain grid, and the large-map UI state — all via offsets validated live and
/// recorded in <see cref="Poe2"/> / resources/community-offsets.md.
///
/// <para>Construct once with the AOB-resolved GameState pointer slot (see Bootstrap). Call
/// <see cref="TryResolve"/> at the start of each tick; everything else takes the resolved
/// AreaInstance / InGameState.</para>
/// </summary>
public sealed class Poe2Live
{
    private readonly MemoryReader _reader;
    private readonly nint _gameStateSlot;

    // Per-entity frozen data, keyed by entity object address (stable within an area).
    private readonly Dictionary<nint, nint> _renderAddr = new();   // entity → Render component
    private readonly Dictionary<nint, nint> _lifeAddr = new();     // entity → Life component (0 = none)
    private readonly Dictionary<nint, nint> _posAddr = new();      // entity → Positioned component (0 = none)
    private readonly Dictionary<nint, nint> _ompAddr = new();      // entity → ObjectMagicProperties (0 = none)
    private readonly Dictionary<nint, nint> _chestAddr = new();    // entity → Chest component (0 = none)
    private readonly Dictionary<nint, nint> _shrineAddr = new();   // entity → Shrine component (0 = none)
    private readonly Dictionary<nint, EntityCategory> _category = new();
    private readonly Dictionary<nint, string> _meta = new();
    private readonly Dictionary<nint, nint> _iconAddr = new();     // entity → MinimapIcon component (0 = none); game POI
    private readonly Dictionary<nint, Rarity> _rarity = new();     // entity → rarity (static per spawn; cached)
    private readonly Dictionary<nint, uint> _idAt = new();         // entity address → last-seen std::map key id (recycle guard)
    private nint _entCacheKey;   // AreaInstance address the entity caches were built for

    // Reused across Entities() calls (tick thread only) to avoid per-tick allocations. The std::map
    // walk reads each 48-byte node in ONE ReadProcessMemory (fields are contiguous), not 5 syscalls.
    private readonly Queue<nint> _entQueue = new();
    private readonly HashSet<nint> _entVisited = new();
    private readonly byte[] _nodeBuf = new byte[0x30];
    public Poe2Live(MemoryReader reader, nint gameStateSlot)
    {
        _reader = reader;
        _gameStateSlot = gameStateSlot;
    }

    public enum EntityCategory { Player, Monster, Npc, Chest, Transition, Object, Other }

    /// <summary>Monster rarity from ObjectMagicProperties.Rarity. NonMonster = not applicable.</summary>
    public enum Rarity { Normal = 0, Magic = 1, Rare = 2, Unique = 3, NonMonster = -1 }

    public readonly record struct EntityDot(
        uint Id, nint Address, System.Numerics.Vector2 Grid, Vector3 World, EntityCategory Category, string Metadata,
        int HpCur, int HpMax, bool Poi, byte Reaction, Rarity Rarity, bool Opened, bool IconComplete = false)
    {
        public bool IsFriendly => (Reaction & 0x7F) == 1;
    }
    public readonly record struct MapUi(bool IsVisible, float ShiftX, float ShiftY, float Zoom);

    /// <summary>A static tile-based landmark: a notable terrain feature and its grid centroid.
    /// <paramref name="CuratedName"/> is an optional curated friendly label (null when none matches);
    /// <paramref name="Name"/> is the derived-from-path fallback.</summary>
    public readonly record struct Landmark(string Name, string Path, System.Numerics.Vector2 Center, int TileCount, string? CuratedName = null)
    {
        /// <summary>Stable per-CLUSTER identity for nav selection. A tile path can now yield several
        /// landmarks (one per spatial cluster — e.g. each stair-up section of a multi-level dungeon),
        /// so the path alone is ambiguous; qualify it with the integer centroid, which is stable per
        /// area (tiles are static terrain).</summary>
        public string Key => $"{Path}@{(int)Center.X},{(int)Center.Y}";
    }

    public sealed record TerrainData(byte[] Walkable, int Width, int Height);

    /// <summary>Resolve the in-game chain. Returns false during loading / character select.</summary>
    public bool TryResolve(out nint inGameState, out nint areaInstance, out nint localPlayer)
    {
        inGameState = areaInstance = localPlayer = 0;
        var gameState = Ptr(_gameStateSlot);
        if (gameState == 0) return false;

        // FullMap: only active states. Inactive States[] may retain a previous zone while loading.
        var candidates = new List<nint>(13);
        var vecFirst = Ptr(gameState + Poe2.GameState.CurrentStatePtr);
        var vecLast = Ptr(gameState + Poe2.GameState.CurrentStatePtr + 8);
        var activeBytes = (long)vecLast - (long)vecFirst;
        if (vecFirst == 0 || activeBytes <= 0 || activeBytes > 32 * 8 || activeBytes % 8 != 0) return false;
        for (var i = 0; i < activeBytes / 8; i++) candidates.Add(Ptr(vecFirst + i * 8));

        foreach (var igs in candidates)
        {
            if (igs == 0) continue;
            var ai = Ptr(igs + Poe2.InGameState.AreaInstanceData);
            if (ai == 0) continue;
            var lp = Ptr(ai + Poe2.AreaInstance.LocalPlayer);
            if (lp == 0) continue;
            if (!ReadMetadata(lp).StartsWith("Metadata/", StringComparison.Ordinal)) continue;
            inGameState = igs; areaInstance = ai; localPlayer = lp;
            return true;
        }
        return false;
    }

    /// <summary>Per-area instance hash. (Caches key on the AreaInstance address; this is for display/ID.)</summary>
    public uint AreaHash(nint areaInstance)
    {
        _reader.TryReadStruct<uint>(areaInstance + Poe2.AreaInstance.CurrentAreaHash, out var h);
        return h;
    }

    private string _areaCode = "";
    private nint _areaCodeFor = -1;

    /// <summary>Area code identifier (e.g. "G1_town"). Cached per area.</summary>
    public string AreaCode(nint areaInstance)
    {
        if (areaInstance == _areaCodeFor) return _areaCode;
        _areaCodeFor = areaInstance;
        var info = Ptr(areaInstance + Poe2.AreaInstance.AreaInfoPtr);
        var s = Ptr(info + Poe2.AreaInfo.Code);
        _areaCode = s == 0 ? "" : _reader.ReadStringUtf16(s, 64);
        return _areaCode;
    }

    private string _areaName = ""; private nint _areaNameFor = -1;

    /// <summary>Area display name (e.g. "Glacial Tarn"). Cached per area.</summary>
    public string AreaName(nint areaInstance)
    {
        if (areaInstance == _areaNameFor) return _areaName;
        _areaNameFor = areaInstance;
        var info = Ptr(areaInstance + Poe2.AreaInstance.AreaInfoPtr);
        var s = Ptr(info + Poe2.AreaInfo.Name);
        _areaName = s == 0 ? "" : _reader.ReadStringUtf16(s, 64);
        return _areaName;
    }

    /// <summary>Player grid position from the Render component.</summary>
    public System.Numerics.Vector2? PlayerGrid(nint localPlayer) => EntityGrid(localPlayer);

    /// <summary>Read monster rarities, including dead IDs needed to retire sleeping markers, and awake POIs.</summary>
    public List<EntityDot> Entities(nint areaInstance)
    {
        if (areaInstance != _entCacheKey)
        {
            _renderAddr.Clear(); _lifeAddr.Clear(); _posAddr.Clear(); _ompAddr.Clear(); _chestAddr.Clear(); _shrineAddr.Clear();
            _category.Clear(); _meta.Clear(); _iconAddr.Clear(); _rarity.Clear(); _idAt.Clear();
            _entCacheKey = areaInstance;
        }

        var dots = new List<EntityDot>(256);
        var head = Ptr(areaInstance + Poe2.AreaInstance.AwakeEntities);
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.AwakeEntities + 8, out var size);
        if (head == 0 || size <= 0 || size > 100000) return dots;

        var root = Ptr(head + Poe2.StdMapNode.Parent);
        _entQueue.Clear(); _entQueue.Enqueue(root);
        _entVisited.Clear();
        while (_entQueue.Count > 0 && _entVisited.Count < 200000)
        {
            var node = _entQueue.Dequeue();
            if (node == 0 || node == head || !_entVisited.Add(node)) continue;

            // One read for the whole node — Left/Right/IsNil/KeyId/ValueEntityPtr are contiguous in
            // 48 bytes, so this replaces 5 separate ReadProcessMemory syscalls per node with one.
            if (_reader.TryReadBytes(node, _nodeBuf) < _nodeBuf.Length) continue;
            if (_nodeBuf[Poe2.StdMapNode.IsNil] != 0) continue; // sentinel/nil — don't traverse its children

            var id = BitConverter.ToUInt32(_nodeBuf, Poe2.StdMapNode.KeyId);
            var entity = (nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.ValueEntityPtr);
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Left));
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Right));

            if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;

            // Recycle guard: entity object addresses are reused within an area as things die/spawn.
            // The std::map key id is the stable per-entity identity (monotonic, never reused in an
            // area), so if THIS address now carries a different id than we cached it under, the prior
            // occupant is gone — evict its frozen component addresses/category/rarity/icon so we don't
            // read a freed/reused Life or Render (stale HP bars over corpses, POIs flickering at stale
            // positions). Re-resolves fresh below.
            if (_idAt.TryGetValue(entity, out var prevId) && prevId != id) EvictEntity(entity);
            _idAt[entity] = id;

            var world = EntityWorld(entity);
            if (world is not { } wv) continue;
            var g = new System.Numerics.Vector2(wv.X / Poe2.WorldToGridRatio, wv.Y / Poe2.WorldToGridRatio);

            var cat = Categorize(entity);
            if (cat == EntityCategory.Player) continue;
            if (cat == EntityCategory.Monster)
            {
                var rarity = ReadRarity(entity);
                var (cur, max) = ReadHp(entity);
                var reaction = ReadReaction(entity);
                if (!IsVisibleMonster(rarity, cur, max, reaction)
                    && !((rarity is Rarity.Magic or Rarity.Rare or Rarity.Unique) && max > 0 && cur <= 0)) continue;
                dots.Add(new EntityDot(id, entity, g, wv, cat, _meta.GetValueOrDefault(entity, ""),
                    cur, max, false, reaction, rarity, false));
                continue;
            }
            var poiMeta = _meta.GetValueOrDefault(entity, "");
            var (isPoi, completed) = ReadIcon(entity, poiMeta);
            if (poiMeta.StartsWith("Metadata/Shrines/", StringComparison.OrdinalIgnoreCase))
                completed |= ReadShrineUsed(entity);
            var earlyPoi = IsEarlyPoiMetadata(poiMeta);
            if (isPoi || earlyPoi || cat is EntityCategory.Transition or EntityCategory.Npc or EntityCategory.Chest
                || poiMeta.StartsWith("Metadata/Shrines/", StringComparison.OrdinalIgnoreCase)
                || poiMeta.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
                || poiMeta.Contains("Waypoint", StringComparison.OrdinalIgnoreCase))
                dots.Add(new EntityDot(id, entity, g, wv, cat, poiMeta, 0, 0, isPoi || earlyPoi, 0, Rarity.NonMonster,
                    cat == EntityCategory.Chest && ReadChestOpened(entity), completed));
        }
        return dots;
    }

    public static bool IsVisibleBoss(Rarity rarity, int hpCur, int hpMax, byte reaction)
        => rarity == Rarity.Unique && IsVisibleMonster(rarity, hpCur, hpMax, reaction);

    public static bool IsVisibleMonster(Rarity rarity, int hpCur, int hpMax, byte reaction)
        => rarity is Rarity.Magic or Rarity.Rare or Rarity.Unique
            && (hpMax <= 0 || hpCur > 0) && (reaction & 0x7F) != 1;

    public static bool IsEarlyPoiMetadata(string metadata) =>
        metadata.StartsWith("Metadata/Shrines/", StringComparison.OrdinalIgnoreCase)
        || metadata.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
        || metadata.Contains("Waypoint", StringComparison.OrdinalIgnoreCase)
        || metadata.Equals("Metadata/Terrain/Leagues/Ritual/RitualRuneObject", StringComparison.OrdinalIgnoreCase)
        || metadata.Equals("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", StringComparison.OrdinalIgnoreCase);

    /// <summary>Visible-rarity monsters and verified POIs already present in the sleeping entity map.
    /// Sleeping entities do not expose reliable live HP or minimap icons.</summary>
    public List<EntityDot> SleepingRadarEntities(nint areaInstance)
    {
        var result = new List<EntityDot>();
        var head = Ptr(areaInstance + Poe2.AreaInstance.SleepingEntities);
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.SleepingEntities + 8, out var size);
        if (head == 0 || size <= 0 || size > 100000) return result;
        _entQueue.Clear(); _entQueue.Enqueue(Ptr(head + Poe2.StdMapNode.Parent));
        _entVisited.Clear();
        while (_entQueue.Count > 0 && _entVisited.Count < 200000)
        {
            var node = _entQueue.Dequeue();
            if (node == 0 || node == head || !_entVisited.Add(node)) continue;
            if (_reader.TryReadBytes(node, _nodeBuf) < _nodeBuf.Length || _nodeBuf[Poe2.StdMapNode.IsNil] != 0) continue;
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Left));
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Right));
            var entity = (nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.ValueEntityPtr);
            if (entity == 0) continue;
            var metadata = ReadMetadata(entity);
            var earlyPoi = IsEarlyPoiMetadata(metadata);
            if (!earlyPoi && (!metadata.Contains("/Monsters/", StringComparison.Ordinal)
                || metadata.Contains("/NPC/", StringComparison.Ordinal) || IsNonCombat(metadata))) continue;
            var raw = 0;
            if (!earlyPoi)
            {
                var omp = ResolveComponent(entity, "ObjectMagicProperties");
                if (omp == 0 || !_reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out raw)
                    || raw is < 1 or > 3) continue;
            }
            var positioned = ResolveComponent(entity, "Positioned");
            if (positioned == 0 || !_reader.TryReadStruct<Vector3>(positioned + Poe2.Positioned.WorldPosition, out var world)
                || !float.IsFinite(world.X) || !float.IsFinite(world.Y) || world.X <= 0 || world.Y <= 0) continue;
            var reaction = !earlyPoi && _reader.TryReadStruct<byte>(positioned + Poe2.Positioned.Reaction, out var observedReaction)
                ? observedReaction : (byte)0;
            if (!earlyPoi && (reaction & 0x7F) == 1) continue;
            var grid = new System.Numerics.Vector2(world.X / Poe2.WorldToGridRatio, world.Y / Poe2.WorldToGridRatio);
            result.Add(new EntityDot(BitConverter.ToUInt32(_nodeBuf, Poe2.StdMapNode.KeyId), entity, grid, world,
                earlyPoi ? EntityCategory.Object : EntityCategory.Monster, metadata, 0, 0, earlyPoi,
                reaction, earlyPoi ? Rarity.NonMonster : (Rarity)raw, false));
        }
        return result;
    }

    public static bool IsBossSpawnMarker(string metadata) =>
        metadata.EndsWith("/BossLeagueContentMarkerMain", StringComparison.OrdinalIgnoreCase) ||
        metadata.EndsWith("/BossRoomMinimapIcon", StringComparison.OrdinalIgnoreCase);

    /// <summary>Boss spawn hints exist in the sleeping map before combat and can have visual-range ids.</summary>
    public List<(uint Id, System.Numerics.Vector2 Grid)> BossSpawnMarkers(nint areaInstance)
    {
        var result = new List<(uint, System.Numerics.Vector2)>();
        var head = Ptr(areaInstance + Poe2.AreaInstance.SleepingEntities);
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.SleepingEntities + 8, out var size);
        if (head == 0 || size <= 0 || size > 100000) return result;
        _entQueue.Clear(); _entQueue.Enqueue(Ptr(head + Poe2.StdMapNode.Parent));
        _entVisited.Clear();
        while (_entQueue.Count > 0 && _entVisited.Count < 200000)
        {
            var node = _entQueue.Dequeue();
            if (node == 0 || node == head || !_entVisited.Add(node)) continue;
            if (_reader.TryReadBytes(node, _nodeBuf) < _nodeBuf.Length || _nodeBuf[Poe2.StdMapNode.IsNil] != 0) continue;
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Left));
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Right));
            var entity = (nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.ValueEntityPtr);
            if (entity == 0 || !IsBossSpawnMarker(ReadMetadata(entity))) continue;
            var positioned = ResolveComponent(entity, "Positioned");
            if (positioned == 0 || !_reader.TryReadStruct<Vector3>(positioned + Poe2.Positioned.WorldPosition, out var world)
                || !float.IsFinite(world.X) || !float.IsFinite(world.Y) || world.X <= 0 || world.Y <= 0) continue;
            result.Add((BitConverter.ToUInt32(_nodeBuf, Poe2.StdMapNode.KeyId),
                new(world.X / Poe2.WorldToGridRatio, world.Y / Poe2.WorldToGridRatio)));
        }
        return result;
    }

    /// <summary>Drop every frozen per-entity cache entry for an address whose occupant has changed
    /// (the std::map key id no longer matches). Forces a fresh component re-resolve next read.</summary>
    private void EvictEntity(nint entity)
    {
        _renderAddr.Remove(entity); _lifeAddr.Remove(entity); _posAddr.Remove(entity);
        _ompAddr.Remove(entity); _chestAddr.Remove(entity); _shrineAddr.Remove(entity); _category.Remove(entity);
        _meta.Remove(entity); _iconAddr.Remove(entity); _rarity.Remove(entity);
    }

    /// <summary>
    /// The entity's POI state from its MinimapIcon component:
    /// <list type="bullet">
    /// <item><c>poi</c> — the game marks it as a map POI (component present).</item>
    /// <item><c>complete</c> — the standard completion flag is set, or a Brequel hand has closed.
    ///   The component stays put once resolved, so cache only its address and read its state live.</item>
    /// </list>
    /// </summary>
    private (bool poi, bool complete) ReadIcon(nint entity, string metadata)
    {
        if (!_iconAddr.TryGetValue(entity, out var icon))
        {
            icon = ResolveComponent(entity, "MinimapIcon");
            _iconAddr[entity] = icon; // cache even if 0, to avoid re-walking non-POI entities
        }
        if (icon == 0) return (false, false);
        _reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.CompletedState, out var standard);
        var brequel = 0;
        if (metadata.Equals("Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", StringComparison.OrdinalIgnoreCase))
            _reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.BrequelFinished, out brequel);
        return (true, IsCompletedIcon(metadata, standard, brequel));
    }

    public static bool IsCompletedIcon(string metadata, int standard, int brequel) => standard != 0
        || (metadata.Equals("Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", StringComparison.OrdinalIgnoreCase)
            && brequel == 1);

    private Rarity ReadRarity(nint entity)
    {
        // Rarity is fixed at spawn — read it once per entity and cache the value (not just the addr).
        if (_rarity.TryGetValue(entity, out var cached)) return cached;
        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) { _rarity[entity] = Rarity.Normal; return Rarity.Normal; }
        if (!_reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var r))
            return Rarity.Normal; // transient read failure — don't poison the cache
        var rarity = r is >= 0 and <= 3 ? (Rarity)r : Rarity.Normal;
        _rarity[entity] = rarity;
        return rarity;
    }

    private byte ReadReaction(nint entity)
    {
        if (!_posAddr.TryGetValue(entity, out var pos))
        {
            pos = ResolveComponent(entity, "Positioned");
            _posAddr[entity] = pos;
        }
        if (pos == 0) return 0;
        return _reader.TryReadStruct<byte>(pos + Poe2.Positioned.Reaction, out var b) ? b : (byte)0;
    }

    private (int cur, int max) ReadHp(nint entity)
    {
        if (!_lifeAddr.TryGetValue(entity, out var life))
        {
            life = ResolveComponent(entity, "Life");
            _lifeAddr[entity] = life;
        }
        if (life == 0) return (0, 0);
        if (!_reader.TryReadStruct<VitalStruct>(life + Poe2.Life.Health, out var v)) return (0, 0);
        return (v.Current, v.Max);
    }

    private List<Landmark>? _landmarks;
    private nint _landmarksKey = -1;

    /// <summary>Optional Overlay-supplied matcher: given a tile path, returns a friendly label (possibly
    /// empty) when the user wants that tile surfaced as a landmark, or null to ignore it. Lets users add
    /// their own landmark/tile patterns at runtime on top of the built-in keyword filter + curated list.
    /// Set by RadarApp; call <see cref="InvalidateLandmarks"/> after the pattern set changes so the
    /// per-area scan cache rebuilds.</summary>
    public Func<string, string?>? CustomLandmarkMatch { get; set; }

    /// <summary>Optional Overlay-supplied curated-label lookup: (areaCode, tilePath) → friendly label,
    /// or null. Lets a user-editable overlay sit on top of the baked-in <see cref="CustomLandmarkData"/>
    /// (the "Landmarks" tab). When unset, the baked data is used directly. Call <see cref="InvalidateLandmarks"/>
    /// after edits so the per-area scan rebuilds.</summary>
    public Func<string, string, string?>? CuratedLookup { get; set; }

    /// <summary>Resolve a tile's curated label: the injected user overlay if wired, else the baked list.</summary>
    private string? Curated(string areaCode, string tilePath)
        => CuratedLookup is { } f ? f(areaCode, tilePath) : CustomLandmarkData.TryMatch(areaCode, tilePath);

    /// <summary>Max gap (in TILES, Chebyshev) between cells still treated as one landmark cluster.
    /// Larger merges nearby copies of a reusable tile into fewer markers; smaller splits them. Set by
    /// the Overlay from <c>RadarSettings.LandmarkClusterGap</c>; call <see cref="InvalidateLandmarks"/>
    /// after changing it so the per-area scan rebuilds. Clamped to a sane range when used.</summary>
    public int LandmarkClusterGap { get; set; } = 2;

    /// <summary>Drop the cached per-area landmark scan so the next <see cref="Landmarks"/> call rebuilds
    /// it (e.g. after the user edits the custom landmark patterns from the dashboard).</summary>
    public void InvalidateLandmarks() => _landmarksKey = -1;

    private List<string>? _tilePaths;
    private nint _tilePathsKey = -1;

    /// <summary>
    /// All DISTINCT terrain-tile paths in the area (sorted), scanned once per area and cached. This is
    /// the full vocabulary of tile names — what the dashboard's add-rule picker browses so a tile rule
    /// can target any tile, not just the ones already surfaced as landmarks.
    /// </summary>
    public IReadOnlyList<string> TilePaths(nint areaInstance)
    {
        if (areaInstance == _tilePathsKey && _tilePaths is not null) return _tilePaths;
        _tilePathsKey = areaInstance;
        _tilePaths = ScanTilePaths(areaInstance);
        return _tilePaths;
    }

    private List<string> ScanTilePaths(nint areaInstance)
    {
        var result = new List<string>();
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Distinct by TgtFilePtr (one read per tile type — dozens, not per tile), collect the paths.
        var seenPtr = new HashSet<nint>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (long i = 0; i < count; i++)
        {
            var tgt = Ptr(first + (nint)(i * Poe2.TileStructureSize) + Poe2.TileStructure.TgtFilePtr);
            if (tgt == 0 || !seenPtr.Add(tgt)) continue;
            var p = ReadStdWString(tgt + Poe2.TgtFileStruct.TgtPath);
            if (!string.IsNullOrEmpty(p)) paths.Add(p);
        }
        result.AddRange(paths);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Static tile-based landmarks for the area (boss arenas, treasure, waypoints, mechanics…).
    /// Scans the terrain tile grid once per area (cached): each tile's TgtPath, grouped by path
    /// for "interesting" features, with the grid centroid of each group. This is the pre-explored
    /// "X is over here" layer — terrain-feature granularity, not a per-monster spawn table.
    /// </summary>
    public IReadOnlyList<Landmark> Landmarks(nint areaInstance)
    {
        if (areaInstance == _landmarksKey && _landmarks is not null) return _landmarks;
        _landmarksKey = areaInstance;
        _landmarks = ScanLandmarks(areaInstance);
        return _landmarks;
    }

    private List<Landmark> ScanLandmarks(nint areaInstance)
    {
        var result = new List<Landmark>();
        var areaCode = AreaCode(areaInstance);
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Collect each kept path's tile cells (in tile-index space) so we can CLUSTER them spatially
        // rather than average every instance into one centroid. A reusable tile (e.g. a "stairs up"
        // wall piece) recurs in several disjoint spots — multi-level dungeons have multiple stair-up /
        // stair-down sections connecting layers — and averaging them lands a marker in the dead space
        // between, pointing at nothing. Clustering yields one landmark per actual spot. Cache path by
        // TgtFilePtr so we read each distinct tile type's StdWString once (dozens), not per tile.
        var pathCache = new Dictionary<nint, string?>();
        var cellsByPath = new Dictionary<string, List<(int tx, int ty)>>();

        for (long i = 0; i < count; i++)
        {
            var tile = first + (nint)(i * Poe2.TileStructureSize);
            var tgtFile = Ptr(tile + Poe2.TileStructure.TgtFilePtr);
            if (tgtFile == 0) continue;
            if (!pathCache.TryGetValue(tgtFile, out var path))
            {
                var p = ReadStdWString(tgtFile + Poe2.TgtFileStruct.TgtPath);
                // Surface a tile as a landmark ONLY if the curated community list names it for this area
                // OR a user "Tile" display rule matches it (CustomLandmarkMatch). The old generic keyword
                // sweep was removed — it surfaced decorative terrain (e.g. every "...Vault_Door..." tile)
                // as noise; users now opt into any tile via Tile rules + the dashboard picker.
                var keep = Curated(areaCode, p) != null
                           || CustomLandmarkMatch?.Invoke(p) != null
                           || IsMapBossArenaTile(areaCode, p);
                path = keep ? p : null;
                pathCache[tgtFile] = path;
            }
            if (path is null) continue;
            (cellsByPath.TryGetValue(path, out var cells) ? cells : cellsByPath[path] = new())
                .Add(((int)(i % tilesX), (int)(i / tilesX)));
        }

        var cell = Poe2.Terrain.TileGridCells;
        foreach (var (path, cells) in cellsByPath)
        {
            var name = LandmarkName(path);
            // Curated label wins; else a non-empty user label; else null (derived name shows). Same
            // for every cluster of this path (they're the same feature type in different spots).
            var curated = Curated(areaCode, path) ?? NonEmpty(CustomLandmarkMatch?.Invoke(path))
                ?? (IsMapBossArenaTile(areaCode, path) ? "Арена босса" : null);
            foreach (var cluster in ClusterTiles(cells, Math.Clamp(LandmarkClusterGap, 0, 64)))
            {
                double sx = 0, sy = 0;
                foreach (var (tx, ty) in cluster) { sx += tx; sy += ty; }
                var center = new System.Numerics.Vector2(
                    (float)(sx / cluster.Count * cell), (float)(sy / cluster.Count * cell));
                result.Add(new Landmark(name, path, center, cluster.Count, curated));
            }
        }
        return result;
    }

    public static bool IsMapBossArenaTile(string areaCode, string path)
        => areaCode.StartsWith("Map", StringComparison.OrdinalIgnoreCase)
           && (path.Contains("BossArena", StringComparison.OrdinalIgnoreCase)
               || path.Contains("BossRoom", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Group same-path tile cells into spatially-disjoint clusters: two cells join when within a
    /// Chebyshev gap of <c>≤ gap</c> tiles (gap=2 bridges a one-tile hole inside a feature while
    /// keeping well-separated copies apart; larger merges more, 0 = only directly-touching cells).
    /// Plain BFS over a cell set — O(tiles) for the small kept-path counts, so a tile type that recurs
    /// across the map yields one cluster per location instead of a single meaningless average.
    /// </summary>
    private static List<List<(int tx, int ty)>> ClusterTiles(List<(int tx, int ty)> cells, int gap)
    {
        var set = new HashSet<(int, int)>(cells);
        var visited = new HashSet<(int, int)>();
        var clusters = new List<List<(int tx, int ty)>>();
        var queue = new Queue<(int, int)>();
        foreach (var start in cells)
        {
            if (!visited.Add(start)) continue;
            var cluster = new List<(int tx, int ty)>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                cluster.Add((cx, cy));
                for (var dx = -gap; dx <= gap; dx++)
                    for (var dy = -gap; dy <= gap; dy++)
                    {
                        var nb = (cx + dx, cy + dy);
                        if (set.Contains(nb) && visited.Add(nb)) queue.Enqueue(nb);
                    }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    /// <summary>Null for null/empty, else the string — so an empty user label means "surface but use the
    /// path-derived name" rather than showing a blank curated label.</summary>
    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string LandmarkName(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Bound terrain allocations while accepting the live Azmerian Ranges grid.</summary>
    public static bool IsPlausibleTerrainGrid(long totalBytes, int bytesPerRow)
    {
        if (bytesPerRow is <= 0 or > 65536 || totalBytes is <= 0 or > 64 * 1024 * 1024
            || totalBytes % bytesPerRow != 0) return false;
        var rows = totalBytes / bytesPerRow;
        return rows is > 0 and <= 65536 && (long)bytesPerRow * 2 * rows <= 32_000_000;
    }

    /// <summary>Read the packed walkable grid (one nibble per cell, 2 cells/byte) into a flat 0/1 array.</summary>
    public TerrainData? Terrain(nint areaInstance)
    {
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        var first = Ptr(terrain + Poe2.Terrain.GridWalkableData);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.GridWalkableData + 8, out var last) || last == 0) return null;
        if (!_reader.TryReadStruct<int>(terrain + Poe2.Terrain.BytesPerRow, out var bytesPerRow)) return null;
        var totalBytes = (long)last - (long)first;
        if (first == 0 || !IsPlausibleTerrainGrid(totalBytes, bytesPerRow)) return null;

        var rows = (int)(totalBytes / bytesPerRow);
        var width = bytesPerRow * 2;

        var raw = new byte[totalBytes];
        if (_reader.TryReadBytes(first, raw) != raw.Length) return null;

        var walk = new byte[width * rows];
        for (var y = 0; y < rows; y++)
        {
            var rowBase = (long)y * bytesPerRow;
            for (var x = 0; x < width; x++)
            {
                var b = raw[rowBase + (x >> 1)];
                var nibble = (x & 1) == 0 ? (b & 0x0F) : (b >> 4);
                walk[y * width + x] = (byte)(nibble != 0 ? 1 : 0);
            }
        }
        return new TerrainData(walk, width, rows);
    }

    private readonly List<nint> _mapEls = new();
    private readonly HashSet<nint> _everHidden = new();  // elements observed with visible-bit clear
    private readonly HashSet<nint> _everVisible = new(); // elements observed with visible-bit set
    private nint _mapCacheKey = -1;

    /// <summary>
    /// Map UI state. The MapUiElements (DefaultShift=(0,-20), Zoom=0.5) are discovered once per area
    /// and cached — per frame we only read their flags/shift/zoom (cheap). The game exposes several:
    /// some are always-on, some always-off, and one is the minimap viewport whose visible bit Tab
    /// toggles. We gate "map open" on a *genuine toggler* — an element observed BOTH visible and
    /// hidden — so a permanently-hidden element can't masquerade as the toggle signal (the bug that
    /// pinned this to "closed" once the UI began exposing 4 elements instead of 2). Projection
    /// params (shift/zoom) come from a currently-visible toggler. Until the first toggle is observed
    /// this area, fall back to "more than the always-on baseline visible" (&gt;=2).
    /// </summary>
    public MapUi ReadMap(nint inGameState, nint areaInstance)
    {
        if (areaInstance != _mapCacheKey || _mapEls.Count == 0)
        {
            _mapCacheKey = areaInstance;
            _mapEls.Clear();
            _everHidden.Clear();
            _everVisible.Clear();
            DiscoverMapElements(inGameState);
        }

        var visibleCount = 0;
        var any = false; MapUi anyUi = default;
        var sawToggler = false; var togglerVisible = false; var haveTogglerUi = false; MapUi togglerUi = default;
        foreach (var el in _mapEls)
        {
            if (!TryReadMapElement(el, out var vis, out var sx, out var sy, out var zoom)) continue;
            if (vis) { _everVisible.Add(el); visibleCount++; } else _everHidden.Add(el);
            if (!any) { any = true; anyUi = new MapUi(vis, sx, sy, zoom); }

            // A genuine toggler has been seen in BOTH states; permanently-on/off elements never qualify.
            if (_everVisible.Contains(el) && _everHidden.Contains(el))
            {
                sawToggler = true;
                if (vis) togglerVisible = true;
                if (vis || !haveTogglerUi) { togglerUi = new MapUi(vis, sx, sy, zoom); haveTogglerUi = true; }
            }
        }
        if (!any) return default;

        if (sawToggler)
            return new MapUi(togglerVisible, togglerUi.ShiftX, togglerUi.ShiftY, togglerUi.Zoom);

        // No toggle observed yet this area: the open minimap lights up one element beyond the
        // always-on baseline, so >=2 visible ≈ open. Superseded as soon as a real toggle is seen.
        return new MapUi(visibleCount >= 2, anyUi.ShiftX, anyUi.ShiftY, anyUi.Zoom);
    }

    private void DiscoverMapElements(nint inGameState)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return;
        var queue = new Queue<nint>(); queue.Enqueue(uiRoot);
        var visited = new HashSet<nint>();
        var body = new byte[Poe2.MapUiElement.Zoom + 8];
        while (queue.Count > 0 && visited.Count < 30000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el)) continue;

            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.Children + 8, out var lastC))
            {
                var n = ((long)lastC - (long)first) / 8;
                if (n is > 0 and <= 8192)
                    for (long k = 0; k < n; k++) queue.Enqueue(Ptr(first + (nint)(k * 8)));
            }

            if (_reader.TryReadBytes(el, body) < body.Length) continue;
            if (BitConverter.ToSingle(body, Poe2.MapUiElement.DefaultShift) != 0f) continue;
            if (BitConverter.ToSingle(body, Poe2.MapUiElement.DefaultShift + 4) != -20f) continue;
            var zoom = BitConverter.ToSingle(body, Poe2.MapUiElement.Zoom);
            if (zoom is <= 0.05f or >= 8f) continue;
            _mapEls.Add(el);
        }
    }

    private bool TryReadMapElement(nint el, out bool visible, out float shiftX, out float shiftY, out float zoom)
    {
        visible = false; shiftX = shiftY = zoom = 0;
        if (!_reader.TryReadStruct<float>(el + Poe2.MapUiElement.DefaultShift + 4, out var dsy) || dsy != -20f) return false;
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Shift, out shiftX);
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Shift + 4, out shiftY);
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Zoom, out zoom);
        visible = IsVisible(el);
        return true;
    }

    /// <summary>Element's own visibility bit (0x0B of Flags). Note: full visibility is hierarchical.</summary>
    public bool IsVisible(nint element)
    {
        if (!_reader.TryReadStruct<uint>(element + Poe2.UiElement.Flags, out var flags)) return false;
        return (flags & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;
    }

    private Vector3? EntityWorld(nint entity)
    {
        if (!_renderAddr.TryGetValue(entity, out var render))
        {
            render = ResolveComponent(entity, "Render");
            _renderAddr[entity] = render; // cache even if 0, to avoid re-walking
        }
        if (render == 0) return null;
        if (!_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out var w)) return null;
        return w;
    }

    private System.Numerics.Vector2? EntityGrid(nint entity)
        => EntityWorld(entity) is { } w ? new System.Numerics.Vector2(w.X / Poe2.WorldToGridRatio, w.Y / Poe2.WorldToGridRatio) : null;

    /// <summary>Chest opened state. The 2026-06-06 patch INVERTED this flag: Chest +0x168 is now 0
    /// while closed/openable and non-zero once opened/used (was the reverse). Validated live by diffing
    /// one rare chest closed-vs-opened — only +0x168 flipped (0→1; loot/interaction pointers nulled).
    /// A read failure returns not-opened (i.e. shows the chest): for chests, over-showing is far safer
    /// than silently hiding a real one — which is exactly the bug this flip caused.</summary>
    private bool ReadChestOpened(nint entity)
    {
        if (!_chestAddr.TryGetValue(entity, out var c)) { c = ResolveComponent(entity, "Chest"); _chestAddr[entity] = c; }
        if (c == 0) return false;
        return _reader.TryReadStruct<byte>(c + Poe2.ChestComponent.OpenState, out var b) && b != 0;
    }

    private bool ReadShrineUsed(nint entity)
    {
        if (!_shrineAddr.TryGetValue(entity, out var shrine)) { shrine = ResolveComponent(entity, "Shrine"); _shrineAddr[entity] = shrine; }
        return shrine != 0 && _reader.TryReadStruct<int>(shrine + Poe2.ShrineComponent.Used, out var used) && used == 1;
    }

    private EntityCategory Categorize(nint entity)
    {
        if (_category.TryGetValue(entity, out var c)) return c;
        var meta = ReadMetadata(entity);
        _meta[entity] = meta;
        c = meta switch
        {
            // NPCs FIRST: friendly NPCs (Alva, vendors…) live under "Metadata/Monsters/NPC/…", so the
            // "/NPC/" check must precede "/Monsters/" or they'd be miscategorized as combat monsters
            // (and a Unique-rarity NPC would draw the enemy unique star). "/NPC/" is the NPC marker.
            _ when meta.Contains("/NPC/", StringComparison.Ordinal)         => EntityCategory.Npc,
            // Real combat monsters only — exclude on-death/aura effect carriers (MonsterMods),
            // player/ally summons, and invisible effect daemons. Those clutter the map and aren't
            // fight targets. (Friendly/hostile is applied at draw time via Positioned.Reaction.)
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal) && IsNonCombat(meta) => EntityCategory.Other,
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal)   => EntityCategory.Monster,
            _ when meta.Contains("/Characters/", StringComparison.Ordinal)  => EntityCategory.Player,
            // Real chests only — exclude breakable props (urns/vases/pots/etc.) under /Chests/.
            _ when meta.Contains("/Chests", StringComparison.Ordinal) && IsBreakableProp(meta) => EntityCategory.Other,
            _ when meta.Contains("/Chests", StringComparison.Ordinal)       => EntityCategory.Chest,
            _ when meta.Contains("Transition", StringComparison.Ordinal)    => EntityCategory.Transition,
            _ when meta.Contains("/Terrain/", StringComparison.Ordinal)     => EntityCategory.Object,
            _                                                              => EntityCategory.Other,
        };
        _category[entity] = c;
        return c;
    }

    /// <summary>True for "/Chests/" entities that are destructible scenery (urns, vases, pots…) not loot chests.</summary>
    private static bool IsBreakableProp(string meta) =>
        meta.Contains("Urn", StringComparison.Ordinal) ||
        meta.Contains("Vase", StringComparison.Ordinal) ||
        meta.Contains("Pot", StringComparison.Ordinal) ||
        meta.Contains("Jar", StringComparison.Ordinal) ||
        meta.Contains("Sack", StringComparison.Ordinal) ||
        meta.Contains("Barrel", StringComparison.Ordinal) ||
        meta.Contains("Crate", StringComparison.Ordinal) ||
        meta.Contains("Debris", StringComparison.Ordinal) ||
        meta.Contains("Rubble", StringComparison.Ordinal) ||
        meta.Contains("Basket", StringComparison.Ordinal) ||
        meta.Contains("Coffin", StringComparison.Ordinal);

    /// <summary>True for "/Monsters/" entities that aren't real fight targets (effects / summons).</summary>
    private static bool IsNonCombat(string meta) =>
        meta.Contains("MonsterMods", StringComparison.Ordinal) ||
        meta.Contains("Summoned", StringComparison.Ordinal) ||
        meta.Contains("/Daemon/", StringComparison.Ordinal) ||
        meta.Contains("Invisible", StringComparison.Ordinal);

    /// <summary>Resolve a component address by name via EntityDetails → ComponentLookUp (StdBucket) → ComponentList.</summary>
    private nint ResolveComponent(nint entity, string name)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return 0;
        var lookup = Ptr(details + Poe2.EntityDetails.ComponentLookUpPtr);
        if (lookup == 0) return 0;
        if (!_reader.TryReadStruct<StdVector>(entity + Poe2.Entity.ComponentList, out var compList)) return 0;
        var compCount = ((long)compList.Last - (long)compList.First) / 8;
        if (compCount is <= 0 or > 256) return 0;

        var bFirst = Ptr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
        if (!_reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return 0;
        var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
        if (bFirst == 0 || entries is <= 0 or > 256) return 0;

        for (long i = 0; i < entries; i++)
        {
            var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
            var namePtr = Ptr(e);
            if (!_reader.TryReadStruct<int>(e + 8, out var index)) continue;
            if (index < 0 || index >= compCount) continue;
            if (_reader.ReadStringUtf8(namePtr, 32) != name) continue;
            return Ptr(compList.First + (nint)(index * 8));
        }
        return 0;
    }

    /// <summary>Read an entity's metadata path: EntityDetails(+0x08) → name StdWString(+0x08).</summary>
    private string ReadMetadata(nint entity)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return string.Empty;
        return ReadStdWString(details + Poe2.EntityDetails.Name);
    }

    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(addr);
        return ptr == 0 ? string.Empty : _reader.ReadStringUtf16(ptr, len);
    }

    /// <summary>Safe pointer read: 0 on failure or implausible (non-user-mode) value.</summary>
    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }
}
