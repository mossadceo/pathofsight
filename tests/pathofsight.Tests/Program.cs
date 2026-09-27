using System.Net;
using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using pathofsight;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;

var checks=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
if(args.Contains("--live"))
{
    using var liveService=new MapService(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"pathofsight-live-check"));
    var deadline=Environment.TickCount64+15000;
    while(liveService.Snapshot.Status is not ("ready" or "incompatible" or "error") && Environment.TickCount64<deadline)
        await Task.Delay(250);
    var liveSnapshot=liveService.Snapshot;
    Console.WriteLine(JsonSerializer.Serialize(new{liveSnapshot.Status,liveSnapshot.Message,liveSnapshot.Area,
        liveSnapshot.Width,liveSnapshot.Height,expedition=liveSnapshot.Targets.Count(p=>p.FilterCategory=="expedition"),
        rare=liveSnapshot.Targets.Count(p=>p.Kind=="rare"),
        breach=liveSnapshot.Targets.Where(p=>p.FilterCategory=="breach").Select(p=>new{p.Id,p.Key,p.Position,p.Name,p.Completed}),
        ritual=liveSnapshot.Targets.Where(p=>p.FilterCategory=="ritual").Select(p=>new{p.Id,p.Completed}),
        shrines=liveSnapshot.Targets.Where(p=>p.FilterCategory=="shrine").Select(p=>new{p.Id,p.Completed})}));
    Check(liveSnapshot.Status=="ready"&&liveSnapshot.Terrain is not null&&liveSnapshot.Picture is not null,
        "live map publishes validated terrain");
    Check(liveSnapshot.Targets.Where(p=>p.FilterCategory=="expedition").All(p=>p.Name=="Expedition")
        && liveSnapshot.Targets.Where(p=>p.Kind=="rare").All(p=>p.Name=="Rare")
        && liveSnapshot.Targets.Where(p=>p.FilterCategory=="map-boss").All(p=>p.Name=="BO$$"),
        "live POIs use short public labels");
    return;
}
string[] EmbeddedArt(string name)
{
    using var stream=typeof(pathofsight.Program).Assembly.GetManifestResourceStream("pathofsight.Art."+name+".txt")!;
    using var reader=new StreamReader(stream,Encoding.UTF8);
    return reader.ReadToEnd().Split('\n');
}
var eyeArt=EmbeddedArt("eye");
using(var inputStop=new CancellationTokenSource())
{
    var pendingKeys=6;
    var inputTime=System.Diagnostics.Stopwatch.StartNew();
    pathofsight.Program.RunTuiLoop(()=>
    {
        if(pendingKeys==0)return false;
        pendingKeys--;
        return true;
    },()=>{if(pendingKeys==0)inputStop.Cancel();},inputStop.Token);
    Check(inputTime.ElapsedMilliseconds<500,"queued navigation keys render without a delay per key ("+inputTime.ElapsedMilliseconds+" ms)");
}
using (var view = new TuiView())
{
    var snapshot = MapSnapshot.Empty("waiting", "Waiting");
    view.RenderFrame(TuiView.Layout(snapshot, "", false, 80, 30), 80, 30);
    var resized = view.RenderFrame(TuiView.Layout(snapshot, "", false, 80, 25), 80, 25);
    Check(!resized.Contains("\x1b[2K"), "redraw never blanks a row before painting its text and borders");
    Check(resized.Contains("\x1b[1;1H"), "height-only resize redraws the top border after terminal reflow");
    var repeated = view.RenderFrame(TuiView.Layout(snapshot, "", false, 80, 25), 80, 25);
    Check(repeated.Length==0, "unchanged frame emits no terminal writes");
    var repair=view.RenderFrame(TuiView.Layout(snapshot,"",false,80,25),80,25,refresh:true);
    Check(repair.Contains("\x1b[1;1H")&&!repair.Contains("\x1b[2K")&&!repair.Contains("\x1b[2J"),"periodic reflow repair overwrites without clearing screen or lines");
    view.RenderFrame([("Long old text","180;180;180",false)],80,25);
    var shorter=view.RenderFrame([("X","180;180;180",false)],80,25);
    Check(shorter.Contains("X"+new string(' ',79)),"shorter rows overwrite old text with trailing spaces");
}
Check(eyeArt.Count(x=>x.Length>0)==8&&eyeArt.Max(x=>x.TrimEnd('\r').Length)==30&&!eyeArt.Any(x=>x.Contains("тЦ")||x.Contains('�')),"new eye logo is embedded as Unicode art");
foreach (var (w,h) in new[] { (120,50), (80,24), (65,18), (64,18), (40,12), (40,8), (40,7), (40,6), (40,5), (40,4), (40,1) })
{
    var screen=TuiView.Layout(MapSnapshot.Empty("waiting","Waiting for connection command"),"Notice\n\u001b[2J",false,w,h);
    Check(screen.Count==h&&screen.All(line=>line.Text.Length<w&&!line.Text.Any(char.IsControl)), $"TUI fits {w}x{h} and sanitizes control characters");
    Check(screen.All(line=>!line.Text.Any(c=>c is >= 'А' and <= 'я' or 'Ё' or 'ё')),
        $"TUI labels are English at {w}x{h}");
    Check(screen.All(line=>!line.Text.Contains("Нажмите")&&!line.Text.Contains("- - -")&&!line.Text.Contains("127.0.0.1")),
        $"TUI has no prompt box or lower URL at {w}x{h}");
    if (h>=4) Check(screen.Any(line=>line.Text.Contains("Connect to game"))&&
        screen.Any(line=>line.Text.Contains("Open web map")), $"TUI keeps commands at {w}x{h}");
    if (h>=24) Check(screen[0].Text.Contains("Path Of Sight  v0.2.0")&&screen.Any(line=>line.Text.Contains("▄████████  ▐███████")),
        $"logo appears on startup at {w}x{h}");
}
foreach (var status in new[] { "waiting", "loading", "ready", "demo", "error", "access", "incompatible" })
{
    var screen=TuiView.Layout(MapSnapshot.Empty(status,"Status"),"",true,80,24);
    Check(screen.Take(23).All(line=>line.Text.Length==79), $"frame stays aligned for {status}");
    Check(screen.Count(line=>line.Text.Contains("1  Connect"))==1&&screen.Count(line=>line.Text.Contains("2  Open"))==1,
        $"connect and browser commands remain unique for {status}");
    Check(screen.Any(line=>line.Text.Contains("3  Settings"))&&screen.Any(line=>line.Text.Contains("4  Profiles")),
        $"settings and profiles commands appear for {status}");
    Check(screen.Single(line=>line.Text.Contains("1  Connect")).Text.Contains(status=="ready"?"✓":status=="demo"?"DEMO":"WAIT"),
        $"connection state reflects snapshot for {status}");
}
var stale=TuiView.Layout(new MapSnapshot("ready","Old data",Environment.TickCount64-3000),"",true,80,24);
Check(!stale.Any(line=>line.Text.Contains("Game connected")),"stale game data does not show connected");
Check(pathofsight.Program.CommandKey(new ConsoleKeyInfo('й',ConsoleKey.Q,false,false,false))=='q',
    "physical Q quits in the Russian keyboard layout");
