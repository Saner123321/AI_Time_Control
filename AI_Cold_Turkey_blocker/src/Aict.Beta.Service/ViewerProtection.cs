using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Aict.Beta.Service;
internal static class ViewerProtection {
    internal static bool Peer(uint pid,out bool protectedProcess)=>Aict.ProcessGuard.ViewerProcessSecurity.Protect(pid,Path.Combine(Env.Root,@"Viewer\Aict.Beta.Viewer.exe"),Env.Config.ProbeSid,out protectedProcess);
    internal static void StopOwned(){foreach(var p in Process.GetProcessesByName("Aict.Beta.Viewer"))using(p)try{if(string.Equals(p.MainModule?.FileName,Path.Combine(Env.Root,@"Viewer\Aict.Beta.Viewer.exe"),StringComparison.OrdinalIgnoreCase)){p.Kill();p.WaitForExit(5000);}}catch(InvalidOperationException){} }
}
