using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using WPoint = System.Windows.Point;

namespace pathofsight;

public static class Projection
{
    public static Point At(Point cell, Point player, Point center, float scale)
    {
        var result = MapProjection.GridToMapPoint(new() { X=cell.X,Y=cell.Y }, new() { X=player.X,Y=player.Y },
            new() { X=center.X,Y=center.Y }, scale);
        return new(result.X,result.Y);
    }
}

public sealed class MapSurface : FrameworkElement
{
    public MapSnapshot Snapshot { get; set; } = MapSnapshot.Empty("waiting", "");
    public DisplaySettings Settings { get; set; } = new();
    public float PixelHeight { get; set; }
    public double Dpi { get; set; } = 1;
    public bool Preview { get; set; }
    public static string ColorFor(string kind) => kind switch
    {
        "transition" => "#D9E6AC", "waypoint" => "#72C4F0", "checkpoint" => "#83E1C1",
        "boss" => "#F09382", "quest" => "#E2C379", "shrine" => "#C4A3F2",
        "magic" => "#75A7FF", "rare" => "#F4D35E", _ => "#B0B9B0"
    };
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var s = Snapshot; var settings = Settings;
        if (s.Player is not { } player || s.Picture == null) return;
        Point center; float scale;
        if (Preview)
        {
            scale = (float)Math.Min((ActualWidth-100)/((s.Width+s.Height)*MapProjection.CameraCos), (ActualHeight-100)/((s.Width+s.Height)*MapProjection.CameraSin));
            player = new(s.Width/2f,s.Height/2f); center = new((float)ActualWidth/2,(float)ActualHeight/2);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16,23,22)),null,new Rect(RenderSize));
        }
        else
        {
            center = new((float)(ActualWidth/2+(s.MapUi.ShiftX+settings.OffsetX)/Dpi),
                (float)(ActualHeight/2+(s.MapUi.ShiftY-20+settings.OffsetY)/Dpi));
            scale = (float)(s.MapUi.Zoom*(PixelHeight/677f)*settings.Scale/Dpi);
        }
        WPoint Project(Point p) { var q = Projection.At(p,player,center,scale); return new(q.X,q.Y); }
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.PushOpacity(settings.Opacity);
        if (settings.Terrain)
        {
            var origin = Project(new(0,0)); var ex = Project(new(1,0))-origin; var ey = Project(new(0,1))-origin;
            dc.PushTransform(new MatrixTransform(ex.X,ex.Y,ey.X,ey.Y,origin.X,origin.Y));
            dc.DrawImage(s.Picture.Bitmap,new Rect(0,0,s.Width,s.Height)); dc.Pop();
        }
        if (s.Route is { Length: > 1 } route)
        {
            var path = new StreamGeometry(); using(var geometry=path.Open())
            { geometry.BeginFigure(Project(route[0]),false,false); geometry.PolyLineTo(route.Skip(1).Select(Project).ToArray(),true,false); }
            path.Freeze(); dc.DrawGeometry(null,new Pen(Brushes.LightGoldenrodYellow,2),path);
        }
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var p in s.Targets.Where(settings.Shows))
        {
            var point = Project(p.Position);
            if (point.X < -200 || point.Y < -40 || point.X > ActualWidth+40 || point.Y > ActualHeight+40) continue;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorFor(p.Kind)));
            if(p.Completed) dc.PushOpacity(.45);
            var radius = settings.IconSize;
            dc.DrawEllipse(p.Source=="entity"?brush:null,new Pen(brush,1.5),point,radius,radius);
            if (p.Id==s.Selected) dc.DrawEllipse(null,new Pen(brush,2),point,radius+5,radius+5);
            if(settings.Labels && !(p.Source=="entity" && p.Kind is "rare" or "boss"))
            {
                var label = new FormattedText(settings.Label(p),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,brush,pixelsPerDip);
                label.MaxTextWidth=280;
                var rect = new Rect(point.X+radius+4,point.Y-9,label.Width+6,label.Height+2);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190,12,20,16)),null,rect,3,3);
                dc.DrawText(label,new WPoint(rect.X+3,rect.Y));
            }
            if(p.Completed)dc.Pop();
        }
        var pp = Project(s.Player); dc.DrawEllipse(Brushes.White,new Pen(Brushes.Black,1),pp,4,4);
        dc.Pop();dc.Pop();
    }
}

