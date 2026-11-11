using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
internal sealed class ViewMask:Form {
    string heading="正在验证：只遮挡观看区域",description="页面没有关闭或断网；切换到其他软件可撤掉遮挡";
    internal void Message(string title,string details){if(title==heading&&details==description)return;heading=title;description=details;Invalidate();}
    bool configured;
    internal int PlaceCalls {get;private set;} internal int ConfigurationWrites {get;private set;}
    internal int GeometryWrites {get;private set;} internal int ShowTransitions {get;private set;}
    internal int HideTransitions {get;private set;} internal int PaintCalls {get;private set;}
    internal int PresentationRepairs {get;private set;}
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowDisplayAffinity(IntPtr window,uint affinity);
    [DllImport("user32.dll",SetLastError=true)] static extern bool GetWindowDisplayAffinity(IntPtr window,out uint affinity);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr window,uint key,byte alpha,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out ManualBrowserProbe.Rect box);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window,uint attribute,out int value,int bytes);
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x00000020|0x00000080|0x00080000;return p;}}
    internal ViewMask(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;BackColor=Color.FromArgb(20,43,47);SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);DoubleBuffered=true;}
    internal bool CaptureExcluded {get{uint a;return GetWindowDisplayAffinity(Handle,out a)&&a==0x11;}}
    internal bool InputPassThrough {get{return (GetWindowLong(Handle,-20)&0x08080020)==0x08080020;}}
    internal bool PresentedAt(Rectangle box){
        IntPtr window=Handle;ManualBrowserProbe.Rect actual;int cloaked;
        return IsWindowVisible(window)&&(GetWindowLong(window,-20)&8)!=0&&GetWindowRect(window,out actual)&&actual.Box==box&&DwmGetWindowAttribute(window,14,out cloaked,4)==0&&cloaked==0;
    }
    internal void Place(Rectangle box){
        PlaceCalls++;
        if(Bounds!=box){Bounds=box;GeometryWrites++;}
        // These writes can reset composition. Configure once per native handle,
        // rather than resetting display affinity and repainting on every timer tick.
        IntPtr window=Handle;
        if(!configured){ConfigurationWrites++;if(!SetLayeredWindowAttributes(window,0,255,2)||!SetWindowDisplayAffinity(window,0x11)||!CaptureExcluded)throw new InvalidOperationException("当前系统没有确认截图排除，已停止遮挡验证。错误码："+Marshal.GetLastWin32Error());configured=true;}
        if(!Visible){Show();ShowTransitions++;}
        // WinForms' cached TopMost/Visible values are not presentation proof.
        // Restore native placement only after a failed readback; do not rewrite
        // affinity or continuously move/repaint a healthy overlay.
        if(!PresentedAt(box)){
            PresentationRepairs++;
            if(!SetWindowPos(window,new IntPtr(-1),box.X,box.Y,box.Width,box.Height,0x250)||!PresentedAt(box))throw new InvalidOperationException("遮挡窗口的可见、置顶或位置未确认。错误码："+Marshal.GetLastWin32Error());
        }
    }
    internal new void Hide(){if(!Visible)return;HideTransitions++;base.Hide();}
    protected override void OnHandleDestroyed(EventArgs e){configured=false;base.OnHandleDestroyed(e);}
    protected override void WndProc(ref Message m){if(m.Msg==0x0084){m.Result=new IntPtr(-1);return;}base.WndProc(ref m);}
    protected override void OnPaint(PaintEventArgs e){PaintCalls++;base.OnPaint(e);var box=ClientRectangle;using(var font=new Font("Microsoft YaHei UI",16))using(var small=new Font("Microsoft YaHei UI",10)){
        TextRenderer.DrawText(e.Graphics,heading,font,new Rectangle(20,Math.Max(20,box.Height/2-45),box.Width-40,50),Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.WordBreak);
        TextRenderer.DrawText(e.Graphics,description,small,new Rectangle(20,Math.Max(75,box.Height/2+12),box.Width-40,50),Color.WhiteSmoke,TextFormatFlags.HorizontalCenter|TextFormatFlags.WordBreak);
    }}
}
