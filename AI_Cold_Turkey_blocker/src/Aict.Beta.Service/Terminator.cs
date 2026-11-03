using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Aict.Core;
using Microsoft.Win32.SafeHandles;

namespace Aict.Beta.Service;

// Acts only on a held OS process handle, after matching the managed SID and PE SIP identity.
// File inspection impersonates that process's user; no SYSTEM file access is driven by a UI path.
internal sealed class Terminator(string managedSid,IEnumerable<string> protectedIds) {
    readonly HashSet<string> essentials=protectedIds.ToHashSet(StringComparer.Ordinal);
    readonly Dictionary<string,(long Size,DateTime Modified,string Id)> cache=new(StringComparer.OrdinalIgnoreCase);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder name,ref uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool TerminateProcess(SafeProcessHandle process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
    public Task EnforceAsync(AppPlan plan,CancellationToken cancellation) {
        var denied=plan.Denied.Select(a=>a.Id).ToImmutableArray();
        if(denied.IsEmpty && plan.Whitelists.IsEmpty) return Task.CompletedTask;
        foreach(var item in Process.GetProcesses()) using(item) {
            cancellation.ThrowIfCancellationRequested();
            bool identified=false;
            try {
                if(item.Id==Environment.ProcessId || item.SessionId==0) continue;
                using var process=OpenProcess(0x1000|1|0x100000,false,item.Id);if(process.IsInvalid) continue;
                if(!OpenProcessToken(process,0xA,out var token)) continue;
                using(token) {
                    using var identity=new WindowsIdentity(token.DangerousGetHandle());if(identity.User?.Value!=managedSid) continue;
                    var name=new StringBuilder(32768);uint length=(uint)name.Capacity;if(!QueryFullProcessImageNameW(process,0,name,ref length)) continue;
                    string path=name.ToString();
                    if(!Path.IsPathFullyQualified(path) || path.StartsWith("\\",StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed) continue;
                    string? id=WindowsIdentity.RunImpersonated(token,()=>{
                        var file=new FileInfo(path);if(!file.Exists || (file.Attributes&FileAttributes.ReparsePoint)!=0) return null;
                        if(cache.TryGetValue(path,out var previous) && previous.Size==file.Length && previous.Modified==file.LastWriteTimeUtc) return previous.Id;
                        string value=ApplicationTarget.Prefix+Aict.Desktop.AuthenticodeHash.Compute(path)[2..];
                        if(cache.Count>256) cache.Clear();cache[path]=(file.Length,file.LastWriteTimeUtc,value);return value;
                    });
                    if(id is null || essentials.Contains(id))continue;
                    bool essentialPath=path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || path.StartsWith(Env.Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
                    bool blocked=denied.Contains(id) || (!essentialPath && plan.Whitelists.Any(g=>!g.Applications.Any(a=>a.Id==id)));
                    if(!blocked)continue;
                    identified=true;
                    bool terminationRequested=TerminateProcess(process,0xA1C7);int error=terminationRequested?0:Marshal.GetLastWin32Error();bool processExitObserved=WaitForSingleObject(process,1000)==0;
                    Env.Log("ProcessTerminated",new {applicationId=id,processId=item.Id,terminationRequested,processExitObserved,nativeErrorCode=error});
                    if(!processExitObserved) throw new InvalidOperationException("已命中的应用进程未确认退出，错误码 "+error);
                }
            } catch(Exception e) when(!identified && e is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException) { /* A gone/unreadable process is not identified by its name and is not killed. */ }
        }
        return Task.CompletedTask;
    }
}
