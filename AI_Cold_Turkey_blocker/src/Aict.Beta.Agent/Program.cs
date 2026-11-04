using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using Aict.Beta;
using Aict.Contracts;
using Aict.Core;
using Microsoft.Win32;

try {if(args.SequenceEqual(new[]{"--build-info"})){Console.WriteLine(PolicyJson.Serialize(new{hostTrial=BuildMode.HostTrial,trialMinutes=BuildMode.TrialMinutes}));return 0;}return await Agent.Run(args);}catch(Exception e){Console.Error.WriteLine(e.Message);Agent.TryLog(e.Message);return 1;}

static class Agent {
    static string Root=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    static string runId="";static string Browser="";static bool userValidated;
    internal static void TryLog(string message){if(!userValidated)return;try{string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AICT");Directory.CreateDirectory(folder);string file=Path.Combine(folder,"beta-agent-"+runId+".log");if(!File.Exists(file) || new FileInfo(file).Length<1048576)File.AppendAllText(file,DateTimeOffset.UtcNow.ToString("o")+" "+message+Environment.NewLine);}catch{}}

    static async Task<Response> Call(Request request){using var timeout=new CancellationTokenSource(3500);using var pipe=new NamedPipeClientStream(".","AICT-beta-"+runId,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);await pipe.ConnectAsync(timeout.Token);Aict.Service.ServicePeer.Verify(pipe,"AICT-Beta-"+runId);await PipeFrames.WriteAsync(pipe,PolicyJson.Serialize(request),PipeFrames.MaxRequestBytes,timeout.Token);return PolicyJson.Parse<Response>(await PipeFrames.ReadAsync(pipe,PipeFrames.MaxResponseBytes,timeout.Token));}
    internal static async Task<int> Run(string[] args){
        using(var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))if(!BuildMode.HostTrial && (Convert.ToString(bios?.GetValue("SystemManufacturer"))?.Contains("VMware")!=true || Convert.ToString(bios?.GetValue("SystemProductName"))?.Contains("VMware")!=true))throw new InvalidOperationException("VMware guest required; no changes performed.");
        string folder=Path.GetFileName(Root);if(!folder.StartsWith("AICT-Beta-",StringComparison.Ordinal) || !Guid.TryParseExact(folder[10..],"N",out _))throw new InvalidDataException("Protected beta root required");runId=folder[10..];
        var config=PolicyJson.Parse<System.Text.Json.JsonElement>(File.ReadAllText(Path.Combine(Root,"config.json")));if(config.GetProperty("runId").GetString()!=runId || config.GetProperty("probeSid").GetString()!=WindowsIdentity.GetCurrent().User?.Value)throw new UnauthorizedAccessException("Agent user mismatch");if(BuildMode.HostTrial)HostInstallation.Validate(config.GetProperty("hostTrial").GetBoolean(),config.TryGetProperty("continuousUse",out var continuous)&&continuous.GetBoolean(),config.GetProperty("hostTrialEndsUtc").ValueKind==System.Text.Json.JsonValueKind.Null?null:config.GetProperty("hostTrialEndsUtc").GetDateTimeOffset());userValidated=true;
        if(args.SequenceEqual(new[]{"--session-agent"})) {using var mutex=new Mutex(false,"Local\\AICT-Agent-"+runId);bool held;try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}if(!held)return 0;try{await Session();}finally{mutex.ReleaseMutex();}return 0;}
        string id=File.ReadAllText(Path.Combine(Root,"Extension","extension-id.txt")).Trim();if(!args.Contains("chrome-extension://"+id+"/"))throw new UnauthorizedAccessException("Only the installed extension may open native messaging");
        Browser=ParentBrowser();if(Browser.Length==0)throw new UnauthorizedAccessException("Unsupported native host parent process");
        using var input=Console.OpenStandardInput();using var output=Console.OpenStandardOutput();
        while(true){string json;try{json=await PipeFrames.ReadAsync(input,256*1024);}catch(EndOfStreamException){return 0;}var sample=PolicyJson.Parse<BrowserSample>(json) with {Browser=Browser};Response response;
            try{response=await Call(new(){Command=Command.BrowserPulse,Browser=sample});}catch{string file=Path.Combine(Root,"state","cleanup-result.json");if(!File.Exists(file))throw;var receipt=PolicyJson.Parse<System.Text.Json.JsonElement>(File.ReadAllText(file));if(!receipt.GetProperty("completed").GetBoolean())throw;response=new(true,"UNINSTALLED","卸载已经完成",BrowserPlan:new([],false));}
            await PipeFrames.WriteAsync(output,PolicyJson.Serialize(response),1024*1024);}
    }
    [StructLayout(LayoutKind.Sequential)] struct LastInput {public uint Size,Tick;}
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint access);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetUserObjectInformationW(IntPtr obj,int index,StringBuilder value,int size,out int needed);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageNameW(IntPtr process,uint flags,StringBuilder name,ref uint size);
    static string? ImagePath(uint pid){IntPtr handle=OpenProcess(0x1000,false,pid);if(handle==IntPtr.Zero)return null;try{var name=new StringBuilder(32768);uint size=(uint)name.Capacity;return QueryFullProcessImageNameW(handle,0,name,ref size)?name.ToString():null;}finally{CloseHandle(handle);}}
    static readonly Dictionary<string,(long Size,DateTime Modified,string Id)> Cache=new(StringComparer.OrdinalIgnoreCase);
    static async Task Session(){while(true){try{var input=new LastInput {Size=(uint)Marshal.SizeOf<LastInput>()};if(!GetLastInputInfo(ref input))throw new InvalidOperationException("Idle state unavailable");double idle=unchecked((uint)Environment.TickCount-input.Tick)/1000.0;bool locked=true;IntPtr desktop=OpenInputDesktop(0,false,1);if(desktop!=IntPtr.Zero){try{var name=new StringBuilder(256);locked=!GetUserObjectInformationW(desktop,2,name,name.Capacity*2,out _) || !name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase);}finally{CloseDesktop(desktop);}}
                string? id=null;string? foregroundBrowser=null;string? applicationName=null;GetWindowThreadProcessId(GetForegroundWindow(),out uint pid);if(pid>0)try{string? path=ImagePath(pid);applicationName=Path.GetFileName(path);if(applicationName?.Length>200)applicationName=applicationName[..200];foregroundBrowser=Path.GetFileNameWithoutExtension(path)?.ToLowerInvariant() switch {"msedge"=>"Edge","chrome"=>"Chrome",_=>null};if(path is not null){var f=new FileInfo(path);if(f.Exists){if(Cache.TryGetValue(path,out var previous) && previous.Size==f.Length && previous.Modified==f.LastWriteTimeUtc)id=previous.Id;else{id=ApplicationTarget.Prefix+Aict.Desktop.AuthenticodeHash.Compute(path)[2..];if(Cache.Count>256)Cache.Clear();Cache[path]=(f.Length,f.LastWriteTimeUtc,id);}}}}catch(Exception e)when(e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException){}
                await Call(new(){Command=Command.AgentPulse,Agent=new(id,locked,idle,foregroundBrowser,applicationName)});
            }catch(Exception e){Console.Error.WriteLine(e.Message);TryLog(e.Message);}await Task.Delay(1000);}}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ProcessEntry {public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;}
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool Process32FirstW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool Process32NextW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    static string ParentBrowser(){IntPtr snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return "";var parents=new Dictionary<uint,(uint Parent,string Name)>();try{var e=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),Name=""};if(Process32FirstW(snapshot,ref e))do{parents[e.Pid]=(e.Parent,e.Name);}while(Process32NextW(snapshot,ref e));}finally{CloseHandle(snapshot);}uint pid=(uint)Environment.ProcessId;for(int i=0;i<4 && parents.TryGetValue(pid,out var p);i++){pid=p.Parent;if(parents.TryGetValue(pid,out var parent)){if(parent.Name.Equals("msedge.exe",StringComparison.OrdinalIgnoreCase))return "Edge";if(parent.Name.Equals("chrome.exe",StringComparison.OrdinalIgnoreCase))return "Chrome";}}return "";}
}
