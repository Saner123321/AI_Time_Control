using System.ComponentModel;
using System.Windows;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;
namespace Aict.Beta.Desktop;
internal interface ITraySurface:IDisposable {
    event Action? OpenRequested;
    event Action? ExitRequested;
    void Show();
}
internal sealed class NativeTraySurface:ITraySurface {
    readonly Forms.NotifyIcon icon;readonly Forms.ContextMenuStrip menu=new();readonly Drawing.Icon image;
    public event Action? OpenRequested;public event Action? ExitRequested;
    public NativeTraySurface(){
        image=TrayIcon.Create();icon=new(){Icon=image,Text="专注计划 · 关闭窗口后仍在托盘运行",ContextMenuStrip=menu};
        menu.Items.Add("打开主窗口",null,(_,_)=>OpenRequested?.Invoke());menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripLabel("后台封锁独立运行"));menu.Items.Add("退出界面",null,(_,_)=>ExitRequested?.Invoke());
        icon.DoubleClick+=(_,_)=>OpenRequested?.Invoke();
    }
    public void Show()=>icon.Visible=true;
    public void Dispose(){icon.Visible=false;icon.Dispose();menu.Dispose();image.Dispose();}
}
internal sealed class TrayController:IDisposable {
    readonly Window window;readonly ITraySurface surface;readonly Action exit;readonly bool activate;bool exiting,disposed;
    public TrayController(Window window,ITraySurface surface,Action exit,bool activate=true){
        this.window=window;this.surface=surface;this.exit=exit;this.activate=activate;
        // Do not intercept closing until the icon has been created successfully.
        try{surface.Show();}catch{surface.Dispose();throw;}
        window.Closing+=Closing;surface.OpenRequested+=Open;surface.ExitRequested+=Exit;
    }
    void Closing(object? sender,CancelEventArgs e){if(exiting)return;e.Cancel=true;window.Hide();}
    public void Open(){if(disposed)return;window.Dispatcher.Invoke(()=>{window.Show();if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;if(activate)window.Activate();});}
    public void Exit(){if(exiting)return;PrepareExit();exit();}
    public void PrepareExit(){exiting=true;Dispose();}
    public void Dispose(){if(disposed)return;disposed=true;window.Closing-=Closing;surface.OpenRequested-=Open;surface.ExitRequested-=Exit;surface.Dispose();}
}
internal static class TrayIcon {
    [System.Runtime.InteropServices.DllImport("user32.dll")] [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] static extern bool DestroyIcon(IntPtr icon);
    public static Drawing.Icon Create(){
        using var bitmap=new Drawing.Bitmap(32,32);using var g=Drawing.Graphics.FromImage(bitmap);g.SmoothingMode=Drawing.Drawing2D.SmoothingMode.AntiAlias;g.Clear(Drawing.Color.Transparent);
        using var path=new Drawing.Drawing2D.GraphicsPath();path.AddArc(1,1,8,8,180,90);path.AddArc(23,1,8,8,270,90);path.AddArc(23,23,8,8,0,90);path.AddArc(1,23,8,8,90,90);path.CloseFigure();
        using var fill=new Drawing.SolidBrush(Drawing.Color.FromArgb(25,125,131));g.FillPath(fill,path);using var pen=new Drawing.Pen(Drawing.Color.White,2.3f);pen.StartCap=pen.EndCap=Drawing.Drawing2D.LineCap.Round;g.DrawEllipse(pen,7,7,18,18);g.DrawLine(pen,16,10,16,16);g.DrawLine(pen,16,16,21,19);
        IntPtr handle=bitmap.GetHicon();try{using var original=Drawing.Icon.FromHandle(handle);return (Drawing.Icon)original.Clone();}finally{DestroyIcon(handle);}
    }
}
