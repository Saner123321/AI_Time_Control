using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using Aict.Beta;
using Aict.Core;
internal static class ViewerSelfTest {
    [DllImport("user32.dll")] static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr first,IntPtr second);
    static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);}
    internal static string FixtureJson(){Application.EnableVisualStyles();using var fixture=new Fixture();fixture.Show();Application.DoEvents();return PolicyJson.Serialize(ManualBrowserProbe.ReadWindow(fixture.Handle,true,true));}
    internal static void Run(string folder){
        Directory.CreateDirectory(folder);Application.EnableVisualStyles();var cases=new List<string>();Check(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(),new IntPtr(-4)),"DPI coordinate mode");cases.Add("per_monitor_v2_coordinates");
        var group=new RuleGroup{Id=Guid.NewGuid(),Name="夹具规则",WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["example.invalid"],WebsiteExceptions=["example.invalid/lesson"],AlwaysBlock=true};var policy=new PolicyConfiguration{Groups=[group]};
        var active=new WebPlan([new(group.Id,group.WebsiteMode,group.WebsiteDomains,group.WebsiteExceptions,[BlockReason.Always],group.Name,["按规则持续封锁"])],true);
        var p=ManualViewingRules.Decide(active,policy,["https://example.invalid/watch","https://example.invalid/lesson"]);Check(p.Pages[0].Denied&&!p.Pages[1].Denied,"rules and exceptions");cases.Add("shared_rules_and_exceptions");
        IntPtr foreground=ManualBrowserProbe.GetForegroundWindow();using(var mask=new ViewMask()){
            var box=new Rectangle(-30000,-30000,600,300);mask.Message("当前网页已受限制",p.Pages[0].Explanation);mask.Place(box);Application.DoEvents();
            for(int i=0;i<100;i++)mask.Place(box);Check(mask.CaptureExcluded&&mask.InputPassThrough&&mask.ConfigurationWrites==1&&mask.ShowTransitions==1,"stable native mask");cases.Add("stable_native_mask_capture_and_passthrough");
            mask.Hide();Check(foreground==ManualBrowserProbe.GetForegroundWindow(),"focus changed");cases.Add("does_not_activate_user_window");
        }
        using(var fixture=new Fixture()){
            fixture.Show();Application.DoEvents();var sample=ManualBrowserProbe.ReadWindow(fixture.Handle,true,true);Check(sample.State=="committed_document_urls_observed"&&sample.Pages.Count==1,"nested document probe");cases.Add("actual_nested_document_probe");
            var lease=new ManualViewConfirmation();lease.Observe(sample,0);string reason;var owner=new Rectangle(sample.OwnerX,sample.OwnerY,sample.OwnerWidth,sample.OwnerHeight);Check(lease.Select(2000,sample.Window,owner,out reason)!=null&&lease.Select(2501,sample.Window,owner,out reason)==null,"lease bounds");cases.Add("bounded_foreground_confirmation");fixture.Hide();
        }
        var roundtrip=System.Threading.Tasks.Task.Run(()=>ViewerContext.Probe(System.Threading.CancellationToken.None,"--fixture-probe")).GetAwaiter().GetResult();Check(roundtrip.State=="committed_document_urls_observed"&&roundtrip.Pages.Count==1,"isolated JSON worker roundtrip");cases.Add("isolated_native_worker_json_roundtrip");
        using(var executable=File.OpenRead(Environment.ProcessPath))using(var image=new System.Reflection.PortableExecutable.PEReader(executable)){Check(image.PEHeaders.PEHeader.Subsystem==System.Reflection.PortableExecutable.Subsystem.WindowsCui,"background executable must not trigger GUI startup feedback");cases.Add("background_console_subsystem_no_gui_startup_feedback");}
        System.Threading.Tasks.Task.Run(async()=>{
            using var worker=new BrowserProbeWorker("--fixture-worker");
            for(int i=0;i<6;i++){var value=await worker.Read(System.Threading.CancellationToken.None);Check(value.State=="committed_document_urls_observed"&&value.Pages.Count==1,"persistent worker response");}
            Check(worker.Starts==1,"healthy observations must reuse one process");cases.Add("six_observations_reuse_one_worker");
            int first=worker.WorkerId;var timer=Stopwatch.StartNew();var failed=await worker.Read(System.Threading.CancellationToken.None,"hang");Check(failed.State=="probe_timeout"&&timer.ElapsedMilliseconds<5000&&worker.WorkerId==0,"hung provider timeout");cases.Add("hung_worker_bounded_and_reaped");
            var recovered=await worker.Read(System.Threading.CancellationToken.None);Check(recovered.State=="committed_document_urls_observed"&&worker.Starts==2&&worker.WorkerId!=first,"timeout recovery");cases.Add("worker_recovers_after_timeout");
            failed=await worker.Read(System.Threading.CancellationToken.None,"crash");Check(failed.State=="probe_failed"&&worker.WorkerId==0,"crashed worker");recovered=await worker.Read(System.Threading.CancellationToken.None);Check(recovered.State=="committed_document_urls_observed"&&worker.Starts==3,"crash recovery");cases.Add("worker_recovers_after_crash");
            using var owned=Process.GetProcessById(worker.WorkerId);worker.Dispose();Check(owned.WaitForExit(2000),"worker not disposed");cases.Add("owned_worker_removed_on_shutdown");
        }).GetAwaiter().GetResult();
        bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using(var child=new Process{StartInfo=new ProcessStartInfo(Environment.ProcessPath,"--security-fixture-wait"){UseShellExecute=false,CreateNoWindow=true}}){
            child.Start();IntPtr retained=child.Handle;try{
                bool authorized=Aict.ProcessGuard.ViewerProcessSecurity.Protect((uint)child.Id,Environment.ProcessPath,WindowsIdentity.GetCurrent().User.Value,out bool protectedProcess);Check(authorized&&protectedProcess,"own fixture process security");cases.Add("own_process_dacl_verified");
                var attempt=Aict.ProcessGuard.ViewerProcessSecurity.OpenProcess(1,false,(uint)child.Id);try{if(!admin)Check(attempt==IntPtr.Zero,"ordinary fresh termination open unexpectedly allowed");}finally{if(attempt!=IntPtr.Zero)Aict.ProcessGuard.ViewerProcessSecurity.CloseHandle(attempt);}cases.Add(admin?"elevated_caller_admin_boundary_preserved":"ordinary_fresh_terminate_open_denied");
                Check(!Aict.ProcessGuard.ViewerProcessSecurity.Protect((uint)child.Id,Path.Combine(folder,"wrong.exe"),WindowsIdentity.GetCurrent().User.Value,out _),"wrong image accepted");cases.Add("security_scope_rejects_other_executable");
            }finally{if(!child.HasExited){child.Kill();child.WaitForExit(5000);}}
        }
        File.WriteAllText(Path.Combine(folder,"result.json"),PolicyJson.Serialize(new{passed=true,cases=cases.Count,checks=cases,elevatedCaller=admin,ordinaryCallerTerminationDenialTested=!admin,systemWrites=false,realClientOrServiceChanges=false}));
    }
    sealed class Fixture:Form {protected override bool ShowWithoutActivation=>true;internal Fixture(){StartPosition=FormStartPosition.Manual;Bounds=new Rectangle(-30000,-30000,700,500);ShowInTaskbar=false;var shell=new Document("app://fixture"){Bounds=new Rectangle(10,30,650,400)};shell.Controls.Add(new Document("https://example.invalid/watch"){Bounds=new Rectangle(10,10,620,350)});Controls.Add(shell);}}
    sealed class Document:Panel {readonly string url;internal Document(string url){this.url=url;}protected override AccessibleObject CreateAccessibilityInstance()=>new Accessible(this);sealed class Accessible:ControlAccessibleObject {internal Accessible(Document owner):base(owner){}public override AccessibleRole Role=>AccessibleRole.Document;public override AccessibleStates State=>AccessibleStates.None;public override string Value{get=>((Document)Owner).url;set{}}}}
}
