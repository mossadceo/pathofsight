using System.Text.Json.Serialization;
using POE2Radar.Core.Game;

namespace pathofsight;

public record Point(float X, float Y)
{
    public float Distance(Point p) => MathF.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y));
    public bool In(Poe2Live.TerrainData t) => float.IsFinite(X) && float.IsFinite(Y) && X >= 0 && Y >= 0 && X < t.Width && Y < t.Height;
}

public record Poi(string Id, string Key, string Name, string Kind, Point Position,
    string Source, bool Remembered = false, bool Completed = false, string? Filter = null)
{
    public string FilterCategory => Filter ?? PoiCatalog.Classify(this);
}
public record MapSnapshot(string Status, string Message, long Updated, string Instance = "", string Area = "",
    Point? Player = null, [property: JsonIgnore] Poi[]? Points = null, Point[]? Route = null, string? Selected = null,
    string RouteStatus = "", int Width = 0, int Height = 0, string GameVersion = "",
    [property: JsonIgnore] Poe2Live.TerrainData? Terrain = null,
    [property: JsonIgnore] TerrainPicture? Picture = null,
    [property: JsonIgnore] Poe2Live.MapUi MapUi = default,
    [property: JsonIgnore] int ProcessId = 0,
    [property: JsonIgnore] long PlayerAddress = 0,
    [property: JsonIgnore] long SettingsEpoch = 0)
{
    public Poi[] Targets => Points ?? [];
    [JsonIgnore] public bool Fresh => Environment.TickCount64 - Updated < 2500;
    public static MapSnapshot Empty(string status, string message) => new(status, message, Environment.TickCount64);
}

public record DisplaySettings
{
    public bool Overlay { get; init; } = true;
    public bool Labels { get; init; } = true;
    public bool Terrain { get; init; } = true;
    public double Opacity { get; init; } = .85;
    public double IconSize { get; init; } = 6;
    public double Scale { get; init; } = 1;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public string[] Categories { get; init; } = PoiCatalog.Enabled.Except(["magic"]).ToArray();
    public string Routing { get; init; } = "manual";
    public Dictionary<string, string> Names { get; init; } = new();
    public string[] Hidden { get; init; } = [];
    public bool Shows(Poi p) => Categories.Contains(p.FilterCategory) && !Hidden.Contains(p.Key);
    public string Label(Poi p) => Names.GetValueOrDefault(p.Key, p.Name);
    public static readonly string[] Kinds = ["transition", "waypoint", "checkpoint", "boss", "quest", "shrine", "poi"];
    public bool Valid() => double.IsFinite(Opacity) && Opacity is >= .1 and <= 1
        && double.IsFinite(IconSize) && IconSize is >= 3 and <= 16
        && double.IsFinite(Scale) && Scale is >= .25 and <= 4
        && double.IsFinite(OffsetX) && Math.Abs(OffsetX) <= 2000
        && double.IsFinite(OffsetY) && Math.Abs(OffsetY) <= 2000
        && Routing is "off" or "manual" or "atlas"
        && Categories != null && Categories.Length <= PoiCatalog.All.Length && Categories.All(PoiCatalog.Enabled.Contains)
        && Hidden != null && Hidden.Length <= 10000 && Hidden.All(x => x is { Length: <= 1000 })
        && Names != null && Names.Count <= 10000 && Names.All(x => x.Key.Length <= 1000 && x.Value is { Length: <= 160 });
}