foreach (var width in new[] { 40, 65, 80, 120 })
{
    var screen=TuiView.Layout(MapSnapshot.Empty("ready","Connected"),"",true,width,30);
    foreach (var label in new[] { "1  Connect to game", "2  Open web map" })
    {
        var line=screen.Single(row=>row.Text.Contains(label));
        Check(TuiView.ColorAt(line,line.Text.IndexOf(label,StringComparison.Ordinal))=="180;180;180",
            $"command is gray at width {width}");
        if(label.StartsWith('1')) Check(TuiView.ColorAt(line,line.Text.IndexOf('✓'))=="78;186;101",
            $"connected checkmark is green at width {width}");
    }
    if(width<65) continue;
    var eye=screen.Single(row=>row.Text.Contains("▄████████  ▐███████"));
    var start=eye.Text.IndexOf('▄');
    Check(TuiView.ColorAt(eye,start)=="255;255;255"&&TuiView.ColorAt(eye,start+11)=="180;65;65",
        $"eye is pure white with a red pupil at width {width}");
    Check(TuiView.ColorAt(eye,0)=="112;119;130",$"border color is independent of eye at width {width}");
}
var fixture=DemoFixture.Create();var tracker=new PoiTracker();tracker.Reset("a",fixture.Landmarks);
Check(Poe2Live.IsPlausibleTerrainGrid(8_498_868,1_484)
    && !Poe2Live.IsPlausibleTerrainGrid(18_000_000,2_000)
    && !Poe2Live.IsPlausibleTerrainGrid(8_498_869,1_484),
    "Azmerian Ranges terrain fits while oversized and incomplete grids are rejected");
Check(tracker.Update(fixture.Objects).Length==9,"tile and entity layers combine, including Magic and Rare");
var found=tracker.Update(fixture.Objects.Concat(fixture.Discovered));
Check(found.Length==9&&found.Single(p=>p.Id=="tile:exit").Source=="entity","discovered transition replaces matching tile");
Check(tracker.Update([]).Single(p=>p.Id=="tile:exit").Remembered,"discovered stationary object survives disappearance");
tracker.Reset("b",[]);Check(tracker.Update([]).Length==0,"new instance clears remembered objects");
tracker.Reset("c",[new("t","k","Exit","transition",new(10,10),"tile")]);
Check(tracker.Update([new("e","e","Checkpoint","checkpoint",new(11,10),"entity")]).Length==2,"adjacent checkpoint and exit never merge");
Check(PoiTracker.Kind("Metadata/Monsters/ShrineFireDaemon","",true)=="poi","shrine cosmetic is not shrine");
Check(PoiTracker.Kind("Metadata/Shrines/Shrine_Trigger","",true)=="shrine","actual shrine classified");
Check(Poe2Live.IsEarlyPoiMetadata("Metadata/Terrain/Leagues/Ritual/RitualRuneObject")
    && Poe2Live.IsEarlyPoiMetadata("Metadata/Shrines/Shrine")
    && Poe2Live.IsEarlyPoiMetadata("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter")
    && !Poe2Live.IsEarlyPoiMetadata("Metadata/MiscellaneousObjects/Expedition2/ExpeditionMarker")
    && !Poe2Live.IsEarlyPoiMetadata("Metadata/Terrain/Abyss/Floor")
    && !Poe2Live.IsEarlyPoiMetadata("Metadata/Quest/RitualObject"),
    "early POI whitelist accepts verified objects without decorative or quest lookalikes");
foreach (var (metadata, category) in new[]
{
    ("Metadata/Terrain/Leagues/Ritual/RitualRuneObject", "ritual"),
    ("Metadata/Shrines/Shrine", "shrine"),
    ("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", "expedition")
})
{
    var earlyDot = new Poe2Live.EntityDot(93, 0, new(100, 100), default,
        Poe2Live.EntityCategory.Object, metadata, 0, 0, true, 0, Poe2Live.Rarity.NonMonster, false);
    var earlyPoi = PoiTracker.FromEntity(earlyDot);
    Check(earlyPoi is not null && earlyPoi.FilterCategory == category
        && new DisplaySettings().Shows(earlyPoi) && !ProfileSettings.Defaults("Act Rush").Shows(earlyPoi),
        "early " + category + " POI enters radar and obeys profile filter");
}
Poe2Live.EntityDot AbyssDot(string metadata) => new(90,0,new(10,10),default,Poe2Live.EntityCategory.Object,
    metadata,0,0,true,0,default,false);
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Abyss/AbyssCrack_01"))==null,"abyss cracks are not POIs");
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Abyss/AbyssFissure_01"))==null,"abyss fissures are not POIs");
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Abyss/AbyssJumpInteractable"))!=null,"abyss entrance remains a POI");
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Other/CrystalFissure"))!=null,"unrelated fissures remain POIs");
var boss=new Poe2Live.EntityDot(91,0,new(32,40),default,Poe2Live.EntityCategory.Monster,
    "Metadata/Monsters/SomeBoss/SomeBossMap",100,100,false,0,Poe2Live.Rarity.Unique,false);
var bossPoi=PoiTracker.FromEntity(boss);
Check(bossPoi is { Kind: "boss", Source: "entity" },"living unique boss becomes a map target");
tracker.Reset("boss",[]);
Check(tracker.Update([bossPoi!]).Length==1 && tracker.Update([]).Length==0,"boss marker disappears when boss is no longer observed");
Check(PoiTracker.FromEntity(boss with { HpCur=0 })==null,"dead boss is hidden");
Check(PoiTracker.FromEntity(boss with { HpCur=0, HpMax=0 })!=null,"boss remains visible when health read is unavailable");
Check(PoiTracker.FromEntity(boss with { Rarity=Poe2Live.Rarity.Rare }) is {Kind:"rare"},"rare monster is classified separately from bosses");
Check(PoiTracker.FromEntity(boss with { Rarity=Poe2Live.Rarity.Normal })==null
    &&!Poe2Live.IsVisibleMonster(Poe2Live.Rarity.Normal,100,100,0),"Normal monsters never enter the radar");
