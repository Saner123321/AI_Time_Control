using System.Windows;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
namespace Aict.Beta.Desktop;
public partial class App:Application {
    protected override void OnStartup(StartupEventArgs e){base.OnStartup(e);try{
        if(e.Args.SequenceEqual(new[]{"--build-info"})){Console.WriteLine(Aict.Core.PolicyJson.Serialize(new{hostTrial=Aict.Core.BuildMode.HostTrial,trialMinutes=Aict.Core.BuildMode.TrialMinutes}));Shutdown(0);return;}
        if(e.Args.SequenceEqual(new[]{"--self-test"})){MainWindow=new MainWindow(new BetaConnection());MainWindow.Show();return;}
        Platform.Guard();if(e.Args.Contains("--guard-check"))throw new InvalidOperationException("Guard check expected physical host");
        string? root=Platform.FindExisting();if(e.Args.Contains("--recovery")){MainWindow=new RecoveryWindow(root);MainWindow.Show();return;}if(root is not null && Platform.Running(root)){var connection=new BetaConnection(root);MainWindow=new MainWindow(connection);MainWindow.Show();if(e.Args.Contains("--uninstall"))new DiagnosticsWindow(connection){Owner=MainWindow}.ShowDialog();}
        else{MainWindow=new SetupWindow(root);MainWindow.Show();}
    }catch(Exception error){Environment.ExitCode=1;if(!e.Args.Contains("--guard-check"))MessageBox.Show(error.Message,"专注计划",MessageBoxButton.OK,MessageBoxImage.Information);Console.Error.WriteLine(error.Message);Shutdown(1);}}
}
internal static class Platform {
    public static void Guard(){using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");if(!Aict.Core.BuildMode.HostTrial && (Convert.ToString(bios?.GetValue("SystemManufacturer"))?.Contains("VMware")!=true || Convert.ToString(bios?.GetValue("SystemProductName"))?.Contains("VMware")!=true))throw new InvalidOperationException("VMware guest required；此测试版不会在主机安装或修改封锁设置。");}
    public static string Package=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    public static string? FindExisting(){string sid=WindowsIdentity.GetCurrent().User!.Value;foreach(var d in Directory.EnumerateDirectories(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AICT-Beta-*").OrderByDescending(Directory.GetCreationTimeUtc))try{if((File.GetAttributes(d)&FileAttributes.ReparsePoint)!=0)continue;string id=Path.GetFileName(d)[10..];if(!Guid.TryParseExact(id,"N",out _))continue;using var c=JsonDocument.Parse(File.ReadAllText(Path.Combine(d,"config.json")));if(c.RootElement.GetProperty("runId").GetString()==id && c.RootElement.GetProperty("probeSid").GetString()==sid && (c.RootElement.TryGetProperty("hostTrial",out var hostTrial) && hostTrial.GetBoolean())==Aict.Core.BuildMode.HostTrial)return d;}catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){}return null;}
    public static bool Running(string root){try{using var service=new ServiceController("AICT-Beta-"+Path.GetFileName(root)[10..]);return service.Status==ServiceControllerStatus.Running;}catch(InvalidOperationException){return false;}}
    public static bool Cleaned(string? root){if(root is null)return false;try{using var c=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"state","cleanup-result.json")));return c.RootElement.GetProperty("completed").GetBoolean();}catch(Exception e)when(e is IOException or JsonException){return false;}}
    public static async Task Install(bool existing,string? root=null,bool recovery=false,bool newSession=false){Guard();string package=existing?root!:Package;string script=Path.Combine(package,@"scripts\Install-Beta.ps1");if(!File.Exists(script))throw new FileNotFoundException("找不到完整安装包，请保留原文件夹后重新解压。 ");var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};foreach(string arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script})start.ArgumentList.Add(arg);if(recovery)start.ArgumentList.Add("-Recover");else if(newSession)start.ArgumentList.Add("-NewSession");else if(existing)start.ArgumentList.Add("-StartExisting");using var p=Process.Start(start)??throw new IOException("未能启动安装向导。");await p.WaitForExitAsync();if(p.ExitCode!=0)throw new IOException("安装/修复未完成。请导出安装目录内的 setup-result.json，不要删除旧规则。 ");}
}
