using System.Text.Json;

namespace pathofsight;

public record PoiFilter(string Id, string Name, string Section, bool Available = true, string Note = "");

public static class PoiCatalog
{
    public static readonly PoiFilter[] All =
    [
        new("ritual", "Ritual", "Mechanics"), new("abyss", "Abyss", "Mechanics"),
        new("expedition", "Expedition", "Mechanics"), new("breach", "Breach", "Mechanics"),
        new("delirium", "Delirium", "Mechanics"), new("essence", "Essence", "Mechanics"),
        new("shrine", "Shrines", "Mechanics"),
        new("strongbox", "Strongboxes", "Mechanics", Note: "Live detection unverified"),
        new("azmeri", "Azmeri", "Mechanics", Note: "Live detection unverified"),
        new("sekhemas", "Trial of the Sekhemas", "Mechanics"),
        new("chaos", "Trial of Chaos", "Mechanics"), new("incursion", "Incursion", "Mechanics"),
        new("transition", "Area transitions", "Act / Quest"), new("quest", "Quest objects", "Act / Quest"),
        new("reward", "Rewards / permanent bonuses", "Act / Quest"),
        new("magic", "Magic", "Enemies"),
        new("rare", "Rare", "Enemies"),
        new("unique", "Unique", "Enemies"), new("arena", "Boss arenas", "Enemies"),
        new("map-boss", "Map boss", "Enemies"),
        new("waypoint", "Waypoints", "World & Objects"), new("checkpoint", "Checkpoints", "World & Objects"),
        new("chest", "Chests", "World & Objects"), new("npc", "NPC", "World & Objects"),
        new("poi", "Other POI", "World & Objects")
    ];
    public static readonly string[] Enabled = All.Where(x => x.Available).Select(x => x.Id).ToArray();
    public static readonly string[] Sections = All.Select(x => x.Section).Distinct().ToArray();
    public static bool IsMechanic(Poi poi) => All.Any(x => x.Id == poi.FilterCategory && x.Section == "Mechanics");

    public static string ShortName(Poi poi)
    {
        if (poi.Source == "entity" && poi.Kind == "rare") return "Rare";
        if (poi.Source == "entity" && poi.Kind == "boss") return "Boss";
        if (poi.Id.StartsWith("marker:boss:", StringComparison.Ordinal) || poi.FilterCategory == "map-boss"
            || poi.FilterCategory == "arena") return "BO$$";
        if (poi.Key.Contains("AbyssSubAreaTransition", StringComparison.OrdinalIgnoreCase)
            || poi.Name.Contains("AbyssSubAreaTransition", StringComparison.OrdinalIgnoreCase)) return "Abyss";
        return poi.FilterCategory switch
        {
            "ritual" => "Ritual", "abyss" => "Abyss", "expedition" => "Expedition",
            "breach" => "Breach", "delirium" => "Delirium", "essence" => "Essence",
            "shrine" => "Shrine", "strongbox" => "Strongbox", "azmeri" => "Azmeri",
            "sekhemas" => "Sekhemas", "chaos" => "Chaos", "incursion" => "Incursion",
            _ => poi.Name
        };
    }

    public static bool KeepMapPoi(string areaCode, Poi poi) => !areaCode.StartsWith("Map", StringComparison.OrdinalIgnoreCase)
        || poi.FilterCategory != "expedition"
        || poi.Key.EndsWith("/Expedition2Encounter", StringComparison.OrdinalIgnoreCase);

    public static string Classify(Poi poi)
    {
        if (poi.Kind is "magic" or "rare") return poi.Kind;
        var path = poi.Key.ToLowerInvariant();
        var metadata = path.IndexOf("metadata/", StringComparison.Ordinal);
        if (metadata >= 0) path = path[metadata..];
        if (poi.Id.StartsWith("marker:boss:", StringComparison.Ordinal)) return "map-boss";
        var separator = poi.Key.IndexOf(':');
        if (poi.Source == "tile" && separator > 0
            && POE2Radar.Core.Game.Poe2Live.IsMapBossArenaTile(poi.Key[..separator], poi.Key)) return "map-boss";
        if (poi.Kind == "boss") return poi.Source == "entity" ? "unique" : "arena";
        if (poi.Kind is "transition" or "waypoint" or "checkpoint") return poi.Kind;
        // Curated campaign rewards take priority over mechanic-looking words in their labels.
        if (poi.Kind == "quest") return Reward(poi.Name) ? "reward" : "quest";
        if (path.Contains("/quest") || path.EndsWith("quest", StringComparison.Ordinal)) return "quest";
        foreach (var (id, prefixes) in Mechanics)
            if (prefixes.Any(path.StartsWith)) return id;
        return poi.Kind is "npc" or "chest" or "shrine" ? poi.Kind : "poi";
    }