foreach(var (rarity,kind,color) in new[]{(Poe2Live.Rarity.Magic,"magic","#75A7FF"),(Poe2Live.Rarity.Rare,"rare","#F4D35E")})
{
    var monster=boss with {Rarity=rarity};
    var poi=PoiTracker.FromEntity(monster)!;
    Check(poi.Kind==kind&&poi.FilterCategory==kind&&poi.Source=="entity",kind+" keeps rarity even when metadata contains Boss");
    Check(MapSurface.ColorFor(kind)==color,kind+" has a distinct rarity color");
    Check(!Poe2Live.IsVisibleBoss(rarity,100,100,0)&&Poe2Live.IsVisibleMonster(rarity,100,100,0),kind+" uses shared monster visibility without becoming a boss");
    Check(PoiTracker.FromEntity(monster with {HpCur=0})==null,kind+" corpse disappears");
    Check(PoiTracker.FromEntity(monster with {Reaction=1})==null&&PoiTracker.FromEntity(monster with {Reaction=129})==null,kind+" friendly entities are excluded");
    Check(PoiTracker.FromEntity(monster with {HpCur=0,HpMax=0})!=null,kind+" follows existing unknown-health visibility policy");
    var movingTracker=new PoiTracker();movingTracker.Reset("monsters",[]);
    movingTracker.Update([poi]);
    var moved=movingTracker.Update([poi with {Position=new(45,60)}]);
    Check(moved.Length==1&&moved[0].Position==new Point(45,60)&&!moved[0].Remembered,kind+" updates position without leaving an old marker");
    Check(movingTracker.Update([]).Length==0,kind+" is not remembered outside the live entity set");
    movingTracker.Update([poi]);movingTracker.Reset("next",[]);
    Check(movingTracker.Update([]).Length==0,kind+" is cleared on instance reset");
    Check(new DisplaySettings().Shows(poi)==(kind=="rare")&&!new DisplaySettings{Categories=[]}.Shows(poi),kind+" respects default and category toggle");
    Check(!ProfileSettings.Defaults("Act Rush").Shows(poi)&&!ProfileSettings.Defaults("Atlas Boss Rush").Shows(poi),kind+" does not leak into rush presets");
    Check(MapService.AutomaticBossTarget("MapExample",[poi])==null,kind+" cannot trigger an automatic boss route");
}
Check(PoiTracker.FromEntity(boss with {Rarity=Poe2Live.Rarity.NonMonster})==null
    &&!Poe2Live.IsVisibleMonster((Poe2Live.Rarity)99,100,100,0),"unsupported rarity values are not monster targets");
Check(PoiTracker.FromEntity(boss with { Reaction=1 })==null,"friendly unique is not marked as boss");
var deadIds=new HashSet<uint>();
var sleepingRare=boss with { Rarity=Poe2Live.Rarity.Rare, HpCur=0, HpMax=0 };
var deadRare=sleepingRare with { HpMax=100 };
Check(MapService.RadarEntities([deadRare],[sleepingRare],deadIds).Select(PoiTracker.FromEntity).All(p=>p is null)
    && deadIds.Contains(sleepingRare.Id),"dead awake Rare suppresses a stale sleeping marker");
Check(!MapService.RadarEntities([], [sleepingRare],deadIds).Any()
    && MapService.RadarEntities([], [sleepingRare],new HashSet<uint>()).Any(),
    "dead Rare remains hidden until the instance state resets");
var bossRouteTargets=new Poi[]
{
    new("tile:arena","MapExcavation:BossArena","Арена босса","boss",new(30,40),"tile"),
    new("marker:boss:7","MapExcavation:boss-spawn","Место появления босса","boss",new(32,40),"tile"),
    bossPoi!,
    new("tile:exit","MapExcavation:Exit","Выход","transition",new(10,10),"tile")
};
Check(MapService.AutomaticBossTarget("MapExcavation",bossRouteTargets)?.Id=="marker:boss:7",
    "waystone route stays on the map-completion boss marker when a unique monster appears");
Check(MapService.AutomaticBossTarget("MapExcavation",[bossPoi! with { Name="Rogue Exile" },
    bossPoi! with { Id="entity:92",Name="Ritual boss" }])==null,
    "rogue exiles and ritual bosses alone never start the automatic route");
Check(MapService.AutomaticBossTarget("MapExcavation",bossRouteTargets[..2])?.Id=="marker:boss:7",
    "waystone route uses the early spawn marker before the boss appears");
Check(MapService.AutomaticBossTarget("MapExcavation",[bossRouteTargets[0],bossPoi!])?.Id=="tile:arena",
    "waystone route falls back to an arena landmark");
Check(MapService.AutomaticBossTarget("MapExcavation",[new("tile:stairs","MapExcavation:BossStairs",
    "Boss stairs","boss",new(12,12),"tile"),bossPoi!])==null,
    "generic boss-labelled stairs and uniques do not become automatic targets");
Check(MapService.AutomaticBossTarget("G1_1",bossRouteTargets)==null
    && MapService.AutomaticBossTarget("",bossRouteTargets)==null,
    "campaign and demo bosses never get an automatic route");
Check(Poe2Live.IsMapBossArenaTile("MapExcavation","Metadata/Terrain/Islands/Tiles/TwilightIsland/PrecursorRuins/Arena/BossArena_Forge_PCR_01.tdt"),
    "waystone boss arena tile is available before boss spawns");
Check(!Poe2Live.IsMapBossArenaTile("G1_1","Metadata/Terrain/Maps/Some/BossArena.tdt")
    && !Poe2Live.IsMapBossArenaTile("MapExcavation","Metadata/Terrain/Maps/Some/BossStairs.tdt"),
    "generic arena detection excludes campaign and boss stairs");
Check(Poe2Live.IsBossSpawnMarker("Metadata/MiscellaneousObjects/BossLeagueContentMarkerMain")
    && Poe2Live.IsBossSpawnMarker("Metadata/Monsters/Hags/Objects/BossRoomMinimapIcon")
    && !Poe2Live.IsBossSpawnMarker("Metadata/MiscellaneousObjects/BossArenaBlocker"),
    "early boss marker names exclude arena blockers");
var transition=PoiTracker.FromEntity(new(89,0,new(10,10),default,Poe2Live.EntityCategory.Transition,
    "Metadata/MiscellaneousObjects/AreaTransition_Animate",0,0,true,0,default,false));
