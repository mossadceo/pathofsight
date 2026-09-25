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
string[] EmbeddedArt(string name)
{
    using var stream=typeof(pathofsight.Program).Assembly.GetManifestResourceStream("pathofsight.Art."+name+".txt")!;
    using var reader=new StreamReader(stream,Encoding.UTF8);
    return reader.ReadToEnd().Split('\n');
}
var eyeArt=EmbeddedArt("eye");
using (var view = new TuiView())
{
    var snapshot = MapSnapshot.Empty("waiting", "Waiting");
    view.RenderFrame(TuiView.Layout(snapshot, "", false, 80, 30), 80, 30);
    var resized = view.RenderFrame(TuiView.Layout(snapshot, "", false, 80, 25), 80, 25);
    Check(resized.Contains("\x1b[1;1H"), "height-only resize redraws the top border after terminal reflow");
    var repeated = view.RenderFrame(TuiView.Layout(snapshot, "", false, 80, 25), 80, 25);
    Check(repeated.Contains("\x1b[1;1H"), "refresh repairs terminal reflow even when sampled dimensions are unchanged");
}
Check(eyeArt.Count(x=>x.Length>0)==8&&eyeArt.Max(x=>x.TrimEnd('\r').Length)==28&&!eyeArt.Any(x=>x.Contains("тЦ")||x.Contains('�')),"eye logo is embedded as Unicode art");
foreach (var (w,h) in new[] { (120,50), (80,24), (65,18), (64,18), (40,12), (40,4), (40,1) })
{
    var screen=TuiView.Layout(MapSnapshot.Empty("waiting","Waiting for connection command"),"Notice\n\u001b[2J",false,w,h);
    Check(screen.Count==h&&screen.All(line=>line.Text.Length<w&&!line.Text.Any(char.IsControl)), $"TUI fits {w}x{h} and sanitizes control characters");
    Check(screen.All(line=>!line.Text.Any(c=>c is >= 'А' and <= 'я' or 'Ё' or 'ё')),
        $"TUI labels are English at {w}x{h}");
    Check(screen.All(line=>!line.Text.Contains("Нажмите")&&!line.Text.Contains("- - -")&&!line.Text.Contains("127.0.0.1")),
        $"TUI has no prompt box or lower URL at {w}x{h}");
    if (h>=4) Check(screen.Any(line=>line.Text.Contains("Connect to game"))&&
        screen.Any(line=>line.Text.Contains("Open web map")), $"TUI keeps commands at {w}x{h}");
    if (h>=24) Check(screen[0].Text.Contains("Path Of Sight  v0.1.0")&&screen.Any(line=>line.Text.Contains("▗▇▇▇▇▇▇▇▍")),
        $"logo appears on startup at {w}x{h}");
}
foreach (var status in new[] { "waiting", "loading", "ready", "demo", "error", "access", "incompatible" })
{
    var screen=TuiView.Layout(MapSnapshot.Empty(status,"Status"),"",true,80,24);
    Check(screen.Take(23).All(line=>line.Text.Length==79), $"frame stays aligned for {status}");
    Check(screen.Count(line=>line.Text.Contains("1  Connect"))==1&&screen.Count(line=>line.Text.Contains("2  Open"))==1,
        $"only the two available commands appear for {status}");
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
    var eye=screen.Single(row=>row.Text.Contains("▗▇▇▇▇▇▇▇▍"));
    var start=eye.Text.IndexOf('▗');
    Check(TuiView.ColorAt(eye,start)=="255;255;255"&&TuiView.ColorAt(eye,start+10)=="180;65;65",
        $"eye is pure white with a red pupil at width {width}");
    Check(TuiView.ColorAt(eye,0)=="112;119;130",$"border color is independent of eye at width {width}");
}
var fixture=DemoFixture.Create();var tracker=new PoiTracker();tracker.Reset("a",fixture.Landmarks);
Check(tracker.Update(fixture.Objects).Length==7,"tile and entity layers combine");
var found=tracker.Update(fixture.Objects.Concat(fixture.Discovered));
Check(found.Length==7&&found.Single(p=>p.Id=="tile:exit").Source=="entity","discovered transition replaces matching tile");
Check(tracker.Update([]).Single(p=>p.Id=="tile:exit").Remembered,"discovered stationary object survives disappearance");
tracker.Reset("b",[]);Check(tracker.Update([]).Length==0,"new instance clears remembered objects");
tracker.Reset("c",[new("t","k","Exit","transition",new(10,10),"tile")]);
Check(tracker.Update([new("e","e","Checkpoint","checkpoint",new(11,10),"entity")]).Length==2,"adjacent checkpoint and exit never merge");
Check(PoiTracker.Kind("Metadata/Monsters/ShrineFireDaemon","",true)=="poi","shrine cosmetic is not shrine");
Check(PoiTracker.Kind("Metadata/Shrines/Shrine_Trigger","",true)=="shrine","actual shrine classified");
Poe2Live.EntityDot AbyssDot(string metadata) => new(90,0,new(10,10),default,Poe2Live.EntityCategory.Object,
    metadata,0,0,true,0,default,false);
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Abyss/AbyssCrack_01"))==null,"abyss cracks are not POIs");
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Abyss/AbyssFissure_01"))==null,"abyss fissures are not POIs");
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Abyss/AbyssJumpInteractable"))!=null,"abyss entrance remains a POI");
Check(PoiTracker.FromEntity(AbyssDot("Metadata/MiscellaneousObjects/Other/CrystalFissure"))!=null,"unrelated fissures remain POIs");
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
var state=await client.GetStringAsync("/api/state");using var json=JsonDocument.Parse(state);Check(json.RootElement.GetProperty("snapshot").GetProperty("targets").GetArrayLength()==7,"local API exposes merged targets");
Check(!json.RootElement.GetProperty("snapshot").TryGetProperty("playerAddress",out _),"player process address stays out of local API");
service.Demo(true);for(var i=0;i<100&&service.Snapshot.Instance==snap.Instance;i++)await Task.Delay(50);
for(var i=0;i<100&&service.Snapshot.Status!="demo";i++)await Task.Delay(50);
Check(service.Snapshot.Instance!=snap.Instance&&service.Snapshot.Selected==null,"new instance clears selected route");
Console.WriteLine($"{checks} checks passed. Synthetic fixtures and an anonymized terrain crop do not certify full live compatibility.");



