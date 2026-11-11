using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ManualViewLab {
    internal static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=1048576,RecursionLimit=25};}
    internal static bool CanMask(BrowserProbeResult r,string domain){return r!=null&&r.State=="committed_document_urls_observed"&&!r.Truncated&&r.DurationMs<=1200&&r.Pages.Any(p=>ManualBrowserProbe.Target(p.Url,domain));}
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [STAThread] static int Main(string[] args){
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        if(args.SequenceEqual(new[]{"--probe"})){
            // A stuck accessibility provider must not leave an orphaned helper.
            var watchdog=new System.Threading.Thread(()=>{System.Threading.Thread.Sleep(1900);Environment.Exit(3);});watchdog.IsBackground=true;watchdog.Start();
            try{Console.Write(Json().Serialize(ManualBrowserProbe.ReadForeground()));return 0;}catch{return 2;}
        }
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length==2&&args[0]=="--render"){IntPtr before=ManualBrowserProbe.GetForegroundWindow();using(var form=new LabForm(true)){form.Show();Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);}form.Hide();Assert(before==ManualBrowserProbe.GetForegroundWindow(),"Layout render activated a window");return 0;}}
            if(args.SequenceEqual(new[]{"--fixture-probe"})){using(var fixture=new Fixture()){fixture.Show();Application.DoEvents();Console.Write(Json().Serialize(ManualBrowserProbe.ReadWindow(fixture.Handle,true)));fixture.Hide();return 0;}}
            if(args.Length==2&&args[0]=="--self-test"){SelfTest(args[1]);return 0;}
            Application.Run(new LabForm());return 0;
        }catch(Exception e){if(args.Length>0){File.WriteAllText(Path.Combine(args.Last(),"error.txt"),e.ToString());return 1;}MessageBox.Show(e.Message,"内置浏览器验证");return 1;}
    }
    sealed class LabForm:Form {
        readonly bool offscreen;
        protected override bool ShowWithoutActivation {get{return offscreen;}}
        readonly TextBox target=new TextBox{Text="bilibili.com",Width=320};
        readonly Label status=new Label{Width=690,Height=110,Text="点击开始检测后，手动切换到 ChatGPT，打开你要验证的视频页。"};
        readonly Label badge=new Label{Width=690,Height=60};
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=100};
        readonly Stopwatch uptime=Stopwatch.StartNew();readonly List<object> evidence=new List<object>();readonly List<ViewMask> masks=new List<ViewMask>();
        readonly ManualViewConfirmation confirmation=new ManualViewConfirmation();string maskSignature="";
        ManualViewFixtureServer fixtureServer;
        BrowserProbeResult last;long receivedAt=-10000,nextProbe,maskEnd;bool running,busy,closing;string domain="bilibili.com",signature="",recentDomains="尚无";
        long manualWindow;Rectangle manualRegion,manualOwner;
        readonly Button arm=new Button{Text="开始60秒遮挡验证",Width=210,Height=38};
        internal LabForm(bool offscreen=false){
            this.offscreen=offscreen;
            Text="专注计划 · 内置浏览器验证 v3";Width=770;Height=Math.Min(790,Screen.PrimaryScreen.WorkingArea.Height-70);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);
            if(offscreen){StartPosition=FormStartPosition.Manual;Location=new Point(-30000,-30000);ShowInTaskbar=false;}
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("AICT.FocusIcon"))if(stream!=null)using(var icon=new Icon(stream))Icon=(Icon)icon.Clone();
            var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(22)};Controls.Add(panel);
            panel.Controls.Add(new Label{Width=690,Height=70,Text="本工具只验证人工观看限制，不连接正式封锁服务、不修改 ChatGPT 设置。\r\n默认只读；遮挡必须手动开启，60秒后自动结束，网页仍保持运行。"});
            panel.Controls.Add(new Label{Text="验证目标域名（不是正式规则，也不会读取你的例外）",AutoSize=true});panel.Controls.Add(target);
            var fixtureButton=new Button{Text="复制本地验证页网址",Width=230,Height=38};panel.Controls.Add(fixtureButton);
            fixtureButton.Click+=(s,e)=>{try{if(fixtureServer==null)fixtureServer=new ManualViewFixtureServer();Stop();target.Text="127.0.0.1";Clipboard.SetText(fixtureServer.Url);status.Text="已复制本地网址，请手动粘贴到ChatGPT内置浏览器。随后回到本工具开启检测/遮挡。\r\n不登录外部网站，可以让AI读取标题并点击“增加测试计数”。";}catch(Exception error){MessageBox.Show(this,error.Message,"本地验证页未启动");}};
            var row=new FlowLayoutPanel{Width=700,Height=48};panel.Controls.Add(row);var start=new Button{Text="开始只读检测",Width=170,Height=38};var stop=new Button{Text="停止并撤掉遮挡",Width=190,Height=38};row.Controls.Add(start);row.Controls.Add(arm);row.Controls.Add(stop);
            var manual=new Button{Text="手动框选：仅验证遮挡与AI操作",Width=360,Height=38};panel.Controls.Add(manual);manual.Click+=(s,e)=>SelectManualRegion();
            panel.Controls.Add(status);panel.Controls.Add(badge);
            var export=new Button{Text="导出验证结果",Width=210,Height=38};panel.Controls.Add(export);
            panel.Controls.Add(new Label{Width=690,Height=70,Text="请验证：你是否看不到视频；AI能否继续读取、截图和点击该页。\r\n截图排除与点击穿透已请求，但真实AI效果仍须这一步确认；音频、画中画和全屏本轮不保证。"});
            start.Click+=(s,e)=>{try{Stop();domain=ManualBrowserProbe.Domain(target.Text);target.ReadOnly=true;running=true;nextProbe=0;status.Text="检测已开启，请切换到 ChatGPT 并显示视频页。";}catch(Exception error){MessageBox.Show(this,error.Message,"域名无效");}};
            arm.Click+=(s,e)=>{try{manualWindow=0;domain=ManualBrowserProbe.Domain(target.Text);target.ReadOnly=true;running=true;nextProbe=0;maskEnd=uptime.ElapsedMilliseconds+60000;badge.Text="自动验证已准备。切到 ChatGPT 的目标页面后，识别成功才显示遮挡。";}catch(Exception error){MessageBox.Show(this,error.Message,"域名无效");}};
            stop.Click+=(s,e)=>Stop();export.Click+=(s,e)=>Export();timer.Tick+=async(s,e)=>await Tick();timer.Start();
            FormClosing+=(s,e)=>{closing=true;timer.Stop();Stop();foreach(var mask in masks)mask.Dispose();if(fixtureServer!=null)fixtureServer.Dispose();};
        }
        void Stop(){running=false;maskEnd=0;manualWindow=0;confirmation.Clear();target.ReadOnly=false;foreach(var mask in masks)mask.Hide();TraceMask("stopped");badge.Text="已停止，所有验证遮挡已撤掉。";}
        void TraceMask(string reason){
            bool visible=masks.Any(m=>m.Visible);string signature=visible+"|"+reason+"|"+(manualWindow!=0);if(signature==maskSignature)return;maskSignature=signature;
            if(evidence.Count<1000)evidence.Add(new{eventType="mask_transition",atUtc=DateTimeOffset.UtcNow.ToString("o"),reason,maskVisible=visible,manualRegionTest=manualWindow!=0,configurationWrites=masks.Sum(m=>m.ConfigurationWrites),geometryWrites=masks.Sum(m=>m.GeometryWrites),showTransitions=masks.Sum(m=>m.ShowTransitions),hideTransitions=masks.Sum(m=>m.HideTransitions),paintCalls=masks.Sum(m=>m.PaintCalls)});
        }
        void SelectManualRegion(){
            Stop();Hide();Rectangle selected=Rectangle.Empty;
            try{using(var pick=new RegionSelectionDialog()){if(pick.ShowDialog()==DialogResult.OK)selected=pick.Selected;}
                if(selected.IsEmpty)return;IntPtr window=ManualBrowserProbe.GetAncestor(ManualBrowserProbe.WindowFromPoint(new Point(selected.Left+selected.Width/2,selected.Top+selected.Height/2)),2);uint pid;ManualBrowserProbe.GetWindowThreadProcessId(window,out pid);ManualBrowserProbe.Rect owner;
                if(!ManualBrowserProbe.KnownClient(ManualBrowserProbe.ProcessImage(pid))||!ManualBrowserProbe.GetWindowRect(window,out owner)||!owner.Box.Contains(selected))throw new InvalidOperationException("框选区域必须完全位于ChatGPT窗口内；请只选择网页，不要框聊天栏。");
                manualWindow=window.ToInt64();manualRegion=selected;manualOwner=owner.Box;running=true;nextProbe=0;maskEnd=uptime.ElapsedMilliseconds+60000;
            }catch(Exception error){MessageBox.Show(error.Message,"未开始手动验证");}
            finally{Show();if(manualWindow!=0)badge.Text="手动区域验证已准备，请切回ChatGPT。此模式不代表自动识别成功。";}
        }
        async Task Tick(){
            if(closing)return;long now=uptime.ElapsedMilliseconds;
            if(maskEnd>0&&now>=maskEnd){maskEnd=0;manualWindow=0;foreach(var mask in masks)mask.Hide();badge.Text="60秒验证已结束，已撤掉遮挡。";}
            IntPtr foreground=ManualBrowserProbe.GetAncestor(ManualBrowserProbe.GetForegroundWindow(),2);ManualBrowserProbe.Rect rect;
            Rectangle owner=ManualBrowserProbe.GetWindowRect(foreground,out rect)?rect.Box:Rectangle.Empty;string reason;
            var confirmed=confirmation.Select(now,foreground.ToInt64(),owner,out reason);
            bool manualReady=manualWindow!=0&&foreground.ToInt64()==manualWindow&&owner==manualOwner;
            if(maskEnd>now&&(manualReady||manualWindow==0&&confirmed!=null&&CanMask(confirmed,domain))){
                var areas=manualReady?new[]{manualRegion}:confirmed.Pages.Where(p=>ManualBrowserProbe.Target(p.Url,domain)).Select(p=>p.Rectangle).ToArray();
                reason=manualReady?"manual_ready":last!=null&&last.State=="committed_document_urls_observed"?"confirmed_document":"confirmed_document_short_lease";
                try{while(masks.Count<areas.Length)masks.Add(new ViewMask());for(int i=0;i<masks.Count;i++){if(i<areas.Length)masks[i].Place(areas[i]);else masks[i].Hide();}badge.Text=(manualReady?"手动框选验证（未确认网址）":"自动识别遮挡验证")+" · 截图排除已读回 · 点击穿透已设置 · 剩余 "+((maskEnd-now+999)/1000)+" 秒";}
                catch(Exception e){maskEnd=0;foreach(var mask in masks)mask.Hide();reason="display_configuration_failed";badge.Text=e.Message;}
            }else foreach(var mask in masks)mask.Hide();
            if(reason!="display_configuration_failed"){if(!running)reason="stopped";else if(maskEnd<=now)reason="not_armed";else if(manualWindow!=0&&!manualReady)reason="manual_owner_not_current";else if(confirmed!=null&&!CanMask(confirmed,domain))reason="current_page_allowed";}
            TraceMask(reason);
            if(!running||busy||now<nextProbe)return;busy=true;nextProbe=now+600;
            try{
                var result=await Task.Run(()=>ReadChild());if(closing)return;last=result;receivedAt=uptime.ElapsedMilliseconds;confirmation.Observe(result,receivedAt);
                string[] hosts=last.Pages.Select(p=>new Uri(p.Url).IdnHost).Distinct().ToArray();
                if(last.State=="committed_document_urls_observed")recentDomains=String.Join("、",hosts);
                status.Text=last.State=="other_application_foreground"?"当前不是 ChatGPT 前台，未计时、未遮挡。\r\n最近一次已识别的域名："+recentDomains:last.State=="committed_document_urls_observed"?"已读取当前可见网页的已提交地址。域名："+String.Join("、",hosts)+"\r\n命中验证域名："+(CanMask(last,domain)?"是":"否")+"；这不是正式规则的封锁结论。":"尚未可靠识别浏览区域："+Reason(last.State)+"\r\n不会用标题或地址栏猜测，也不会直接封锁整个ChatGPT。";
                string sig=last.State+"|"+String.Join("|",hosts)+"|"+masks.Any(m=>m.Visible)+"|"+last.EmptyDocumentValues+"|"+last.NonWebDocumentValues+"|"+last.HiddenDocuments+"|"+last.RejectedBounds;if(sig!=signature){signature=sig;if(evidence.Count<500)evidence.Add(new{atUtc=DateTimeOffset.UtcNow.ToString("o"),state=last.State,domains=hosts,durationMs=last.DurationMs,documents=last.DocumentsExamined,truncated=last.Truncated,emptyDocumentValues=last.EmptyDocumentValues,nonWebDocumentValues=last.NonWebDocumentValues,hiddenDocuments=last.HiddenDocuments,rejectedBounds=last.RejectedBounds,documentReadErrors=last.DocumentReadErrors,candidateWindows=last.CandidateWindows,visibleBounds=last.Pages.Select(p=>new{p.X,p.Y,p.Width,p.Height}).ToArray(),maskVisible=masks.Any(m=>m.Visible),manualRegionTest=manualWindow!=0});}
            }finally{busy=false;}
        }
        static string Reason(string code){switch(code){case "probe_timeout":return "读取超时，已结束自有检测进程";case "committed_document_url_unavailable":return "客户端没有提供可确认的网页地址";case "ambiguous_browser_rectangles":return "多个网页区域重叠，无法确认";case "probe_budget_exceeded":return "读取信息过多，已停止本次读取";case "client_not_visible":return "客户端窗口未显示";case "unrecognized_client":return "尚未适配此客户端安装方式";default:return "本次读取未完成，请导出验证结果";}}
        internal static BrowserProbeResult ReadChild(string mode="--probe"){
            using(var child=new Process{StartInfo=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,mode){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}}){
                try{child.Start();var read=child.StandardOutput.ReadToEndAsync();if(!child.WaitForExit(1700)){try{child.Kill();}catch{}return new BrowserProbeResult{State="probe_timeout"};}if(child.ExitCode!=0)return new BrowserProbeResult{State="probe_failed"};if(!read.Wait(200))return new BrowserProbeResult{State="probe_output_incomplete"};return Json().Deserialize<BrowserProbeResult>(read.Result);}catch{return new BrowserProbeResult{State="probe_unavailable"};}
            }
        }
        void Export(){try{string folder=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"reports","manual-view-"+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"result.json"),Json().Serialize(new{kind="AICT_MANUAL_VIEWING_LAB_V3",productionIntegrated=false,productionRulesModified=false,chatgptSettingsModified=false,networkOrAppRestrictionsAdded=false,urlsTitlesQueriesOrScreenshotsRecorded=false,humanCaptureAndAiInteractionVerified=false,manualRegionDoesNotConfirmAutomaticDetection=true,maskStatistics=masks.Select(m=>new{m.PlaceCalls,m.ConfigurationWrites,m.GeometryWrites,m.ShowTransitions,m.HideTransitions,m.PaintCalls}).ToArray(),observations=evidence}));using(var dialog=new ReportPathDialog(folder)){dialog.ShowDialog(this);if(dialog.Copied)badge.Text="报告文件夹路径已复制，可以直接粘贴到聊天。";}}catch(Exception e){MessageBox.Show(this,e.Message,"导出未完成");}}
    }
    sealed class Fixture:Form {protected override bool ShowWithoutActivation{get{return true;}}internal Fixture(bool nested=false,bool empty=false){StartPosition=FormStartPosition.Manual;Bounds=new Rectangle(-30000,-30000,700,500);ShowInTaskbar=false;var address=new TextBox{Text="https://allowed.invalid/",AccessibleName="Browser address",Bounds=new Rectangle(10,10,650,30)};Controls.Add(address);var document=new DocumentFixture(nested?(empty?"":"app://client/shell"):"https://video.example.invalid/watch"){Bounds=new Rectangle(10,65,650,350)};if(nested)document.Controls.Add(new DocumentFixture{Bounds=new Rectangle(10,10,620,320)});Controls.Add(document);}}
    sealed class DocumentFixture:Panel {internal readonly string CommittedUrl;internal DocumentFixture(string url="https://video.example.invalid/watch"){CommittedUrl=url;}protected override AccessibleObject CreateAccessibilityInstance(){return new DocumentAccessibility(this);}sealed class DocumentAccessibility:ControlAccessibleObject {internal DocumentAccessibility(Control owner):base(owner){}public override AccessibleRole Role{get{return AccessibleRole.Document;}}public override AccessibleStates State{get{return AccessibleStates.None;}}public override string Value{get{return ((DocumentFixture)Owner).CommittedUrl;}set{}}}}
    static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static void SelfTest(string folder){
        Directory.CreateDirectory(folder);var checks=new List<string>();IntPtr before=ManualBrowserProbe.GetForegroundWindow();
        Assert(ManualBrowserProbe.Domain("bilibili.com")=="bilibili.com","Domain parse");Assert(ManualBrowserProbe.Target("https://www.bilibili.com/video/1","bilibili.com")&&!ManualBrowserProbe.Target("https://bilibili.com.evil.invalid/","bilibili.com"),"Domain boundary");checks.Add("domain_boundary");
        Assert(ManualBrowserProbe.WebUrl("https://name:secret@example.invalid/")==null&&ManualBrowserProbe.WebUrl("file:///C:/a")==null,"Reject non-web/private credentials");checks.Add("web_url_validation");
        using(var fixture=new Fixture()){
            fixture.Show();Application.DoEvents();var r=ManualBrowserProbe.ReadWindow(fixture.Handle,true);Assert(r.State=="committed_document_urls_observed"&&r.Pages.Count==1,"Own document accessibility probe: "+Json().Serialize(r));Assert(r.Pages[0].Url=="https://video.example.invalid/watch","Address-field text must not replace committed document URL");checks.Add("committed_url_not_address_field");
            Assert(CanMask(r,"example.invalid"),"Matched visible document");checks.Add("verified_document_matches");r.Truncated=true;Assert(!CanMask(r,"example.invalid"),"Truncated must not mask");r.Truncated=false;r.State="ambiguous_browser_rectangles";Assert(!CanMask(r,"example.invalid"),"Ambiguous must not mask");checks.Add("unconfirmed_never_masks");
            using(var mask=new ViewMask()){
                var bounds=new Rectangle(-30000,-29900,500,300);mask.Place(bounds);Application.DoEvents();Assert(mask.CaptureExcluded,"Display affinity readback");Assert(mask.InputPassThrough,"Non-activating transparent style");checks.Add("capture_exclusion_readback");checks.Add("input_passthrough_style");Assert(before==ManualBrowserProbe.GetForegroundWindow(),"Fixture must not steal foreground");checks.Add("no_foreground_activation");
                for(int i=0;i<100;i++)mask.Place(bounds);Application.DoEvents();Assert(mask.ConfigurationWrites==1&&mask.GeometryWrites==1&&mask.ShowTransitions==1&&mask.HideTransitions==0,"Unchanged placement must not reset composition or visibility");checks.Add("unchanged_overlay_no_native_mutation");
                bounds.Offset(10,0);mask.Place(bounds);Assert(mask.ConfigurationWrites==1&&mask.GeometryWrites==2&&mask.ShowTransitions==1,"Move should preserve capture configuration");checks.Add("overlay_move_preserves_capture_configuration");
                mask.Hide();mask.Hide();mask.Place(bounds);Assert(mask.CaptureExcluded&&mask.ConfigurationWrites==1&&mask.ShowTransitions==2&&mask.HideTransitions==1,"Hide/show should reuse the same protected native handle");checks.Add("overlay_hide_show_reuses_protection");mask.Hide();
            }
            fixture.Hide();
        }
        foreach(bool empty in new[]{false,true})using(var nested=new Fixture(true,empty)){
            nested.Show();Application.DoEvents();var r=ManualBrowserProbe.ReadWindow(nested.Handle,true,true);
            Assert(r.State=="committed_document_urls_observed"&&r.Pages.Count==1&&r.Pages[0].Url=="https://video.example.invalid/watch","Nested committed document: "+Json().Serialize(r));
            Assert(empty?r.EmptyDocumentValues>0:r.NonWebDocumentValues>0,"Redacted document rejection reason missing");checks.Add(empty?"nested_empty_application_document":"nested_non_web_application_document");nested.Hide();
        }
        var proof=new BrowserProbeResult{State="committed_document_urls_observed",Window=42,OwnerWidth=700,OwnerHeight=500,Pages=new List<VisibleBrowserPage>{new VisibleBrowserPage{Url="https://video.example.invalid/watch",X=10,Y=65,Width=650,Height=350}}};
        var lease=new ManualViewConfirmation();var owner=new Rectangle(0,0,700,500);string leaseReason;
        lease.Observe(proof,0);lease.Observe(new BrowserProbeResult{State="probe_timeout"},1400);
        Assert(lease.Select(2000,42,owner,out leaseReason)!=null&&lease.Select(2501,42,owner,out leaseReason)==null,"Temporary lookup failure must have a bounded, nonrenewing lease");checks.Add("confirmation_lease_bounded_not_poll_renewed");
        lease.Observe(proof,3000);Assert(lease.Select(3100,43,owner,out leaseReason)==null&&lease.Select(3100,42,new Rectangle(1,0,700,500),out leaseReason)==null,"Foreground and owner geometry must remain exact");checks.Add("confirmation_lease_scope_guard");
        lease.Observe(new BrowserProbeResult{State="committed_document_urls_observed",Window=42,OwnerWidth=700,OwnerHeight=500,Pages=new List<VisibleBrowserPage>{new VisibleBrowserPage{Url="https://allowed.invalid/",Width=650,Height=350}}},3200);
        Assert(!CanMask(lease.Select(3201,42,owner,out leaseReason),"example.invalid"),"A newly confirmed allowed page must release the old target immediately");checks.Add("new_allowed_page_replaces_old_target_immediately");
        lease.Observe(proof,4000);lease.Observe(new BrowserProbeResult{State="committed_document_url_unavailable",HiddenDocuments=1},4100);Assert(lease.Select(4200,42,owner,out leaseReason)==null,"Hidden browser must invalidate lease");lease.Observe(new BrowserProbeResult{State="probe_timeout"},4300);Assert(lease.Select(4400,42,owner,out leaseReason)==null,"No proof must not acquire lease");checks.Add("hidden_or_never_confirmed_page_has_no_lease");
        var ipc=LabForm.ReadChild("--fixture-probe");Assert(ipc.State=="committed_document_urls_observed"&&ipc.Pages.Count==1,"Isolated fixture IPC: "+ipc.State);checks.Add("isolated_worker_fixture_ipc");
        using(var server=new ManualViewFixtureServer())using(var client=new System.Net.WebClient()){
            string html=client.DownloadString(server.Url);Assert(html.Contains("increase")&&html.Contains("counter"),"Loopback fixture");bool refused=false;try{client.DownloadString(server.Url+"/unexpected");}catch(System.Net.WebException){refused=true;}Assert(refused,"Fixture must not serve arbitrary paths");checks.Add("loopback_fixture_static_and_scoped");
        }
        File.WriteAllText(Path.Combine(folder,"result.json"),Json().Serialize(new{passed=true,cases=checks.Count,checks,realChatgptProbeExecuted=false,realChatgptSettingsOrRulesChanged=false,realAiScreenshotAndClickVerified=false,onlyOffscreenOwnWindowsCreated=true}));
    }
}