    private static bool Reward(string name) => new[] { "passive", "gem", "reward", "bonus", "res)", "spirit" }
        .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    // Match namespaces, not arbitrary words (RitualClearing and DoryanisSanctum are campaign landmarks).
    private static readonly (string Id, string[] Prefixes)[] Mechanics =
    [
        ("ritual", ["metadata/terrain/leagues/ritual/", "metadata/terrain/gallows/leagues/ritual/", "metadata/miscellaneousobjects/ritual", "metadata/npc/league/ritual/"]),
        ("abyss", ["metadata/terrain/leagues/abyss/", "metadata/terrain/gallows/leagues/abyss/", "metadata/miscellaneousobjects/abyss/", "metadata/npc/league/abyss/"]),
        ("expedition", ["metadata/terrain/leagues/expedition/", "metadata/terrain/gallows/leagues/expedition/", "metadata/miscellaneousobjects/expedition/", "metadata/miscellaneousobjects/expedition2/", "metadata/npc/league/expedition/", "metadata/npc/four_endgame/expedition/"]),
        ("breach", ["metadata/terrain/leagues/breach/", "metadata/terrain/gallows/leagues/breach/", "metadata/miscellaneousobjects/breach", "metadata/miscellaneousobjects/brequel/brequelinitiator", "metadata/npc/league/breach/"]),
        ("delirium", ["metadata/terrain/leagues/delirium/", "metadata/terrain/gallows/leagues/delirium/", "metadata/miscellaneousobjects/delirium"]),
        ("essence", ["metadata/terrain/leagues/essence/", "metadata/miscellaneousobjects/essence", "metadata/chests/essence"]),
        ("strongbox", ["metadata/chests/strongboxes/", "metadata/chests/strongbox"]),
        ("azmeri", ["metadata/terrain/woods/woods/azmerileague/", "metadata/miscellaneousobjects/azmeri/", "metadata/terrain/gallows/leagues/azmeri/"]),
        ("sekhemas", ["metadata/terrain/gallows/leagues/sanctum/", "metadata/miscellaneousobjects/sanctum/", "metadata/npc/league/sanctum/"]),
        ("chaos", ["metadata/terrain/gallows/leagues/ultimatum/", "metadata/miscellaneousobjects/ultimatum/", "metadata/npc/league/ultimatum/"]),
        ("incursion", ["metadata/terrain/gallows/leagues/incursion/", "metadata/miscellaneousobjects/leagueincursionnew/", "metadata/npc/league/incursion/"])
    ];
}

public sealed record ProfileSettings
{
    public int Version { get; init; } = 3;
    public string Active { get; init; } = "Default";
    public Dictionary<string, DisplaySettings> Profiles { get; init; } = Names.ToDictionary(x => x, Defaults);
    public static readonly string[] Names = ["Default", "Act Rush", "Atlas Boss Rush", "Custom 1", "Custom 2", "Custom 3"];
    public static DisplaySettings Defaults(string name) => name switch
    {
        "Act Rush" => new() { Categories = ["transition", "quest", "reward", "npc", "unique", "arena", "map-boss", "waypoint", "checkpoint"], Routing = "off" },
        "Atlas Boss Rush" => new() { Categories = ["map-boss"], Routing = "atlas" },
        _ => new()
    };
    public bool Valid() => Version == 3 && Names.Contains(Active) && Profiles != null
        && Profiles.Count == Names.Length && Names.All(n => Profiles.TryGetValue(n, out var p) && p != null && p.Valid());
}

public record SettingsView(string Profile, string Instance, long Revision, long ProfileEpoch,
    DisplaySettings Settings, DisplaySettings ProfileSettings, bool Temporary);

public sealed class SettingsStore
{
    private ProfileSettings _profiles = new();
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _filters = new();
    private string[]? _hidden;
    private string _instance = "";
    private long _revision;
    private long _profileEpoch;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SettingsView _view = new("Default", "", 0, 0, new(), new(), false);
    public SettingsView Read() => Volatile.Read(ref _view);
    public DisplaySettings Value => Read().Settings;
    public string? LoadWarning { get; }

