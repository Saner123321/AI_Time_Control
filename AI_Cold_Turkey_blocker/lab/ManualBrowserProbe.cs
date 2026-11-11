using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

// Read-only, isolated accessibility probe. Never uses accDoDefaultAction, accSelect,
// input synthesis, browser debugging, native-host credentials or application files.
internal sealed class VisibleBrowserPage {
    public string Url {get;set;}
    public int X {get;set;} public int Y {get;set;}
    public int Width {get;set;} public int Height {get;set;}
    public Rectangle Rectangle {get{return new Rectangle(X,Y,Width,Height);}}
}
internal sealed class BrowserProbeResult {
    public string State {get;set;}
    public long Window {get;set;}
    public int Pid {get;set;}
    public long DurationMs {get;set;}
    public int OwnerX {get;set;} public int OwnerY {get;set;}
    public int OwnerWidth {get;set;} public int OwnerHeight {get;set;}
    public int DocumentsExamined {get;set;}
    public int EmptyDocumentValues {get;set;} public int NonWebDocumentValues {get;set;}
    public int HiddenDocuments {get;set;} public int RejectedBounds {get;set;}
    public int DocumentReadErrors {get;set;} public int CandidateWindows {get;set;}
    public bool Truncated {get;set;}
    public List<VisibleBrowserPage> Pages {get;set;}
    public BrowserProbeResult(){Pages=new List<VisibleBrowserPage>();}
}
internal static class ManualBrowserProbe {
    [StructLayout(LayoutKind.Sequential)] internal struct Rect {public int Left,Top,Right,Bottom;public Rectangle Box {get{return Rectangle.FromLTRB(Left,Top,Right,Bottom);}}}
    internal delegate bool EnumProc(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr state);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int capacity);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="QueryFullProcessImageNameW")] static extern bool QueryImage(IntPtr process,uint flags,StringBuilder name,ref uint length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr window,uint id,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
    [DllImport("oleacc.dll")] static extern int AccessibleChildren(IAccessible parent,int start,int count,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] object[] children,out int obtained);
    internal static string ProcessImage(uint pid){IntPtr p=OpenProcess(0x1000,false,pid);if(p==IntPtr.Zero)return null;try{uint length=32768;var name=new StringBuilder((int)length);return QueryImage(p,0,name,ref length)?name.ToString():null;}finally{CloseHandle(p);}}
    internal static bool KnownClient(string path){
        if(String.IsNullOrEmpty(path)||!Path.GetFileName(path).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase))return false;
        string parent=Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps"))+Path.DirectorySeparatorChar;
        return path.StartsWith(parent+"OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)||path.StartsWith(parent+"OpenAI.ChatGPT_",StringComparison.OrdinalIgnoreCase);
    }
    internal static string WebUrl(string value){Uri u;if(String.IsNullOrWhiteSpace(value)||value.Length>8192||!Uri.TryCreate(value,UriKind.Absolute,out u)||!(u.Scheme=="https"||u.Scheme=="http")||u.UserInfo.Length!=0)return null;return u.AbsoluteUri;}
    internal static bool Target(string url,string domain){Uri u;if(WebUrl(url)==null||!Uri.TryCreate(url,UriKind.Absolute,out u))return false;return u.IdnHost.Equals(domain,StringComparison.OrdinalIgnoreCase)||u.IdnHost.EndsWith("."+domain,StringComparison.OrdinalIgnoreCase);}
    internal static string Domain(string input){Uri u;string s=input.Trim();if(!Uri.TryCreate("https://"+s,UriKind.Absolute,out u)||u.UserInfo.Length!=0||(u.HostNameType!=UriHostNameType.Dns&&u.Host!="127.0.0.1")||u.AbsolutePath!="/"||u.Query.Length!=0||u.Fragment.Length!=0||!u.IsDefaultPort||s.IndexOfAny(new[]{'/','\\',':','@','?'})>=0)throw new ArgumentException("请只填写域名，例如 bilibili.com。");return u.IdnHost.ToLowerInvariant();}
    internal static BrowserProbeResult ReadForeground(){
        var r=new BrowserProbeResult();IntPtr window=GetAncestor(GetForegroundWindow(),2);uint pid;GetWindowThreadProcessId(window,out pid);r.Pid=(int)pid;r.Window=window.ToInt64();
        if(!KnownClient(ProcessImage(pid))){r.State="other_application_foreground";return r;}
        return ReadWindow(window,false);
    }
    internal static BrowserProbeResult ReadWindow(IntPtr window,bool fixture,bool rootOnly=false){
        var watch=Stopwatch.StartNew();var r=new BrowserProbeResult{Window=window.ToInt64()};uint pid;GetWindowThreadProcessId(window,out pid);r.Pid=(int)pid;
        if(!fixture&&!KnownClient(ProcessImage(pid))){r.State="unrecognized_client";return r;}
        Rect rect;if(!IsWindowVisible(window)||IsIconic(window)||!GetWindowRect(window,out rect)){r.State="client_not_visible";return r;}
        r.OwnerX=rect.Left;r.OwnerY=rect.Top;r.OwnerWidth=rect.Right-rect.Left;r.OwnerHeight=rect.Bottom-rect.Top;
        var windows=new List<IntPtr>();
        EnumProc callback=(w,s)=>{var name=new StringBuilder(160);GetClassName(w,name,name.Capacity);if(IsWindowVisible(w)&&(name.ToString()=="Chrome_RenderWidgetHostHWND"||fixture))windows.Add(w);return windows.Count<128;};
        if(!rootOnly)EnumChildWindows(window,callback,IntPtr.Zero);GC.KeepAlive(callback);windows.Add(window);r.CandidateWindows=windows.Distinct().Count();
        int visited=0;
        foreach(IntPtr w in windows.Distinct()){
            IAccessible root=null;var iid=new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
            try{if(AccessibleObjectFromWindow(w,0xfffffffc,ref iid,out root)<0||root==null)continue;Visit(root,0,0,rect.Box,r,ref visited);}catch(COMException){}catch(InvalidCastException){}finally{if(root!=null&&Marshal.IsComObject(root))Marshal.ReleaseComObject(root);}
            if(visited>=1200||watch.ElapsedMilliseconds>1150){r.Truncated=true;break;}
        }
        r.Pages=r.Pages.GroupBy(p=>p.Url+"|"+p.Rectangle.ToString()).Select(g=>g.First()).ToList();
        // Do not guess which nested or stale document is the visible browser pane.
        bool overlap=r.Pages.Any(a=>r.Pages.Any(b=>!Object.ReferenceEquals(a,b)&&a.Rectangle.IntersectsWith(b.Rectangle)));
        r.State=r.Truncated?"probe_budget_exceeded":overlap?"ambiguous_browser_rectangles":r.Pages.Count==0?"committed_document_url_unavailable":"committed_document_urls_observed";
        r.DurationMs=watch.ElapsedMilliseconds;return r;
    }
    static void Visit(IAccessible a,object child,int depth,Rectangle owner,BrowserProbeResult r,ref int visited){
        if(++visited>1200||depth>12)return;
        int role;try{role=Convert.ToInt32(a.get_accRole(child));}catch{return;}
        if(role==15){
            r.DocumentsExamined++;
            try{int state=Convert.ToInt32(a.get_accState(child));if((state&0x18000)!=0){r.HiddenDocuments++;return;}
                // The document's committed URL is used. Address-field text is never trusted.
                string value=a.get_accValue(child),url=WebUrl(value);
                if(url==null){if(String.IsNullOrEmpty(value))r.EmptyDocumentValues++;else r.NonWebDocumentValues++;}
                else{
                    int x,y,width,height;a.accLocation(out x,out y,out width,out height,child);var box=new Rectangle(x,y,width,height);var clipped=Rectangle.Intersect(box,owner);
                    if(width<160||height<100||clipped.Width!=width||clipped.Height!=height||r.Pages.Count>=8){r.RejectedBounds++;return;}
                    r.Pages.Add(new VisibleBrowserPage{Url=url,X=x,Y=y,Width=width,Height=height});return;
                }
            }catch(COMException){r.DocumentReadErrors++;}catch(ArgumentException){r.DocumentReadErrors++;}
            // Electron's application shell can itself be a document (empty or app://).
            // Continue through structural containers to find an embedded web document.
        }
        if(role!=9&&role!=10&&role!=14&&role!=15&&role!=16&&role!=18&&role!=20&&role!=22&&role!=24&&role!=27&&role!=28&&role!=33&&role!=35&&role!=37&&role!=38&&role!=60)return;
        if(!Object.Equals(child,0))return;
        int count;try{count=Math.Min(a.accChildCount,128);}catch{return;}if(count<=0)return;
        var nodes=new object[count];int got;if(AccessibleChildren(a,0,count,nodes,out got)<0)return;
        if(a.accChildCount>128)r.Truncated=true;
        for(int i=0;i<got&&visited<1200;i++){
            var item=nodes[i] as IAccessible;
            try{if(item!=null)Visit(item,0,depth+1,owner,r,ref visited);else if(nodes[i] is int)Visit(a,nodes[i],depth+1,owner,r,ref visited);}finally{if(item!=null&&Marshal.IsComObject(item))Marshal.ReleaseComObject(item);}
        }
    }
}