Check(transition?.Name=="Area Transition","unresolved transition metadata uses a generic name for landmark refinement");
tracker.Reset("ambiguous",[new("t1","k1","Exit","transition",new(10,10),"tile"),new("t2","k2","Exit","transition",new(15,10),"tile")]);
Check(tracker.Update([new("e1","e1","Exit","transition",new(11,10),"entity")]).Length==3,"ambiguous landmarks do not consume one entity twice");
tracker.Reset("generic",[new("t1","k1","The Grelwood","transition",new(10,10),"tile")]);
Check(tracker.Update([new("e1","e1","Area Transition","transition",new(11,10),"entity")]).Single().Source=="entity","unique generic transition refines named landmark");
Check(!new pathofsight.Point(float.NaN,1).In(fixture.Terrain),"NaN coordinate rejected");
var center=Projection.At(new(23,42),new(23,42),new(800,450),2);Check(center==new pathofsight.Point(800,450),"player maps to center");
var projected=Projection.At(new(33,42),new(23,42),new(800,450),2);Check(projected.X>800&&projected.Y<450,"projection orientation matches upstream");
var planner=new PathPlanner();var path=planner.Plan(fixture.Terrain,(40,90),(209,40));
Check(path.Count>1,"route traverses connected rooms");
Check(planner.Plan(fixture.Terrain,(40,90),(210,133)).Count==0,"disconnected room returns no path");
const int longWidth=800,longHeight=800;
var longCells=new byte[longWidth*longHeight];Array.Fill(longCells,(byte)1);
for(var y=0;y<longHeight-10;y++)longCells[y*longWidth+400]=0;
var longTerrain=new Poe2Live.TerrainData(longCells,longWidth,longHeight);
Check(new PathPlanner().Plan(longTerrain,(600,100),(700,100),250000).Count>1,
    "near boss route succeeds on the same large map");
var syntheticBoss=new Poi("marker:boss:1","MapExample:boss-spawn","Босс","boss",new(700,100),"tile");
Check(MapService.RouteSearchBudget("G1_1",syntheticBoss,longTerrain)==250000,
    "campaign boss keeps the ordinary search budget");
Check(new PathPlanner().Plan(longTerrain,(100,100),(700,100),
    MapService.RouteSearchBudget("MapExample",syntheticBoss,longTerrain)).Count>1,
    "route from map entrance survives a long detour before the boss");
var corner=new Poe2Live.TerrainData([1,0,0,1],2,2);
Check(new PathPlanner().Plan(corner,(0,0),(1,1)).Count==0,"route cannot cut a diagonal wall corner");
Check(!PathSmoother.HasLineOfSight(new TerrainCellReader(corner),0,0,1,1),"smoother cannot cut a diagonal wall corner");
var cells=new TerrainCellReader(fixture.Terrain);Check(path.Zip(path.Skip(1)).All(p=>PathSmoother.HasLineOfSight(cells,p.First.x,p.First.y,p.Second.x,p.Second.y)),"route stays in walkable cells");
using(var saved=JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"fixtures/scorched-entry.json"))))
{
    var r=saved.RootElement;
    var terrain=new Poe2Live.TerrainData(Convert.FromBase64String(r.GetProperty("cells").GetString()!),r.GetProperty("width").GetInt32(),r.GetProperty("height").GetInt32());
    pathofsight.Point ReadPoint(string name)=>new(r.GetProperty(name).GetProperty("x").GetSingle(),r.GetProperty(name).GetProperty("y").GetSingle());
    var player=ReadPoint("player");var checkpoint=ReadPoint("checkpoint");
    Check(player.In(terrain)&&checkpoint.In(terrain),"saved live coordinates fit anonymized terrain crop");
    var liveRoute=new PathPlanner().Plan(terrain,((int)player.X,(int)player.Y),((int)checkpoint.X,(int)checkpoint.Y));
    Check(liveRoute.Count>1&&liveRoute.Zip(liveRoute.Skip(1)).All(p=>PathSmoother.HasLineOfSight(new TerrainCellReader(terrain),p.First.x,p.First.y,p.Second.x,p.Second.y)),"route to checkpoint on saved live terrain is walkable");
}
var dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"pathofsight-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
var profilePath=System.IO.Path.Combine(dir,"profiles.json");
var profiles=new SettingsStore(profilePath);
Check(profiles.Read().Profile=="Default"&&profiles.Value.Routing=="manual"&&profiles.Value.Categories.Length==PoiCatalog.Enabled.Length-1
    &&!profiles.Value.Categories.Contains("magic")&&profiles.Value.Categories.Contains("rare"),"fresh Default hides Magic and keeps Rare with manual routing");
Check(!PoiCatalog.All.Any(f=>f.Id=="normal")&&new[]{"magic","rare","unique"}.All(id=>PoiCatalog.All.Single(f=>f.Id==id).Available),
    "Normal has no filter while Magic and Rare remain configurable");
Check(ProfileSettings.Names.All(name=>!ProfileSettings.Defaults(name).Categories.Contains("magic")),"Magic is off in all profile defaults");
foreach(var name in ProfileSettings.Names)
{
    profiles.SelectProfile(name);
    Check(profiles.Value.Valid(),name+" defaults are valid");
}
profiles.SelectProfile("Act Rush");
Check(profiles.Value.Routing=="off"&&!profiles.Value.Categories.Contains("ritual")&&profiles.Value.Categories.Contains("npc")&&profiles.Value.Categories.Contains("checkpoint"),"Act Rush keeps campaign navigation and hides mechanics");
profiles.SelectProfile("Atlas Boss Rush");
Check(profiles.Value.Categories.SequenceEqual(new[]{"map-boss"})&&profiles.Value.Routing=="atlas","Atlas profile isolates completion-boss POIs");
profiles.Update(s=>s with { Labels=false });
profiles.SelectProfile("Custom 2");profiles.Update(s=>s with { Opacity=.5 });
profiles.SelectProfile("Atlas Boss Rush");
Check(!profiles.Value.Labels&&profiles.Value.Opacity==.85,"profiles have independent values");
profiles.SelectProfile("Atlas Boss Rush",reset:true);
Check(profiles.Value.Labels&&profiles.Value.Routing=="atlas","reset restores built-in defaults");
profiles.SelectProfile("Custom 2");
Check(profiles.Value.Opacity==.5,"reset leaves other profiles untouched");
profiles.SelectProfile("Custom 2",reset:true);
Check(profiles.Value.Opacity==.85&&profiles.Value.Routing=="manual","custom reset restores Default values");
profiles.SetInstance("first");
bool WebFilter(DisplaySettings value,bool reset=false)
{var v=profiles.Read();return profiles.WebSave(v.Profile,v.Instance,v.Revision,value,true,reset);}
Check(WebFilter(profiles.Value with { Categories=profiles.Value.Categories.Except(new[]{"shrine","waypoint"}).ToArray(),Hidden=["local"] }),"temporary filter accepted");
Check(profiles.Read().Temporary&&!profiles.Value.Categories.Contains("shrine")&&profiles.Read().ProfileSettings.Categories.Contains("shrine"),"web overrides leave saved profile intact");
var staleSettings=profiles.Read();
profiles.Update(s=>s with { Categories=s.Categories.Except(new[]{"shrine"}).ToArray() },"shrine");
profiles.Update(s=>s with { Categories=s.Categories.Append("shrine").ToArray() },"shrine");
Check(profiles.Value.Categories.Contains("shrine")&&!profiles.Value.Categories.Contains("waypoint"),"TUI clears only the edited filter override");
Check(!profiles.WebSave(staleSettings.Profile,staleSettings.Instance,staleSettings.Revision,staleSettings.Settings,false),"stale settings revision rejected");
var currentSettings=profiles.Read();
Check(profiles.WebSave(currentSettings.Profile,currentSettings.Instance,currentSettings.Revision,currentSettings.Settings with { Labels=false },false)
    &&profiles.Read().ProfileSettings.Categories.Contains("waypoint"),"web appearance save does not persist temporary categories");