// One tracker per instance. Never infer a specific mechanic from the mere presence of an icon.
public sealed class PoiTracker
{
    private readonly Dictionary<string, Poi> _objects = new();
    private Poi[] _landmarks = [];
    public string Instance { get; private set; } = "";
    public void Reset(string instance, IEnumerable<Poi> landmarks)
    {
        Instance = instance;
        _objects.Clear();
        _landmarks = landmarks.ToArray();
    }
    public Poi[] Update(IEnumerable<Poi> observed)
    {
        foreach (var id in _objects.Keys.ToArray()) _objects[id] = _objects[id] with { Remembered = true };
        var moving = new List<Poi>();
        foreach (var p in observed)
            if (p.Source == "entity" && p.Kind is "boss" or "npc" or "magic" or "rare") moving.Add(p);
            else _objects[p.Id] = p with { Remembered = false };
        var result = _objects.Values.Concat(moving).ToList();
        var used = new HashSet<string>();
        foreach (var tile in _landmarks)
        {
            // Match a unique nearby object of the same category, with an equal or generic name.
            // Nearby checkpoint/exit pairs and ambiguous generic landmarks must remain distinct.
            var candidates = result.Where(e => e.Source == "entity" && !used.Contains(e.Id) && e.Kind == tile.Kind && tile.Kind != "poi"
                && e.Position.Distance(tile.Position) <= 23).ToArray();
            var candidate = candidates.Length == 1 ? candidates[0] : null;
            var match = candidate != null && _landmarks.Count(l => l.Kind == candidate.Kind && l.Position.Distance(candidate.Position) <= 23) == 1
                && (string.Equals(candidate.Name, tile.Name, StringComparison.OrdinalIgnoreCase)
                    || candidate.Name is "Area Transition" or "Waypoint" or "Checkpoint" or "Shrine") ? candidate : null;
            if (match is null) result.Add(tile);
            else
            {
                used.Add(match.Id);
                used.Add(tile.Id);
                var at = result.IndexOf(match);
                result[at] = match with { Id = tile.Id, Key = tile.Key, Name = tile.Name, Filter = tile.Filter ?? tile.FilterCategory };
            }
        }
        return result.OrderBy(p => p.Kind).ThenBy(p => p.Name).ToArray();
    }
    public static string Kind(string path, string label, bool entity = false)
    {
        var p = path.ToLowerInvariant(); var text = (path + " " + label).ToLowerInvariant();
        if (text.Contains("checkpoint")) return "checkpoint";
        if (text.Contains("waypoint")) return "waypoint";
        if (entity && p.StartsWith("metadata/shrines/")) return "shrine";
        if (!entity && (p.Contains("boss") || text.Contains("arena"))) return "boss";
        if (p.Contains("areatransition") || p.Contains("transition")) return "transition";
        if (!entity && (text.Contains("passive") || text.Contains("gem") || text.Contains("quest") || text.Contains("reward"))) return "quest";
        return "poi";
    }
    public static Poi? FromEntity(Poe2Live.EntityDot e)
    {
        if (e.Category == Poe2Live.EntityCategory.Monster)
        {
            if (!Poe2Live.IsVisibleMonster(e.Rarity, e.HpCur, e.HpMax, e.Reaction)) return null;
            var monsterKind = e.Rarity switch
            {
                Poe2Live.Rarity.Magic => "magic",
                Poe2Live.Rarity.Rare => "rare", _ => "boss"
            };
            return new($"entity:{e.Id}", e.Metadata, EntityNameResolver.Shared.ResolveOrShorten(e.Metadata), monsterKind,
                new(e.Grid.X, e.Grid.Y), "entity");
        }
        // Moving entities must not be remembered as permanent map objectives.
        if (e.Category == Poe2Live.EntityCategory.Player) return null;
        var name = EntityNameResolver.Shared.ResolveOrShorten(e.Metadata);
        var abyss = e.Metadata.Contains("Abyss", StringComparison.OrdinalIgnoreCase) || name.Contains("Abyss", StringComparison.OrdinalIgnoreCase);
        if (abyss && (e.Metadata.Contains("Crack", StringComparison.OrdinalIgnoreCase)
            || e.Metadata.Contains("Fissure", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Crack", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Fissure", StringComparison.OrdinalIgnoreCase))) return null;
        if (name.StartsWith("AreaTransition", StringComparison.OrdinalIgnoreCase)) name = "Area Transition";
        var kind = Kind(e.Metadata, name, true);
        if (e.Category == Poe2Live.EntityCategory.Npc) kind = "npc";
        if (e.Category == Poe2Live.EntityCategory.Chest && kind == "poi") kind = "chest";
        if (!e.Poi && e.Category != Poe2Live.EntityCategory.Transition && kind == "poi") return null;
        if (e.Category == Poe2Live.EntityCategory.Transition) kind = "transition";
        return new($"entity:{e.Id}", e.Metadata, name, kind,
            new(e.Grid.X, e.Grid.Y), "entity", Completed: e.IconComplete || e.Opened);
    }
}
