using POE2Radar.Core;
using POE2Radar.Core.Game;
using System.Text.Json;
using POE2Radar.Core.Pathfinding;

// Developer-only, read-only compatibility check. Never reads account/character names.
using var proc=ProcessHandle.AttachToPoE();
if(proc is null){Console.WriteLine("No game process");return 2;}
var reader=new MemoryReader(proc);
nint Ptr(nint a)=>reader.TryReadStruct<nint>(a,out var p)?p:0;
string Hex(nint a)=>$"0x{a:X}";
var slots=AobPatterns.GameStateRefs.SelectMany(p=>AobScanner.ScanForResolvedAddresses(proc,reader,p)).Distinct().ToArray();
Console.WriteLine($"AOB slots: {slots.Length}");
var resolved=false;
foreach(var slot in slots)
{
    var live=new Poe2Live(reader,slot);var root=Ptr(slot);var first=Ptr(root+Poe2.GameState.CurrentStatePtr);var last=Ptr(root+Poe2.GameState.CurrentStatePtr+8);
    var strict=live.TryResolve(out var igs,out var ai,out var lp);resolved|=strict;
    Console.WriteLine(JsonSerializer.Serialize(new{slot=Hex(slot),root=Hex(root),first=Hex(first),last=Hex(last),activeBytes=(long)last-(long)first,strict}));
    if(strict)
    {
        var t=live.Terrain(ai);var entities=live.Entities(ai);
        var beforeSleepingReads=reader.ReadCount;
        var sleepingTimer=System.Diagnostics.Stopwatch.StartNew();
        var sleeping=live.SleepingRadarEntities(ai);
        Console.WriteLine(JsonSerializer.Serialize(new{area=live.AreaName(ai),hash=live.AreaHash(ai),width=t?.Width,height=t?.Height,
            player=live.PlayerGrid(lp)?.ToString(),entities=entities.Count,landmarks=live.Landmarks(ai).Count,
            awakeRarities=entities.Where(e=>e.Category==Poe2Live.EntityCategory.Monster).GroupBy(e=>e.Rarity).ToDictionary(g=>g.Key.ToString(),g=>g.Count()),
            sleepingRarities=sleeping.Where(e=>e.Category==Poe2Live.EntityCategory.Monster).GroupBy(e=>e.Rarity).ToDictionary(g=>g.Key.ToString(),g=>g.Count()),
            sleepingReadCount=reader.ReadCount-beforeSleepingReads,sleepingMs=sleepingTimer.ElapsedMilliseconds}));
        if(t!=null && live.PlayerGrid(lp) is {} p)
        {
            var bossRouteAt=Array.IndexOf(args,"--boss-route");
            if(bossRouteAt>=0)
            {
                var code=live.AreaCode(ai);
                var markers=live.BossSpawnMarkers(ai);
                var marker=markers.FirstOrDefault();
                var arena=live.Landmarks(ai).FirstOrDefault(l=>Poe2Live.IsMapBossArenaTile(code,l.Path));
                var hasGoal=marker.Id!=0 || !string.IsNullOrEmpty(arena.Path);
                var goal=marker.Id!=0 ? marker.Grid : arena.Center;
                Console.WriteLine(JsonSerializer.Serialize(new{areaCode=code,markerCount=markers.Count,
                    arenaCount=live.Landmarks(ai).Count(l=>Poe2Live.IsMapBossArenaTile(code,l.Path)),goal=hasGoal?goal.ToString():null}));
                if(code.StartsWith("Map",StringComparison.OrdinalIgnoreCase) && hasGoal)
                {
                    var routePlanner=new PathPlanner();
                    foreach(var budget in new[]{250_000,1_000_000,4_000_000})
                    {
                        var watch=System.Diagnostics.Stopwatch.StartNew();
                        var route=routePlanner.Plan(t,((int)p.X,(int)p.Y),((int)goal.X,(int)goal.Y),budget);
                        Console.WriteLine(JsonSerializer.Serialize(new{budget,routePoints=route.Count,ms=watch.ElapsedMilliseconds}));
                        if(route.Count>0)break;
                    }
                    if(bossRouteAt+1<args.Length)
                        File.WriteAllText(args[bossRouteAt+1],JsonSerializer.Serialize(new{width=t.Width,height=t.Height,
                            cells=Convert.ToBase64String(t.Walkable),player=new{x=p.X,y=p.Y},boss=new{x=goal.X,y=goal.Y}}));
                }
                continue;
            }
            var fixtureAt=Array.IndexOf(args,"--fixture");
            var checkpoint=entities.FirstOrDefault(e=>e.Metadata.Contains("/Checkpoint"));
            if(fixtureAt>=0 && fixtureAt+1<args.Length && checkpoint.Metadata!=null && t.Width>=128 && t.Height>=128)
            {
                const int size=128;
                var ox=Math.Clamp((int)p.X-size/2,0,t.Width-size);
                var oy=Math.Clamp((int)p.Y-size/2,0,t.Height-size);
                var cropped=new byte[size*size];
                for(var y=0;y<size;y++)Array.Copy(t.Walkable,(oy+y)*t.Width+ox,cropped,y*size,size);
                var fixture=new { source=$"{live.AreaName(ai)}; captured {DateTime.UtcNow:yyyy-MM-dd}; local coordinates; no process addresses or character data",
                    width=size,height=size,cells=Convert.ToBase64String(cropped),
                    player=new{x=p.X-ox,y=p.Y-oy},checkpoint=new{x=checkpoint.Grid.X-ox,y=checkpoint.Grid.Y-oy} };
                File.WriteAllText(args[fixtureAt+1],JsonSerializer.Serialize(fixture));
            }
            var planner=new PathPlanner();
            foreach(var l in live.Landmarks(ai))
            {
                var clock=System.Diagnostics.Stopwatch.StartNew();
                var route=planner.Plan(t,((int)p.X,(int)p.Y),((int)l.Center.X,(int)l.Center.Y),1_000_000);
                Console.WriteLine(JsonSerializer.Serialize(new{target=l.CuratedName??l.Name,routePoints=route.Count,milliseconds=clock.ElapsedMilliseconds}));
            }
        }
    }
}
Console.WriteLine($"Reads: {reader.ReadCount}; failed: {reader.FailedReads}; resolved: {resolved}");
return resolved?0:1;