Check(WebFilter(profiles.Value,reset:true)&&!profiles.Read().Temporary&&profiles.Value.Hidden.Length==0,"reset map filters restores profile");
WebFilter(profiles.Value with { Categories=[] });profiles.SetInstance("second");
Check(!profiles.Read().Temporary&&profiles.Value.Categories.Length>0,"new instance clears overrides");
WebFilter(profiles.Value with { Categories=[] });profiles.SetInstance("first");
Check(profiles.Value.Categories.Length>0,"return to previous location starts with profile");
WebFilter(profiles.Value with { Categories=[] });profiles.SelectProfile("Default");
Check(!profiles.Read().Temporary&&profiles.Value.Categories.Length>0,"profile selection clears overrides");
WebFilter(profiles.Value with { Categories=[] });profiles.SelectProfile("Default",reset:true);
Check(!profiles.Read().Temporary,"reset current clears overrides");
WebFilter(profiles.Value with { Categories=[] });
var reloadedProfiles=new SettingsStore(profilePath);
Check(reloadedProfiles.Read().Profile=="Default"&&!reloadedProfiles.Read().Temporary&&reloadedProfiles.Value.Categories.Length>0,"restart restores active profile without map overrides");
var oldRarityPath=System.IO.Path.Combine(dir,"old-rarities.json");
var oldRarityProfiles=new ProfileSettings {Version=2};
foreach(var name in ProfileSettings.Names)oldRarityProfiles.Profiles[name]=oldRarityProfiles.Profiles[name] with
    {Categories=oldRarityProfiles.Profiles[name].Categories.Append("normal").Append("magic").Distinct().ToArray()};
File.WriteAllText(oldRarityPath,JsonSerializer.Serialize(oldRarityProfiles,SettingsStore.Json));
var oldRarityStore=new SettingsStore(oldRarityPath);
Check(oldRarityStore.LoadWarning==null&&ProfileSettings.Names.All(name=>
    {oldRarityStore.SelectProfile(name);return !oldRarityStore.Value.Categories.Contains("normal")&&!oldRarityStore.Value.Categories.Contains("magic");})
    &&File.Exists(oldRarityPath+".before-monster-filters.bak"),"saved profiles drop Normal/Magic with a backup");
oldRarityStore.Update(s=>s with {Categories=s.Categories.Append("magic").ToArray()});
Check(new SettingsStore(oldRarityPath).Value.Categories.Contains("magic"),"Magic can be re-enabled after migration");
var beforeFailure=profiles.Read();Directory.CreateDirectory(profilePath+".tmp");
try {profiles.Update(s=>s with { Labels=false });Check(false,"save should fail");}
catch(Exception e) when(e is IOException or UnauthorizedAccessException) {Check(profiles.Read()==beforeFailure,"failed persistence leaves state untouched");}
Directory.Delete(profilePath+".tmp");
var legacyPath=System.IO.Path.Combine(dir,"legacy.json");
File.WriteAllText(legacyPath,"{\"labels\":false,\"categories\":[\"boss\",\"quest\"],\"names\":{\"old\":\"Name\"},\"hidden\":[\"old\"]}");
var migratedProfiles=new SettingsStore(legacyPath);
Check(migratedProfiles.Read().Profile=="Custom 1"&&migratedProfiles.Value.Routing=="atlas"&&!migratedProfiles.Value.Labels
    &&migratedProfiles.Value.Categories.Contains("map-boss")&&migratedProfiles.Value.Categories.Contains("reward")&&migratedProfiles.Value.Hidden.Contains("old"),"legacy migration preserves appearance, exclusions and automatic routing in Custom 1");
Check(File.Exists(legacyPath+".v1.bak")&&new SettingsStore(legacyPath).LoadWarning==null,"migration backs up old file and reloads versioned profiles");
File.WriteAllText(System.IO.Path.Combine(dir,"invalid-profiles.json"),"{\"version\":2,\"profiles\":null}");
Check(new SettingsStore(System.IO.Path.Combine(dir,"invalid-profiles.json")).LoadWarning!=null,"invalid profile structure falls back with warning");
Poi FilterPoi(string path,string label="Object",string kind="poi",string source="tile")=>new("test",path,label,kind,new(1,1),source);
var breachHand=PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Brequel/BrequelInitiator"))!;
Check(breachHand.FilterCategory=="breach"&&PoiCatalog.ShortName(breachHand)=="Breach",
    "Brequel initiator is a Breach mechanic with a short label");
Check(!Poe2Live.IsCompletedIcon(breachHand.Key,0,0)
    && Poe2Live.IsCompletedIcon(breachHand.Key,0,1)
    && !Poe2Live.IsCompletedIcon("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",0,1)
    && Poe2Live.IsCompletedIcon("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",1,0),
    "Brequel completion flag is specific to the hand and standard icon completion still works");
var ritualSite=new Poi("ritual:1","Metadata/Terrain/Leagues/Ritual/RitualRuneObject","Ritual","poi",new(10,10),"entity");
var completedRitual=ritualSite with {Id="ritual:done",Key="Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable",Completed=true};
var otherRitual=ritualSite with {Id="ritual:other",Position=new(100,100)};
var mechanicTracker=new PoiTracker();mechanicTracker.Reset("mechanics",[]);
var ritualPoints=mechanicTracker.Update([ritualSite,completedRitual,otherRitual]);
Check(ritualPoints.Length==1 && ritualPoints[0].Id==otherRitual.Id,
    "completing one Ritual hides colocated Ritual POIs but not other sites");
Check(mechanicTracker.Update([ritualSite,otherRitual]).Single().Id==otherRitual.Id,
    "mechanic completion persists within the instance after the interactable vanishes");
Check(mechanicTracker.Update([ritualSite,completedRitual with {Completed=false},otherRitual]).Single().Id==otherRitual.Id,
    "a transient incomplete read does not restore a finished mechanic");
