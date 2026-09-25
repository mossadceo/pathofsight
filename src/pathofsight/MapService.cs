using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;

namespace pathofsight;

public sealed class MapService : IDisposable
{
    private MapSnapshot _snapshot = MapSnapshot.Empty("waiting", "Waiting for connection command");
    public MapSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public SettingsStore Settings { get; }
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<(string Instance, string? Id)> _selections = new();
    private readonly PoiTracker _tracker = new();
    private PathPlanner _planner = new();
    private string? _selected;
    private Point[] _route = [];
    private string _routeStatus = "";
    private long _lastPlan;
    private volatile bool _demo;
    private volatile bool _connected;
    private int _demoReset;
    private int _demoDiscover;
    public bool IsDemo => _demo;
    public string DataDirectory { get; }
    private readonly Task _worker;
    public MapService(string dataDirectory, bool demo = false, bool autoConnect = true)
    {
        DataDirectory = dataDirectory;
        Settings = new(System.IO.Path.Combine(dataDirectory, "settings.json"));
        _demo = demo;
        _connected = autoConnect;
        _worker = Task.Run(Run);
    }
    public void Connect() => _connected = true;
    public void Demo(bool enabled) { _demo = enabled; Interlocked.Increment(ref _demoReset); }
    public void DiscoverDemo() => Interlocked.Increment(ref _demoDiscover);
    public bool Select(string instance, string? id)
    {
        var s = Snapshot;
        if (!s.Fresh || instance != s.Instance || s.Terrain == null || (id != null && !s.Targets.Any(x => x.Id == id))) return false;
        _selections.Enqueue((instance, id)); return true;
    }
    private void Publish(MapSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
    private void Clear(string status, string message)
    {
        _tracker.Reset("", []); _selected = null; _route = []; _routeStatus = "";
        while (_selections.TryDequeue(out _)) { }
        Publish(MapSnapshot.Empty(status, message));
    }
    private void Reset(string key, IEnumerable<Poi> landmarks)
    {
        _tracker.Reset(key, landmarks); _planner = new(); _selected = null; _route = []; _lastPlan = 0; _routeStatus = "";
    }
    private void Route(string key, Poe2Live.TerrainData terrain, Point player, Poi[] points)
    {
        var changed = false;
        while (_selections.TryDequeue(out var command))
            if (command.Instance == key) { _selected = command.Id; changed = true; }
        var target = points.FirstOrDefault(p => p.Id == _selected);
        if (target is null) { _selected = null; _route = []; _routeStatus = ""; return; }
        if (!changed && Environment.TickCount64 - _lastPlan < 1000) return;
        _lastPlan = Environment.TickCount64;
        _route = _planner.Plan(terrain, ((int)player.X, (int)player.Y), ((int)target.Position.X, (int)target.Position.Y), 250000)
            .Select(p => new Point(p.x, p.y)).ToArray();
        _routeStatus = _route.Length == 0 ? "Путь не найден" : target.Source == "tile" ? "Маршрут к ориентиру (приблизительно)" : "Маршрут к объекту";
    }
    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (!_connected)
            {
                try { await Task.Delay(100, _stop.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                if (_demo) await RunDemo(); else await RunGame();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Win32Exception e) when (e.NativeErrorCode == 5)
            { Clear("access", "Insufficient permissions to read PoE2. Restart Path Of Sight as administrator."); Log(e); }
            catch (Exception e)
            { Clear("error", "Could not read game. Retrying connection… See the log for details."); Log(e); }
            try { await Task.Delay(1500, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task RunGame()
    {
        // The upstream names overlap PoE1; require an explicit PoE2 window/title or product identity.
        var processId = FindGame();
        if (processId == 0) { Clear("waiting", "Waiting for PoE2. Start the game and enter an area."); return; }
        using var proc = ProcessHandle.AttachToProcess(processId);
        using var lifetime = Process.GetProcessById(processId);
        var reader = new MemoryReader(proc);
        var version = FileVersionInfo.GetVersionInfo(proc.ModulePath).FileVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            using var executable = File.OpenRead(proc.ModulePath);
            version = "SHA256:" + Convert.ToHexString(SHA256.HashData(executable))[..16];
        }
        Clear("loading", "Scanning PoE2 data…");
        var slots = AobPatterns.GameStateRefs.SelectMany(p => AobScanner.ScanForResolvedAddresses(proc, reader, p)).Distinct().ToArray();
        if (slots.Length == 0)
        {
            Clear("incompatible", $"PoE2 version {version}: signature not found. Compatibility update required.");
            await Task.Delay(10000, _stop.Token); return;
        }
        Poe2Live? live = null;
        nint selectedSlot = 0;
        Poe2Live.TerrainData? terrain = null;
        TerrainPicture? picture = null;
        string instance = "";
        var session = Guid.NewGuid().ToString("N")[..8];
        var unresolvedSince = Environment.TickCount64;
        while (!_demo && !_stop.IsCancellationRequested && !lifetime.HasExited)
        {
            nint igs = 0, area = 0, playerAddress = 0;
            if (live is null)
            {
                foreach (var slot in slots)
                {
                    var candidate = new Poe2Live(reader, slot);
                    if (!candidate.TryResolve(out igs, out area, out playerAddress)) continue;
                    live = candidate; selectedSlot = slot; break;
                }
            }
            if (live is null || !live.TryResolve(out igs, out area, out playerAddress))
            {
                Clear("loading", Environment.TickCount64 - unresolvedSince > 30000
                    ? "No character data. Enter an area; if you already have, this game version may be incompatible."
                    : "Loading area / selecting character…");
                terrain = null; picture = null; live = null; instance = "";
                await Task.Delay(1000, _stop.Token); continue;
            }
            unresolvedSince = Environment.TickCount64;
            var key = $"{session}:{area:X}:{live.AreaHash(area):X8}";
            if (key != instance || terrain is null)
            {
                Clear("loading", "Reading terrain and landmarks…");
                // Recreate all upstream address-keyed caches even if an allocation address is reused.
                live = new Poe2Live(reader, selectedSlot);
                terrain = live.Terrain(area);
                if (terrain is null || !terrain.Walkable.Any(x => x != 0))
                {
                    Clear("incompatible", $"PoE2 {version}: terrain validation failed. Map hidden.");
                    await Task.Delay(3000, _stop.Token); continue;
                }
                var landmarks = live.Landmarks(area).Select(l => new Poi("tile:" + l.Key,
                    live.AreaCode(area) + ":" + l.Path, l.CuratedName ?? l.Name, PoiTracker.Kind(l.Path, l.CuratedName ?? l.Name),
                    new(l.Center.X, l.Center.Y), "tile")).Where(p => p.Position.In(terrain));
                Reset(key, landmarks);
                picture = TerrainPicture.Create(terrain);
                instance = key;
            }
            var points = _tracker.Update(live.Entities(area, pointsOfInterestOnly: true).Select(PoiTracker.FromEntity)
                .OfType<Poi>().Where(p => p.Position.In(terrain)).Select(p => p with { Key = live.AreaCode(area) + ":" + p.Key }));
            var grid = live.PlayerGrid(playerAddress);
            var position = grid is { } g ? new Point(g.X, g.Y) : null;
            if (position is null || !position.In(terrain))
            {
                Clear("incompatible", "Invalid character position. Map hidden.");
                instance = ""; terrain = null;
                await Task.Delay(1000, _stop.Token); continue;
            }
            Route(key, terrain, position, points);
            var map = live.ReadMap(igs, area);
            if (!float.IsFinite(map.Zoom) || map.Zoom is <= .05f or >= 8f
                || !float.IsFinite(map.ShiftX) || !float.IsFinite(map.ShiftY)) map = default;
            // A transition can occur while reading terrain/entities. Never publish a mixed frame.
            if (!live.TryResolve(out _, out var finalArea, out _) || finalArea != area
                || key != $"{session}:{finalArea:X}:{live.AreaHash(finalArea):X8}")
            { Clear("loading", "Changing area…"); terrain = null; instance = ""; continue; }
            Publish(new("ready", "Data received", Environment.TickCount64,
                key, live.AreaName(area), position, points, _route, _selected, _routeStatus, terrain.Width, terrain.Height,
                version, terrain, picture, map, processId, (long)playerAddress));
            await Task.Delay(100, _stop.Token);
        }
        Clear("waiting", "Waiting for PoE2");
    }
    private static int FindGame()
    {
        foreach (var name in new[] { "PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExile_KG", "PathOfExileEGS", "PathOfExile2" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var p in processes)
                {
                    var title = p.MainWindowTitle;
                    if (title.Contains("Path of Exile 2", StringComparison.OrdinalIgnoreCase)) return p.Id;
                    try
                    {
                        if (p.MainModule?.FileVersionInfo.ProductName?.Contains("Path of Exile 2", StringComparison.OrdinalIgnoreCase) == true) return p.Id;
                    }
                    catch (Win32Exception) { /* Title remains usable for protected/elevated processes. */ }
                }
            }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        return 0;
    }
    private async Task RunDemo()
    {
        var reset = _demoReset; var discovery = _demoDiscover;
        var fixture = DemoFixture.Create();
        var key = "demo:" + Guid.NewGuid().ToString("N");
        Reset(key, fixture.Landmarks);
        var picture = TerrainPicture.Create(fixture.Terrain);
        var seen = false;
        while (_demo && reset == _demoReset && !_stop.IsCancellationRequested)
        {
            var observed = fixture.Objects;
            if (_demoDiscover != discovery && !seen) { seen = true; observed = fixture.Objects.Concat(fixture.Discovered).ToArray(); }
            var points = _tracker.Update(observed);
            Route(key, fixture.Terrain, fixture.Player, points);
            Publish(new("demo", "DEMO · simulated map, not game data", Environment.TickCount64,
                key, "Training area", fixture.Player, points, _route, _selected, _routeStatus,
                fixture.Terrain.Width, fixture.Terrain.Height, Terrain: fixture.Terrain, Picture: picture));
            await Task.Delay(100, _stop.Token);
        }
        Clear("waiting", "Waiting for PoE2");
    }
    private void Log(Exception e)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = System.IO.Path.Combine(DataDirectory, "pathofsight.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {e}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public Task Completion => _worker;
    public void Dispose() { _stop.Cancel(); /* worker owns and disposes the process handle */ }
}

public sealed record DemoFixture(Poe2Live.TerrainData Terrain, Point Player, Poi[] Landmarks, Poi[] Objects, Poi[] Discovered)
{
    public static DemoFixture Create()
    {
        const int w = 240, h = 160; var cells = new byte[w*h];
        void Room(int x0, int y0, int x1, int y1)
        { for(var y=y0;y<y1;y++) for(var x=x0;x<x1;x++) cells[y*w+x]=1; }
        Room(15,60,70,120); Room(60,80,115,95); Room(100,35,160,120); Room(145,45,210,62);
        Room(180,15,225,85); Room(120,110,135,145); Room(100,135,165,151); Room(192,119,229,147);
        return new(new(cells,w,h), new(40,90),
        [new("tile:exit", "demo/exit", "The Grelwood", "transition", new(209,40), "tile"),
         new("tile:boss", "demo/boss", "Арена босса", "boss", new(129,140), "tile"),
         new("tile:quest", "demo/quest", "Квестовая награда", "quest", new(125,45), "tile"),
         new("tile:isolated", "demo/isolated", "Недоступная комната", "quest", new(210,133), "tile")],
        [new("entity:wp", "demo/waypoint", "Waypoint", "waypoint", new(32,82), "entity"),
         new("entity:cp", "demo/checkpoint", "Checkpoint", "checkpoint", new(58,97), "entity"),
         new("entity:sh", "demo/shrine", "Shrine", "shrine", new(141,99), "entity")],
        [new("entity:exit", "demo/exit-object", "The Grelwood", "transition", new(211,42), "entity")]);
    }
}
