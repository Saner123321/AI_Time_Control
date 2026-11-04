using System.Diagnostics;
using System.IO;
using System.Windows;
using Aict.Core;
namespace Aict.Beta.Desktop;
internal static class TraySelfTest {
    sealed class Surface:ITraySurface {
        public event Action? OpenRequested;public event Action? ExitRequested;public bool Visible,Disposed;
        public void Show()=>Visible=true;public void Open()=>OpenRequested?.Invoke();public void Exit()=>ExitRequested?.Invoke();
        public void Dispose(){Visible=false;Disposed=true;}
    }
    static string Output=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","tray-self-test"));
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    public static async Task Run(){
        Directory.CreateDirectory(Output);var window=new Window{Title="AICT isolated tray fixture",Width=200,Height=100,ShowInTaskbar=false,ShowActivated=false,Left=-10000,Top=-10000};bool closed=false;int exits=0;window.Closed+=(_,_)=>closed=true;
        var surface=new Surface();using var tray=new TrayController(window,surface,()=>{exits++;window.Close();},activate:false);window.Show();window.Close();
        Require(!window.IsVisible&&!closed&&surface.Visible&&exits==0,"Close did not hide only the window");
        window.WindowState=WindowState.Minimized;surface.Open();Require(window.IsVisible&&window.WindowState==WindowState.Normal,"Tray open did not restore window");
        window.Close();string token=Guid.NewGuid().ToString("N");using(var owner=new DesktopInstance("AICT-Desktop-Test-"+token)){
            Require(owner.IsPrimary,"Fixture primary instance missing");int activation=0;owner.Listen(command=>window.Dispatcher.InvokeAsync(()=>{activation++;tray.Open();}).Task);
            using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,ArgumentList={"--activation-self-test-client",token}})??throw new IOException("Fixture client did not start");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Require(child.ExitCode==0&&activation==1&&window.IsVisible,"Second process did not activate the existing window");
        }
        surface.Exit();Require(exits==1&&closed&&surface.Disposed&&!surface.Visible,"Explicit tray exit did not close/dispose");surface.Exit();Require(exits==1,"Exit executed twice");
        using(var image=TrayIcon.Create()){using var bitmap=image.ToBitmap();bitmap.Save(Path.Combine(Output,"tray-icon.png"),System.Drawing.Imaging.ImageFormat.Png);}
        File.WriteAllText(Path.Combine(Output,"result.json"),PolicyJson.Serialize(new{passed=true,closeHides=true,restoreFromMinimized=true,secondProcessReopensExistingWindow=true,explicitExit=true,iconDisposed=true,realNotificationIconCreated=false,systemWrites=false}));
    }
    public static void Failed(Exception e){try{Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"failure.json"),PolicyJson.Serialize(new{passed=false,error=e.ToString()}));}catch{}}
}