mechanicTracker.Reset("next",[]);
Check(mechanicTracker.Update([ritualSite]).Single().Completed==false,"mechanic completion resets in a new instance");
mechanicTracker.Reset("breach",[]);
var remainingBreach=breachHand with {Id="breach:other",Position=new(100,100)};
var remainingRitual=ritualSite with {Position=breachHand.Position};
Check(mechanicTracker.Update([breachHand with {Completed=true},remainingBreach,remainingRitual])
    .Select(p=>p.Id).Order().SequenceEqual(new[]{remainingBreach.Id,remainingRitual.Id}.Order()),
    "completed Breach hides only its hand, leaving another Breach and a different mechanic");
var shrineDot=AbyssDot("Metadata/Shrines/Shrine");
var shrinePoi=PoiTracker.FromEntity(shrineDot)!;
var otherShrine=PoiTracker.FromEntity(shrineDot with {Id=91,Grid=new(100,100)})!;
mechanicTracker.Reset("shrine",[]);
Check(mechanicTracker.Update([shrinePoi,otherShrine]).Length==2
    && mechanicTracker.Update([PoiTracker.FromEntity(shrineDot with {IconComplete=true})!,otherShrine])
        .Single().Id==otherShrine.Id
    && mechanicTracker.Update([shrinePoi,otherShrine]).Single().Id==otherShrine.Id,
    "used Shrine disappears without hiding another Shrine and stays hidden in its instance");
mechanicTracker.Reset("chest",[]);
Check(mechanicTracker.Update([ritualSite with {Id="chest:1",Key="Metadata/Chests/Chest",Kind="chest",Completed=true}]).Length==1,
    "completed non-mechanics keep their existing display behavior");
foreach(var (mechanicPath,label) in new[]{
    ("Metadata/MiscellaneousObjects/Abyss/AbyssJumpInteractable","Abyss"),
    ("Metadata/Terrain/Leagues/Ritual/RitualRuneObject","Ritual"),
    ("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter","Expedition"),
    ("Metadata/MiscellaneousObjects/Breach/BreachPortal","Breach")})
    Check(PoiCatalog.ShortName(FilterPoi(mechanicPath,"Long internal name"))==label,label+" uses a short default label");
var abyssTransition=FilterPoi("Metadata/MiscellaneousObjects/Abyss/AbyssSubAreaTransition","AbyssSubAreaTransition","transition");
Check(PoiCatalog.ShortName(abyssTransition)=="Abyss"&&abyssTransition.FilterCategory=="transition",
    "Abyss subarea keeps transition behavior with a short label");
var shortBoss=FilterPoi("MapExcavation:boss-spawn","Место появления босса","boss") with { Id="marker:boss:7" };
Check(PoiCatalog.ShortName(shortBoss)=="BO$$"&&PoiCatalog.ShortName(bossPoi!)=="Boss"
    && PoiCatalog.ShortName(bossPoi! with {Kind="rare"})=="Rare",
    "map boss and enemy names have generic defaults");
Check(new DisplaySettings { Names = new() { [shortBoss.Key]="My Boss" } }.Label(shortBoss with { Name=PoiCatalog.ShortName(shortBoss) })=="My Boss",
    "saved custom label overrides the short default");
Check(PoiCatalog.KeepMapPoi("MapAzmerianRanges",FilterPoi("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"))
    && !PoiCatalog.KeepMapPoi("MapAzmerianRanges",FilterPoi("Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExplodingFill_StrongBox"))
    && PoiCatalog.KeepMapPoi("G1_1",FilterPoi("Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExplodingFill_StrongBox")),
    "map Expedition keeps rune stations, drops filler, and preserves campaign POIs");
Check(FilterPoi("Metadata/Terrain/Woods/HuntingGrounds/RitualClearing01","Ritual Site (Level 4 Skill Gem)","quest").FilterCategory=="reward","campaign ritual reward is not hidden as a mechanic");
Check(FilterPoi("Metadata/Terrain/Desert/Badlands/Features/AbyssHole","Lightless Passage","transition").FilterCategory=="transition","campaign Abyss passage keeps transition category");
Check(FilterPoi("Metadata/Terrain/Dungeon/DoryanisSanctum/Entrance","Jiquani's Sanctum","transition").FilterCategory=="transition","campaign sanctum is not a trial mechanic");
Check(FilterPoi("Metadata/MiscellaneousObjects/StashSkins/RitualStashAdditive").FilterCategory=="poi","cosmetic mechanic name is not enough to classify");
foreach(var (metadata,category) in new[]{("Metadata/MiscellaneousObjects/RitualRune","ritual"),("Metadata/MiscellaneousObjects/Abyss/AbyssWellInteractable","abyss"),
    ("Metadata/Terrain/Leagues/Expedition/Marker","expedition"),("Metadata/MiscellaneousObjects/Breach/BreachPortal","breach"),
    ("Metadata/MiscellaneousObjects/Delirium/Portal","delirium"),("Metadata/Chests/Essence/Monolith","essence"),
    ("Metadata/Chests/StrongBoxes/Box","strongbox"),("Metadata/Terrain/Woods/Woods/AzmeriLeague/Feature","azmeri"),
    ("Metadata/Terrain/Gallows/Leagues/Sanctum/Objects/Lever","sekhemas"),("Metadata/NPC/League/Ultimatum/UltimatumNpc","chaos"),
    ("Metadata/MiscellaneousObjects/LeagueIncursionNew/Bench","incursion")})
    Check(FilterPoi(metadata,source:"entity").FilterCategory==category,"metadata namespace classifies "+category);
Check(FilterPoi("MapExcavation:BossArena_Forge_PCR_01","Arena","boss").FilterCategory=="map-boss","recognized map arena shares completion-boss filter");
Check(bossPoi!.FilterCategory=="unique"&&ProfileSettings.Defaults("Atlas Boss Rush").Shows(bossRouteTargets[1])
    &&!ProfileSettings.Defaults("Atlas Boss Rush").Shows(bossPoi),"Unique entities do not leak into Atlas Boss Rush");