public sealed class OverlayWindow : Window
{
    private readonly MapService _service;
    private readonly MapSurface _surface = new();
    private readonly DispatcherTimer _timer = new() { Interval=TimeSpan.FromMilliseconds(16) };
    private ProcessHandle? _poseProcess;
    private Poe2Live? _poseLive;
    private string _poseInstance = "";
    private int _x=-1,_y=-1,_width=-1,_height=-1;
    private nint _hwnd;
    public OverlayWindow(MapService service)
    {
        _service=service; Content=_surface; Width=1;Height=1;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        ShowInTaskbar=false;ShowActivated=false;Topmost=true;ResizeMode=ResizeMode.NoResize;IsHitTestVisible=false;
        SourceInitialized+=(_,_)=>
        {
            _hwnd=new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtrW(_hwnd,-20,Native.GetWindowLongPtrW(_hwnd,-20)|0x20|0x80|0x08000000);
        };
        _timer.Tick+=(_,_)=>Tick();Closed+=(_,_)=>{_timer.Stop();DropPose();};_timer.Start();
    }
    private void DropPose()
    {
        _poseProcess?.Dispose(); _poseProcess=null; _poseLive=null; _poseInstance="";
    }
    private void Tick()
    {
        var s=_service.Snapshot;var settings=_service.Settings.Value;var foreground=Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground,out var pid);
        if(s.Status!="ready" || !s.Fresh)DropPose();
        if(s.Status!="ready" || !s.Fresh || !s.MapUi.IsVisible || !settings.Overlay || pid!=s.ProcessId
            || !Native.GetClientRect(foreground,out var rect))
        { if(IsVisible)Hide();return; }
        if (s.PlayerAddress != 0 && s.Terrain is { } terrain)
        {
            if (_poseInstance != s.Instance || _poseProcess?.ProcessId != s.ProcessId)
            {
                DropPose();
                try
                {
                    _poseProcess=ProcessHandle.AttachToProcess(s.ProcessId);
                    _poseLive=new Poe2Live(new MemoryReader(_poseProcess),0);
                    _poseInstance=s.Instance;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { DropPose(); }
            }
            if (_poseLive?.PlayerGrid((nint)s.PlayerAddress) is { } grid)
            {
                var point=new Point(grid.X,grid.Y);
                if (point.In(terrain)) s=s with { Player=point };
            }
        }
        var pos=new Native.NativePoint(); if(!Native.ClientToScreen(foreground,ref pos))return;
        var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
        if(width<=0||height<=0){Hide();return;}
        var wasVisible=IsVisible;if(!wasVisible)Show();
        if(!wasVisible || pos.X!=_x || pos.Y!=_y || width!=_width || height!=_height)
        {
            Native.SetWindowPos(_hwnd,(nint)(-1),pos.X,pos.Y,width,height,0x0010);
            _x=pos.X;_y=pos.Y;_width=width;_height=height;
        }
        var dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
        if(s.Updated!=_surface.Snapshot.Updated || s.Player!=_surface.Snapshot.Player || settings!=_surface.Settings
            || height!=_surface.PixelHeight || dpi!=_surface.Dpi)
        {
            _surface.Dpi=dpi;_surface.PixelHeight=height;
            _surface.Snapshot=s;_surface.Settings=settings;_surface.InvalidateVisual();
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]public struct NativePoint{public int X,Y;}
        [StructLayout(LayoutKind.Sequential)]public struct NativeRect{public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")]public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
        [DllImport("user32.dll")]public static extern bool GetClientRect(nint hwnd,out NativeRect rect);
        [DllImport("user32.dll")]public static extern bool ClientToScreen(nint hwnd,ref NativePoint point);
        [DllImport("user32.dll")]public static extern bool SetWindowPos(nint hwnd,nint insert,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")]public static extern nint GetWindowLongPtrW(nint hwnd,int index);
        [DllImport("user32.dll")]public static extern nint SetWindowLongPtrW(nint hwnd,int index,nint value);
    }
}
