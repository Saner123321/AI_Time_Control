using System.IO;
using System.Windows;
using Aict.Core;
using Aict.Startup;
namespace Aict.Beta.Desktop;
internal static class StartupSelfTest {
    sealed class Store:ILoginStartupStore {public string? Command{get;set;}public bool Configured{get;set;}}
    sealed class Surface:ITraySurface {
        public event Action? OpenRequested;public event Action? ExitRequested;public bool Visible;
        public void Show()=>Visible=true;public void Open()=>OpenRequested?.Invoke();public void Exit()=>ExitRequested?.Invoke();public void Dispose()=>Visible=false;
    }
    internal static void Run(){
        var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AICT-Beta-"+Guid.NewGuid().ToString("N"));var startup=new LoginStartup(root);var store=new Store();
        Check(startup.Command=="\""+Path.Combine(root,@"App\Aict.Beta.Desktop.exe")+"\" --startup","quoted_installed_executable_and_startup_argument");
        Check(startup.Initialize(store)&&store.Configured,"first_configuration_enables_login_startup");
        startup.Set(store,false);Check(store.Command is null&&!startup.Enabled(store),"disable_removes_only_own_run_value");
        Check(!startup.Initialize(store)&&store.Command is null,"manual_disable_survives_reopen_and_update");
        startup.Set(store,true);Check(startup.Enabled(store),"reenable_readback");
        store.Command=null;Check(!startup.Initialize(store),"external_removal_is_not_silently_reenabled");
        store.Command="unrelated application";bool refused=false;try{startup.Set(store,true);}catch(InvalidDataException){refused=true;}Check(refused&&store.Command=="unrelated application","foreign_entry_not_overwritten");
        Check(!startup.RemoveOwned(store)&&store.Command=="unrelated application","foreign_entry_not_removed_by_cleanup");
        store.Command=startup.Command;Check(startup.RemoveOwned(store)&&store.Command is null,"owned_cleanup_removes_startup");
        refused=false;try{_=new LoginStartup(Path.Combine(Path.GetTempPath(),"AICT-Beta-"+Guid.NewGuid().ToString("N")));}catch(InvalidDataException){refused=true;}Check(refused,"development_path_rejected");
        var window=new Window{ShowActivated=false,ShowInTaskbar=false};var surface=new Surface();using(var tray=new TrayController(window,surface,()=>window.Close(),activate:false)){
            Check(surface.Visible&&!window.IsVisible,"login_creates_tray_without_showing_window");surface.Open();Check(window.IsVisible,"tray_opens_initially_hidden_window");window.Close();Check(!window.IsVisible&&surface.Visible,"close_returns_to_tray");surface.Exit();
        }
        string output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","startup-self-test"));Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"result.json"),PolicyJson.Serialize(new{passed=true,cases=checks.Count,checks,systemWrites=false,realRunRegistryChanged=false,realNotificationIconCreated=false}));
    }
}
