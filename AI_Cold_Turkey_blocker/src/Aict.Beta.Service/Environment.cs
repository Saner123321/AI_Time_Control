using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Aict.Core;
using Microsoft.Win32;
namespace Aict.Beta.Service;
internal sealed record Configuration(string RunId,string ProbeSid,string TargetSipHash,long TargetSize,bool InteractiveMode=true,bool HostTrial=false,DateTimeOffset? HostTrialEndsUtc=null,bool ContinuousUse=false);
internal static class Env {
    static readonly object LogGate=new();
    public static string Root=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    public static Configuration Config {get;private set;}=null!;
    public static string Name=>"AICT-Beta-"+Config.RunId;
    public static string Pipe=>"AICT-beta-"+Config.RunId;
    public static void Guard(bool installer=false) {
        using(var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))if(!BuildMode.HostTrial && (Convert.ToString(bios?.GetValue("SystemManufacturer"))?.Contains("VMware")!=true || Convert.ToString(bios?.GetValue("SystemProductName"))?.Contains("VMware")!=true))throw new InvalidOperationException("VMware guest required; no files or policy changed.");
        using var identity=WindowsIdentity.GetCurrent();if(installer?!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator):identity.User?.Value!="S-1-5-18")throw new UnauthorizedAccessException("Wrong privileged operation identity");
        string folder=Path.GetFileName(Root);if(!folder.StartsWith("AICT-Beta-",StringComparison.Ordinal) || !Guid.TryParseExact(folder[10..],"N",out _) || !string.Equals(Root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),folder),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected protected installation root");
        foreach(string dir in new[]{Root,Path.Combine(Root,"state"),Path.Combine(Root,"Service")}) {var info=new DirectoryInfo(dir);if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Reparse input refused");CheckAcl(info.GetAccessControl());}
        var file=new FileInfo(Path.Combine(Root,"config.json"));if(file.Length>8192 || (file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Invalid config");CheckAcl(file.GetAccessControl());Config=PolicyJson.Parse<Configuration>(File.ReadAllText(file.FullName));if(Config.RunId!=folder[10..] || !Config.ProbeSid.StartsWith("S-1-5-21-",StringComparison.Ordinal))throw new InvalidDataException("Invalid installation identity");_ = new SecurityIdentifier(Config.ProbeSid);
        if(BuildMode.HostTrial)HostInstallation.Validate(Config.HostTrial,Config.ContinuousUse,Config.HostTrialEndsUtc);
    }
    public static void CheckAcl(FileSystemSecurity acl) {if(acl.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))throw new InvalidDataException("Input owner is not privileged");const FileSystemRights write=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles;foreach(FileSystemAccessRule r in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(r.AccessControlType==AccessControlType.Allow && r.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544") && (r.FileSystemRights&write)!=0)throw new InvalidDataException("Privileged input is user-writable");}
    public static void Save(string path,object value) {string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false),leaveOpen:true);writer.Write(PolicyJson.Serialize(value));writer.Flush();stream.Flush(true);}File.Move(temp,path,true);}
    public static void Log(string kind,object details) {lock(LogGate){string folder=Path.Combine(Root,"state","events");Directory.CreateDirectory(folder);File.AppendAllText(Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd")+".jsonl"),PolicyJson.Serialize(new {atUtc=DateTimeOffset.UtcNow,kind,details})+Environment.NewLine);}}
    public static async Task<string> Native(string mode,CancellationToken ct=default) {using var p=new Process{StartInfo=new ProcessStartInfo(Path.Combine(Root,"BetaBridge.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};p.StartInfo.ArgumentList.Add(mode);p.Start();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(45000);var output=p.StandardOutput.ReadToEndAsync(timeout.Token);var error=p.StandardError.ReadToEndAsync(timeout.Token);try{await p.WaitForExitAsync(timeout.Token);string text=await output,err=await error;if(p.ExitCode!=0)throw new InvalidOperationException(err);return text;}finally{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}}}
}
internal sealed class SystemClock(string boot):IClock {
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool QueryUnbiasedInterruptTime(out ulong ticks);
    public string BootId=>boot;public long Uptime=>Environment.TickCount64;public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow;
    public long AwakeMilliseconds=>QueryUnbiasedInterruptTime(out ulong ticks)?checked((long)(ticks/10000)):Environment.TickCount64;
    long checkedAt=-10000;bool present=true;
    public bool UserSessionPresent {get {if(Uptime-checkedAt<2000)return present;checkedAt=Uptime;present=SessionPresence.ManagedUserActive();return present;}}
}