var npc=PoiTracker.FromEntity(AbyssDot("Metadata/NPC/Four_Act1/HoodedMentor") with { Category=Poe2Live.EntityCategory.Npc,Poi=false });
var chest=PoiTracker.FromEntity(AbyssDot("Metadata/Chests/GenericChest") with { Category=Poe2Live.EntityCategory.Chest,Poi=false,Opened=true });
Check(npc is { Kind:"npc" }&&chest is { Kind:"chest",Completed:true },"NPC and chests enter POI flow without map icons");
tracker.Reset("npc",[]);tracker.Update([npc!]);Check(tracker.Update([]).Length==0,"NPC do not become remembered static targets");
var atlas=ProfileSettings.Defaults("Atlas Boss Rush");
Check(MapService.RouteTarget("MapExcavation",bossRouteTargets,atlas,null)?.Id=="marker:boss:7","Atlas mode routes to map completion boss");
Check(MapService.RouteTarget("MapExcavation",bossRouteTargets,new(),null)==null,"Manual mode never chooses automatic target");
Check(MapService.RouteTarget("MapExcavation",bossRouteTargets,new(),bossPoi.Id)?.Id==bossPoi.Id,"Manual mode accepts visible manual target");
Check(MapService.RouteTarget("MapExcavation",bossRouteTargets,new(){Routing="off"},bossPoi.Id)==null,"Off mode rejects every route target");
Check(MapService.RouteTarget("MapExcavation",bossRouteTargets,atlas with { Categories=[] },null)==null,"hidden map boss does not retain automatic route");
Check(MapService.RouteTarget("G1_1",bossRouteTargets,atlas,null)==null,"Atlas mode does not auto-route campaign");
var menu=new TuiMenu(reloadedProfiles);
ConsoleKeyInfo Key(ConsoleKey key,char c='\0')=>new(c,key,false,false,false);
var mainColors=menu.Layout(MapSnapshot.Empty("waiting",""),"",false,80,24);
foreach(var label in new[]{"1  Connect to game","2  Open web map","3  Settings","4  Profiles"})
{
    var row=mainColors.Single(l=>l.Text.Contains(label));
    Check(TuiView.ColorAt(row,row.Text.IndexOf(label,StringComparison.Ordinal))==(label.StartsWith('1')?"255;255;255":"180;180;180"),"uniform gray and bright selected color: "+label);
    Check(!TuiView.BoldAt(row,0)&&!TuiView.BoldAt(row,row.Text.Length-1),"selection never changes border weight: "+label);
}
menu.Handle(Key(ConsoleKey.D4,'4'));menu.Handle(Key(ConsoleKey.D2,'2'));
Check(menu.Page=="Profiles"&&reloadedProfiles.Read().Profile=="Act Rush","number keys select profile through TUI");
menu.Handle(Key(ConsoleKey.Escape));menu.Handle(Key(ConsoleKey.D3,'3'));menu.Handle(Key(ConsoleKey.Enter));menu.Handle(Key(ConsoleKey.Enter));
Check(menu.Page=="Mechanics","TUI opens Settings / Active POI / Mechanics");
for(var i=0;i<20;i++)menu.Handle(Key(ConsoleKey.DownArrow));
foreach(var (w,h) in new[]{(120,30),(80,24),(65,18),(40,6),(16,3),(4,1)})
{
    var layout=menu.Layout(MapSnapshot.Empty("waiting",""),"",false,w,h);
    Check(layout.Count==h&&layout.All(l=>l.Text.Length<w)&&layout.Any(l=>l.Text.Contains("> ")),"settings selection stays visible at "+w+"x"+h);
    if(w>=65&&h>=18)
    {
        Check(layout.Any(l=>l.Text.Contains("Profile: Act Rush")),"profile label is explicit in the right column");
        var home=TuiView.Layout(MapSnapshot.Empty("waiting",""),"",false,w,h);
        var bodyRows=Math.Min(h-5,19);
        Check(Enumerable.Range(1,bodyRows).All(i=>layout[i].Text.Split('│')[1]==home[i].Text.Split('│')[1]),"settings keep main-menu logo and status column at "+w+"x"+h);
        Check(layout.Any(l=>l.Text.Contains("> [ ] Incursion"))&&layout.Any(l=>l.Text.Contains("Esc Back")),"scrolling right column retains selected mechanic and back hint");
        var statusRow=layout.FirstOrDefault(l=>l.Text.Contains("● "));
        if(statusRow.Text!=null) Check(TuiView.ColorAt(statusRow,statusRow.Text.IndexOf('●'))=="112;119;130","right-column selection does not recolor connection status");
    }
}
menu.Handle(Key(ConsoleKey.Spacebar,' '));
Check(reloadedProfiles.Value.Categories.Contains("incursion"),"space toggles selected profile filter");
var enabledMenu=menu.Layout(MapSnapshot.Empty("waiting",""),"",false,80,24);
foreach(var text in new[]{"Mechanics","Profile: Act Rush","Esc Back","↑↓ Select"})
{
    var accentRow=enabledMenu.Single(l=>l.Text.Contains(text));
    Check(TuiView.ColorAt(accentRow,accentRow.Text.IndexOf(text,StringComparison.Ordinal))=="210;80;80","non-selectable label retains red accent: "+text);
}
var compactAccents=menu.Layout(MapSnapshot.Empty("waiting",""),"",false,40,6);
Check(compactAccents[0].Color=="210;80;80"&&compactAccents[^1].Color=="210;80;80","compact headings and help retain red accents");
Check(enabledMenu.Any(l=>l.Text.Contains("[✓] Incursion"))&&!enabledMenu.Any(l=>l.Text.Contains("[x]")),"enabled POI use checkmarks");
var enabledRow=enabledMenu.Single(l=>l.Text.Contains("> [✓] Incursion"));
Check(TuiView.ColorAt(enabledRow,enabledRow.Text.IndexOf('>'))=="255;255;255"&&!TuiView.BoldAt(enabledRow,0),"settings selection is bright and borders remain regular weight");
menu.Handle(Key(ConsoleKey.Escape));menu.Handle(Key(ConsoleKey.Escape));
menu.Handle(Key(ConsoleKey.DownArrow));menu.Handle(Key(ConsoleKey.DownArrow));menu.Handle(Key(ConsoleKey.Enter));
Check(menu.Page=="Display","back restores parent selection");
for(var i=0;i<3;i++)menu.Handle(Key(ConsoleKey.DownArrow));
menu.Handle(Key(ConsoleKey.LeftArrow));
Check(reloadedProfiles.Value.Opacity==.8,"left arrow adjusts bounded display value");
Check(menu.Handle(Key(ConsoleKey.Q,'й'))=='q',"physical Q works in settings with Russian keyboard");
menu.Handle(Key(ConsoleKey.Escape));
menu.Handle(Key(ConsoleKey.UpArrow));menu.Handle(Key(ConsoleKey.UpArrow));menu.Handle(Key(ConsoleKey.Enter));
menu.Handle(Key(ConsoleKey.DownArrow));menu.Handle(Key(ConsoleKey.DownArrow));menu.Handle(Key(ConsoleKey.Enter));
Check(menu.Page=="Enemies","Enemies remains a separate TUI section");
Check(!menu.Layout(MapSnapshot.Empty("waiting",""),"",false,80,24).Any(row=>row.Text.Contains("Normal")),"TUI Enemies has no Normal option");
foreach(var rarity in new[]{"magic","rare"})
{
    menu.Handle(Key(ConsoleKey.Spacebar,' '));
    Check(reloadedProfiles.Value.Categories.Contains(rarity),"TUI enables "+rarity+" in Act Rush");
    menu.Handle(Key(ConsoleKey.DownArrow));
}
var store=new SettingsStore(System.IO.Path.Combine(dir,"settings.json"));store.Save(new(){Labels=false,Names=new(){["a"]="Моя цель"}});
Check(new SettingsStore(System.IO.Path.Combine(dir,"settings.json")).Value.Names["a"]=="Моя цель","settings persist unicode names");
Check(!new DisplaySettings{Opacity=double.NaN}.Valid(),"invalid settings rejected");
File.WriteAllText(System.IO.Path.Combine(dir,"bad.json"),"{broken");Check(new SettingsStore(System.IO.Path.Combine(dir,"bad.json")).LoadWarning!=null,"corrupt settings reported with defaults");
using(var manual=new MapService(dir,demo:true,autoConnect:false))
{
    await Task.Delay(250);
    Check(manual.Snapshot.Message=="Waiting for connection command","manual start does not read before connect");
    manual.Connect();
    for(var i=0;i<100&&manual.Snapshot.Status!="demo";i++)await Task.Delay(50);
    Check(manual.Snapshot.Status=="demo","connect starts service worker");
}
using var service=new MapService(dir,true);await using var server=await LocalServer.Start(service);
using var client=new HttpClient{BaseAddress=new Uri(server.Url)};
for(var i=0;i<100&&service.Snapshot.Status!="demo";i++)await Task.Delay(50);
var snap=service.Snapshot;Check(snap.Status=="demo"&&snap.Picture?.Png.Length>100,"demo publishes map and PNG without game");
Check(!service.Select("old-instance","tile:exit"),"stale navigation command rejected");
Check(service.Select(snap.Instance,"tile:isolated"),"valid target accepted");
for(var i=0;i<100&&service.Snapshot.RouteStatus!="Путь не найден";i++)await Task.Delay(50);
Check(service.Snapshot.RouteStatus=="Путь не найден","no-path status reaches snapshot");
Check((await client.PostAsJsonAsync("/api/demo",new{enabled=true})).StatusCode==HttpStatusCode.Forbidden,"mutation without Origin blocked");
client.DefaultRequestHeaders.Add("Origin",server.Url);
Check((await client.PostAsJsonAsync("/api/settings",new DisplaySettings{IconSize=999})).StatusCode==HttpStatusCode.BadRequest,"invalid settings API rejected");
Check((await client.GetAsync("/api/terrain?instance=old")).StatusCode==HttpStatusCode.NotFound,"stale terrain not served");
var state=await client.GetStringAsync("/api/state");using var json=JsonDocument.Parse(state);Check(json.RootElement.GetProperty("snapshot").GetProperty("targets").GetArrayLength()==9,"local API exposes merged targets including demo monsters");
Check(new[]{"magic","rare"}.All(kind=>json.RootElement.GetProperty("snapshot").GetProperty("targets").EnumerateArray()
    .Any(p=>p.GetProperty("kind").GetString()==kind&&p.GetProperty("filterCategory").GetString()==kind)),"API preserves independent monster categories for browser filtering");
