using System.Windows;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
namespace Aict.Beta.Desktop;
public partial class App:Application {
    TrayController? tray;DesktopInstance? instance;BetaConnection? primaryConnection;
    protected override async void OnStartup(StartupEventArgs e){base.OnStartup(e);bool test=e.Args.Any(a=>a.Contains("self-test",StringComparison.Ordinal));try{
        if(e.Args.SequenceEqual(new[]{"--build-info"})){Console.WriteLine(Aict.Core.PolicyJson.Serialize(new{hostTrial=Aict.Core.BuildMode.HostTrial,trialMinutes=Aict.Core.BuildMode.TrialMinutes}));Shutdown(0);return;}
        if(e.Args.SequenceEqual(new[]{"--self-test"})){MainWindow=new MainWindow(new BetaConnection()){ShowActivated=false};MainWindow.Show();return;}
        if(e.Args.SequenceEqual(new[]{"--tray-self-test"})){ShutdownMode=ShutdownMode.OnExplicitShutdown;await TraySelfTest.Run();Shutdown(0);return;}
        if(e.Args.Length==2&&e.Args[0]=="--activation-self-test-client"&&Guid.TryParseExact(e.Args[1],"N",out _)){
            ShutdownMode=ShutdownMode.OnExplicitShutdown;using var client=new DesktopInstance("AICT-Desktop-Test-"+e.Args[1]);if(client.IsPrimary){Shutdown(2);return;}await client.Activate("show");Shutdown(0);return;
        }
        Platform.Guard();if(e.Args.Contains("--guard-check"))throw new InvalidOperationException("Guard check expected physical host");
        string? root=Platform.FindExisting();if(e.Args.Contains("--recovery")){MainWindow=new RecoveryWindow(root);MainWindow.Show();return;}
        if(root is not null && Platform.Running(root))await OpenMainWindow(new BetaConnection(root),e.Args.Contains("--uninstall"));
        else{MainWindow=new SetupWindow(root);MainWindow.Show();}
    }catch(Exception error){Environment.ExitCode=1;if(test)TraySelfTest.Failed(error);else if(!e.Args.Contains("--guard-check"))MessageBox.Show(error.Message,"专注计划",MessageBoxButton.OK,MessageBoxImage.Information);Console.Error.WriteLine(error.Message);Shutdown(1);}}
    internal async Task OpenMainWindow(BetaConnection connection,bool uninstall=false){
        if(connection.Root is {} root&&instance is null){
            string name="AICT-Desktop-"+Path.GetFileName(root)+"-"+WindowsIdentity.GetCurrent().User!.Value;instance=new(name);
            if(!instance.IsPrimary){try{await instance.Activate(uninstall?"uninstall":"show");}catch(Exception e){MessageBox.Show("已有界面正在运行，但暂时未能打开。请从右下角专注计划图标打开。\n"+e.Message,"专注计划");}Shutdown();return;}
        }
        var window=new MainWindow(connection);MainWindow=window;primaryConnection=connection;
        try{tray=new(window,new NativeTraySurface(),()=>Shutdown());ShutdownMode=ShutdownMode.OnExplicitShutdown;
            using var image=TrayIcon.Create();var bitmap=System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(image.Handle,Int32Rect.Empty,System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());bitmap.Freeze();window.Icon=bitmap;
        }catch(Exception e){tray?.Dispose();tray=null;ShutdownMode=ShutdownMode.OnLastWindowClose;MessageBox.Show("托盘图标未能创建，窗口将保持普通关闭方式。后台封锁仍独立运行。\n"+e.Message,"专注计划");}
        window.Show();instance?.Listen(command=>Dispatcher.InvokeAsync(()=>{if(tray is not null)tray.Open();else{window.Show();if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;window.Activate();}if(command=="uninstall")Dispatcher.BeginInvoke(new Action(ShowDiagnostics));}).Task);
        if(uninstall)ShowDiagnostics();
    }
    void ShowDiagnostics(){if(primaryConnection is null||MainWindow is null)return;var existing=Windows.OfType<DiagnosticsWindow>().FirstOrDefault();if(existing is not null){existing.Activate();return;}new DiagnosticsWindow(primaryConnection){Owner=MainWindow}.ShowDialog();}
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e){base.OnSessionEnding(e);if(!e.Cancel){tray?.PrepareExit();Shutdown();}}
    protected override void OnExit(ExitEventArgs e){tray?.PrepareExit();instance?.Dispose();base.OnExit(e);}
}

internal static class Platform {
    public static void Guard(){using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");if(!Aict.Core.BuildMode.HostTrial && (Convert.ToString(bios?.GetValue("SystemManufacturer"))?.Contains("VMware")!=true || Convert.ToString(bios?.GetValue("SystemProductName"))?.Contains("VMware")!=true))throw new InvalidOperationException("VMware guest required；此测试版不会在主机安装或修改封锁设置。");}
    public static string Package=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    public static string? FindExisting(){string sid=WindowsIdentity.GetCurrent().User!.Value;foreach(var d in Directory.EnumerateDirectories(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AICT-Beta-*").OrderByDescending(Directory.GetCreationTimeUtc))try{if((File.GetAttributes(d)&FileAttributes.ReparsePoint)!=0)continue;string id=Path.GetFileName(d)[10..];if(!Guid.TryParseExact(id,"N",out _))continue;using var c=JsonDocument.Parse(File.ReadAllText(Path.Combine(d,"config.json")));if(c.RootElement.GetProperty("runId").GetString()==id && c.RootElement.GetProperty("probeSid").GetString()==sid && (c.RootElement.TryGetProperty("hostTrial",out var hostTrial) && hostTrial.GetBoolean())==Aict.Core.BuildMode.HostTrial)return d;}catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){}return null;}
    public static bool Running(string root){try{using var service=new ServiceController("AICT-Beta-"+Path.GetFileName(root)[10..]);return service.Status==ServiceControllerStatus.Running;}catch(InvalidOperationException){return false;}}
    public static bool Cleaned(string? root){if(root is null)return false;try{using var c=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"state","cleanup-result.json")));return c.RootElement.GetProperty("completed").GetBoolean();}catch(Exception e)when(e is IOException or JsonException){return false;}}
    public static async Task Install(bool existing,string? root=null,bool recovery=false,bool newSession=false,bool shortTrial=false){Guard();string package=existing?root!:Package;string script=Path.Combine(package,@"scripts\Install-Beta.ps1");if(!File.Exists(script))throw new FileNotFoundException("找不到完整安装包，请保留原文件夹后重新解压。 ");var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};foreach(string arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script})start.ArgumentList.Add(arg);if(recovery)start.ArgumentList.Add("-Recover");else if(newSession)start.ArgumentList.Add("-NewSession");else if(existing)start.ArgumentList.Add("-StartExisting");if(shortTrial && (!existing || newSession))start.ArgumentList.Add("-ShortTrial");using var p=Process.Start(start)??throw new IOException("未能启动安装向导。");await p.WaitForExitAsync();if(p.ExitCode!=0)throw new IOException("安装/修复未完成。请导出安装目录内的 setup-result.json，不要删除旧规则。 ");}
}
