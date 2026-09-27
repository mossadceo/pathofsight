using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pathofsight;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Contains("--render-demo"))return RenderDemo(args);
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        var data=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"pathofsight");
        var dataAt=Array.IndexOf(args,"--data-dir");if(dataAt>=0&&dataAt+1<args.Length)data=System.IO.Path.GetFullPath(args[dataAt+1]);
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        using var service=new MapService(data,args.Contains("--demo"),autoConnect:args.Contains("--demo"));
        LocalServer? server=null;
        OverlayWindow? overlay=null;
        Task? tuiTask=null;
        using var stop=new CancellationTokenSource();
        app.DispatcherUnhandledException+=(_,e)=>
        { Console.Error.WriteLine(e.Exception);e.Handled=true;app.Shutdown(1); };
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;app.Dispatcher.BeginInvoke(() => app.Shutdown());};
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var port=0;var portAt=Array.IndexOf(args,"--port");if(portAt>=0&&portAt+1<args.Length)port=int.Parse(args[portAt+1]);
                server=await LocalServer.Start(service,port);
                overlay=new OverlayWindow(service);overlay.Show();
                Directory.CreateDirectory(data);await File.WriteAllTextAsync(System.IO.Path.Combine(data,"server-url.txt"),server.Url);
                tuiTask=Task.Run(() => RunTui(service,server.Url,service.Settings.LoadWarning,
                    () => app.Dispatcher.BeginInvoke(() => app.Shutdown()),stop.Token));
            }
            catch(Exception e){Console.Error.WriteLine("Path Of Sight failed to start: "+e);app.Shutdown(1);}
        });
        var result=app.Run();
        stop.Cancel();
        if (!Console.IsInputRedirected) tuiTask?.GetAwaiter().GetResult();
        overlay?.Close();
        service.Dispose();
        if(server!=null)server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        service.Completion.GetAwaiter().GetResult();
        return result;
    }
    private static void RunTui(MapService service,string url,string? warning,Action exit,CancellationToken stop)
    {
        var notice=warning ?? "";
        var last="";
        var lastRender=0L;
        var connected=false;
        var quitting=false;
        if(!Console.IsOutputRedirected)Console.Title="Path Of Sight";
        using var view = new TuiView();
        var menu = new TuiMenu(service.Settings);
        void Render()
        {
            var snapshot=service.Snapshot;
            var width=Console.IsOutputRedirected?80:Console.WindowWidth;
            var height=Console.IsOutputRedirected?30:Console.WindowHeight;
            var state=$"{snapshot.Status}|{snapshot.Message}|{snapshot.Area}|{snapshot.Fresh}|{notice}|{connected}|{width}|{height}|{service.Settings.Read().Revision}|{menu.Page}|{menu.Selected}|{menu.Notice}";
            var now=Environment.TickCount64;
            if(state==last && (Console.IsOutputRedirected || now-lastRender<200))return;
            last=state;
            lastRender=now;
            view.Draw(snapshot,notice,connected,width,height,menu);
        }
        void Command(char key)
        {
            switch(char.ToLowerInvariant(key))
            {
                case '1': service.Connect();connected=true;notice="Connecting to game";break;
                case '2':
                    try { Process.Start(new ProcessStartInfo(url){UseShellExecute=true});notice="Web map opened"; }
                    catch(Exception e){notice="Could not open browser: "+e.Message;}
                    break;
                case 'q': case 'й': quitting=true;exit();break;
            }
            last="";
        }
        try
        {
            if(Console.IsInputRedirected)
            {
                Render();
                string? line;
                while(!stop.IsCancellationRequested && !quitting && (line=Console.ReadLine())!=null)
                { if(line.Length>0)Command(menu.Handle(new ConsoleKeyInfo(line[0],0,false,false,false)));if(!quitting)Render(); }
                exit();
            }
            else RunTuiLoop(() =>
            {
                if(quitting || !Console.KeyAvailable)return false;
                Command(menu.Handle(Console.ReadKey(true)));
                return true;
            },Render,stop);
        }
        catch(IOException){exit();}
        catch(InvalidOperationException){exit();}
    }
    internal static char CommandKey(ConsoleKeyInfo key) => key.Key == ConsoleKey.Q ? 'q' : key.KeyChar;
    internal static void RunTuiLoop(Func<bool> readInput, Action render, CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            while(!stop.IsCancellationRequested && readInput()) render();
            if(stop.IsCancellationRequested)break;
            render();
            stop.WaitHandle.WaitOne(10);
        }
    }

    private static int RenderDemo(string[] args)
    {
        var at=Array.IndexOf(args,"--render-demo");if(at+1>=args.Length)return 2;
        var path=System.IO.Path.GetFullPath(args[at+1]);var fixture=DemoFixture.Create();
        var picture=TerrainPicture.Create(fixture.Terrain);var points=fixture.Landmarks.Concat(fixture.Objects).ToArray();
        var route=new POE2Radar.Core.Pathfinding.PathPlanner().Plan(fixture.Terrain,(40,90),(209,40)).Select(p=>new Point(p.x,p.y)).ToArray();
        var snapshot=new MapSnapshot("demo","",Environment.TickCount64,"demo","",fixture.Player,points,route,"tile:exit",Width:240,Height:160,Terrain:fixture.Terrain,Picture:picture);
        var scale=args.Contains("--dpi150")?1.5:1;
        var surface=new MapSurface{Width=1000,Height=700,Preview=true,Snapshot=snapshot};
        surface.Measure(new Size(1000,700));surface.Arrange(new Rect(0,0,1000,700));surface.UpdateLayout();
        var image=new RenderTargetBitmap((int)(1000*scale),(int)(700*scale),96*scale,96*scale,PixelFormats.Pbgra32);image.Render(surface);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);using var output=File.Create(path);encoder.Save(output);return 0;
    }
}