Check(!json.RootElement.GetProperty("snapshot").TryGetProperty("playerAddress",out _),"player process address stays out of local API");
Check(json.RootElement.GetProperty("profile").GetString()=="Default"&&json.RootElement.GetProperty("filters").GetArrayLength()==PoiCatalog.All.Length,"state exposes profile and shared filter catalogue");
var webView=service.Settings.Read();
LocalServer.SettingsCommand WebCommand(SettingsView v,DisplaySettings value,bool reset=false)=>new(v.Profile,v.Instance,v.Revision,value,reset);
var filterResponse=await client.PostAsJsonAsync("/api/filters",WebCommand(webView,webView.Settings with { Categories=[] }));
Check(filterResponse.IsSuccessStatusCode&&service.Settings.Value.Categories.Length==0,"web filter changes effective overlay settings");
Check(service.Snapshot.Selected==null&&service.Snapshot.Route?.Length==0,"hiding selected target clears published route immediately");
Check(!service.Select(snap.Instance,"tile:exit"),"hidden target cannot be selected");
Check((await client.PostAsJsonAsync("/api/settings",WebCommand(webView,webView.Settings))).StatusCode==HttpStatusCode.Conflict,"stale browser revision is rejected by API");
Check((await client.PostAsJsonAsync("/api/filters",WebCommand(service.Settings.Read() with { Instance="old-instance" },new()))).StatusCode==HttpStatusCode.Conflict,"stale map filter is rejected by API");
webView=service.Settings.Read();
Check((await client.PostAsJsonAsync("/api/filters",WebCommand(webView,webView.Settings,true))).IsSuccessStatusCode&&service.Settings.Value.Categories.Length>0,"web can restore profile filters");
service.Settings.SelectProfile("Act Rush");
Check(!service.Select(snap.Instance,"tile:exit")&&service.Snapshot.Route?.Length==0,"Off rejects manual routes and clears existing route");
webView=service.Settings.Read();
Check((await client.PostAsJsonAsync("/api/target",new LocalServer.TargetCommand(snap.Instance,"tile:exit",webView.Profile,webView.Revision))).StatusCode==HttpStatusCode.Conflict,"Off also rejects target API");
service.Settings.SelectProfile("Default");
Check(service.Select(snap.Instance,"tile:exit"),"manual routing resumes in Default");
for(var i=0;i<100&&service.Snapshot.Selected!="tile:exit";i++)await Task.Delay(50);
Check(service.Snapshot.Route is {Length:>0},"manual route is published in demo");
service.Settings.Update(s=>s with { Routing="off" });service.Settings.Update(s=>s with { Routing="manual" });
Check(service.Snapshot.Selected==null,"rapid Off/Manual changes cannot resurrect previous route");
service.Select(snap.Instance,"tile:exit");
for(var i=0;i<100&&service.Snapshot.Selected!="tile:exit";i++)await Task.Delay(50);
var oldProfile=service.Settings.Read();service.Settings.SelectProfile("Custom 1");
Check(service.Snapshot.Selected==null,"profile switch immediately drops previous route");
Check((await client.PostAsJsonAsync("/api/target",new LocalServer.TargetCommand(snap.Instance,"tile:exit",oldProfile.Profile,oldProfile.Revision))).StatusCode==HttpStatusCode.Conflict,"target from an old profile is rejected");
await Task.Delay(200);Check(service.Snapshot.Selected==null,"queued/manual selection does not reappear after profile switch");
service.Demo(true);for(var i=0;i<100&&service.Snapshot.Instance==snap.Instance;i++)await Task.Delay(50);
for(var i=0;i<100&&service.Snapshot.Status!="demo";i++)await Task.Delay(50);
Check(service.Snapshot.Instance!=snap.Instance&&service.Snapshot.Selected==null,"new instance clears selected route");
Console.WriteLine($"{checks} checks passed. Synthetic fixtures and an anonymized terrain crop do not certify full live compatibility.");