    public SettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("version", out _))
                {
                    var loaded = JsonSerializer.Deserialize<ProfileSettings>(text, Json);
                    if (loaded?.Profiles == null || loaded.Profiles.Any(x => x.Value?.Categories == null))
                        throw new InvalidDataException("Invalid profiles");
                    var cleaned = loaded.Version == 2 ? loaded with { Version = 3,
                        Profiles = loaded.Profiles.ToDictionary(x => x.Key,
                            x => x.Value with { Categories = x.Value.Categories.Except(["normal", "magic"]).ToArray() }) } : loaded;
                    if (!cleaned.Valid()) throw new InvalidDataException("Invalid profiles");
                    _profiles = cleaned;
                    if (loaded.Version == 2)
                    {
                        if (!File.Exists(_path + ".before-monster-filters.bak"))
                            File.Copy(_path, _path + ".before-monster-filters.bak");
                        Persist(cleaned);
                    }
                }
                else
                {
                    var legacy = JsonSerializer.Deserialize<DisplaySettings>(text, Json);
                    if (legacy == null) throw new InvalidDataException("Invalid settings");
                    var categories = doc.RootElement.TryGetProperty("categories", out var old)
                        ? old.Deserialize<string[]>(Json) : DisplaySettings.Kinds;
                    if (categories == null || categories.Any(x => !DisplaySettings.Kinds.Contains(x)))
                        throw new InvalidDataException("Invalid legacy categories");
                    legacy = legacy with { Routing = "atlas", Categories = categories.SelectMany(x => x switch
                    {
                        "boss" => new[] { "unique", "arena", "map-boss" },
                        "quest" => ["quest", "reward"],
                        "poi" => PoiCatalog.Enabled.Except(["magic", "rare", "unique", "arena", "map-boss", "quest", "reward", "transition", "waypoint", "checkpoint", "shrine"]),
                        _ => [x]
                    }).Distinct().ToArray() };
                    if (!legacy.Valid()) throw new InvalidDataException("Invalid legacy settings");
                    var migrated = new ProfileSettings { Active = "Custom 1" };
                    migrated.Profiles["Custom 1"] = legacy;
                    File.Copy(_path, _path + ".v1.bak", false);
                    Persist(migrated);
                    _profiles = migrated;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { LoadWarning = "Could not read settings; using defaults. " + e.Message; }
        Publish();
    }

    private void Persist(ProfileSettings profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(profiles, Json));
        File.Move(_path + ".tmp", _path, true);
    }
    private void Publish()
    {
        var profile = _profiles.Profiles[_profiles.Active];
        var effective = profile with
        {
            Categories = PoiCatalog.Enabled.Where(id => _filters.GetValueOrDefault(id, profile.Categories.Contains(id))).ToArray(),
            Hidden = _hidden ?? profile.Hidden
        };
        Volatile.Write(ref _view, new(_profiles.Active, _instance, _revision, _profileEpoch, effective, profile,
            _filters.Count > 0 || _hidden != null));
    }
    public void SetInstance(string instance)
    {
        lock (_gate)
        {
            if (_instance == instance) return;
            _instance = instance; _filters.Clear(); _hidden = null; _revision++; Publish();
        }
    }
    public void SelectProfile(string name, bool reset = false)
    {
        if (!ProfileSettings.Names.Contains(name)) throw new ArgumentException("Unknown profile");
        lock (_gate)
        {
            var profiles = new Dictionary<string, DisplaySettings>(_profiles.Profiles);
            if (reset) profiles[name] = ProfileSettings.Defaults(name);
            var next = _profiles with { Active = name, Profiles = profiles };
            Persist(next); _profiles = next;
            _filters.Clear(); _hidden = null; _revision++; _profileEpoch++; Publish();
        }
    }
    public void Update(Func<DisplaySettings, DisplaySettings> change, string? changedFilter = null)
    {
        lock (_gate) SaveCore(change(_profiles.Profiles[_profiles.Active]), changedFilter);
    }
    public void Save(DisplaySettings value) { lock (_gate) SaveCore(value); }
    private void SaveCore(DisplaySettings value, string? changedFilter = null)
    {
        if (!value.Valid()) throw new ArgumentException("Invalid settings");
        var previous = _profiles.Profiles[_profiles.Active];
        var next = _profiles with { Profiles = new(_profiles.Profiles) { [_profiles.Active] = value } };
        Persist(next); _profiles = next;
        if (previous.Routing != value.Routing) _profileEpoch++;
        foreach (var id in PoiCatalog.Enabled)
            if (id == changedFilter || previous.Categories.Contains(id) != value.Categories.Contains(id)) _filters.Remove(id);
        _revision++; Publish();
    }
    public bool WebSave(string profile, string instance, long revision, DisplaySettings value, bool filters, bool reset = false)
    {
        if (!value.Valid()) throw new ArgumentException("Invalid settings");
        lock (_gate)
        {
            if (profile != _profiles.Active || instance != _instance || revision != _revision) return false;
            if (!filters)
            {
                var current = _profiles.Profiles[_profiles.Active];
                SaveCore(value with { Categories = current.Categories, Hidden = current.Hidden, Routing = current.Routing });
            }
            else
            {
                if (instance.Length == 0) return false;
                _filters.Clear(); _hidden = null;
                if (!reset)
                {
                    var current = _profiles.Profiles[_profiles.Active];
                    foreach (var id in PoiCatalog.Enabled)
                        if (value.Categories.Contains(id) != current.Categories.Contains(id)) _filters[id] = value.Categories.Contains(id);
                    if (!value.Hidden.SequenceEqual(current.Hidden)) _hidden = value.Hidden.ToArray();
                }
                _revision++; Publish();
            }
            return true;
        }
    }
}
