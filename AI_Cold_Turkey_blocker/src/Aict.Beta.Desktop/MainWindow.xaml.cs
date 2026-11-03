using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aict.Contracts;
using PreviewRequest=Aict.Beta.Request;
using PreviewResponse=Aict.Beta.Response;
using PreviewStatus=Aict.Beta.Status;
using PreviewCommand=Aict.Beta.Command;
using AuthenticodeHash=Aict.Desktop.AuthenticodeHash;
using Aict.Core;
using Microsoft.Win32;

namespace Aict.Beta.Desktop;

public partial class MainWindow : Window {
    readonly BetaConnection connection;
    readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromSeconds(1)};
    readonly SemaphoreSlim calls=new(1,1);
    readonly List<WindowEditor> windows=[];
    readonly List<QuotaSlotEditor> quotaSlots=[];bool loadingQuota;
    readonly Dictionary<string,string> appNames=new(StringComparer.Ordinal);
    ImmutableArray<WebsiteExceptionDetail> exceptionRows=[];
    PreviewStatus? latest;
    PolicyConfiguration? draftBase;
    string? draftToken;
    RuleGroup? editing;
    DateTimeOffset anchor;
    string originalTimedText="";
    bool loading,polling,editingPending;
    sealed record AppEntry(string Id,string Label) {public override string ToString()=>Label;}
    sealed record RuleEntry(RuleGroup Rule) {public override string ToString()=>Rule.Name+(Rule.Enabled?"":" · 停用");}
    public MainWindow(BetaConnection connection) {
        this.connection=connection;InitializeComponent();
        Sites.TextChanged+=(_,_)=>{if(Sites.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(Sites.Text) && SiteMode.SelectedIndex==0)SiteMode.SelectedIndex=1;};Loaded+=OnLoaded;timer.Tick+=async(_,_)=>await RefreshAsync();Closing+=(_,_)=>timer.Stop();
    }
    async void OnLoaded(object sender,RoutedEventArgs e) {
        try {
            await connection.EnsureStartedAsync();
            Title=BuildMode.HostTrial?"专注计划 · 主机短时测试":"专注计划 · 自用测试版";if(BuildMode.HostTrial)((ComboBoxItem)AppMode.Items[2]).IsEnabled=false;ModeNotice.Text=connection.Simulated?"界面自测：不修改系统、不启用真实封锁。":"后台持续执行应用与网站规则。关闭界面不解锁；网站保护启用期间请保持浏览器组件连接。";
            FooterNotice.Text="当前规则在等待期间继续运行。待生效规则到期自动切换；已保存与实际执行状态分别显示。";DemoButton.Visibility=Visibility.Visible;
            DelayNotice.Text="收紧限制立即生效；包含放宽的修改按当前等待时间提交，到期自动切换。编辑待生效内容并重新提交将重新计时。";
            string names=Path.Combine(connection.DataDirectory,"app-labels.json");
            try {if(!connection.IsService && File.Exists(names)) foreach(var pair in PolicyJson.Parse<Dictionary<string,string>>(File.ReadAllText(names))) appNames[pair.Key]=pair.Value;}
            catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException) {Feedback.Text="应用显示名称未能读取；规则身份仍从后台读取。";}
            await RefreshAsync();timer.Start();
            string[] launch=Environment.GetCommandLineArgs();int draftArg=Array.IndexOf(launch,"--website-draft");
            if(draftArg>=0 && draftArg+1<launch.Length) {
                string draftPath=Path.GetFullPath(launch[draftArg+1]);if(new FileInfo(draftPath).Length>131072)throw new ArgumentException("网站草稿过大。");
                var draft=PolicyJson.Parse<WebsiteDraft>(File.ReadAllText(draftPath));
                foreach(string item in draft.Sites.Concat(draft.Exceptions))_=WebsitePattern.Normalize(item);
                RuleName.Text=draft.Name;Sites.Text=string.Join(Environment.NewLine,draft.Sites);SiteExceptions.Text=string.Join(Environment.NewLine,draft.Exceptions);SetExceptionRows(ExceptionRowsFromDraft());SiteMode.SelectedIndex=1;
                Feedback.Text="已恢复更新前的网站列表和例外草稿，尚未提交。请核对时间条件，再点击提交。";
            }
            if(Environment.GetCommandLineArgs().Contains("--self-test")) await SelfTestAsync();
        } catch(Exception ex) {Disconnected(ex);if(Environment.GetCommandLineArgs().Contains("--self-test")) {Environment.ExitCode=1;Close();}}
    }
    async Task<PreviewResponse> CallAsync(PreviewRequest request) {
        await calls.WaitAsync();
        try {return await connection.CallAsync(request);} finally {calls.Release();}
    }
    sealed record WebsiteDraft(string Name,string[] Sites,string[] Exceptions);
    async Task RefreshAsync() {
        if(polling) return;polling=true;
        try {
            var response=await CallAsync(new());
            if(!response.Ok || response.Status is null) throw new IOException(response.Message);
            ShowStatus(response.Status);
            if(draftBase is null) StartDraft(VisiblePolicy().Groups.FirstOrDefault() ?? NewGroup());
        } catch(Exception ex) {Disconnected(ex);} finally {polling=false;}
    }
    void ShowStatus(PreviewStatus status) {
        bool changed=latest?.EditToken!=status.EditToken;bool pendingFinished=editingPending && latest?.Pending is not null && status.Pending is null;latest=status;if(pendingFinished){editingPending=false;draftBase=null;}
        BackendState.Text=status.StorageHealthy?"后台：已连接 · 持续服务":"后台：存储故障";
        UsageText.Text=status.TelemetryConnected?"用量：前台采集正常":"用量：采集失联，额度规则保守限制";
        var used=status.Usage.Where(u=>u.GroupId==editing?.Id).OrderByDescending(u=>u.QuotaWindowActive).FirstOrDefault()??status.Usage.FirstOrDefault();
        if(used is {LimitTicks:{} limit})UsageText.Text+="\n"+used.QuotaWindowLabel+"\n本周期已用 "+Duration(TimeSpan.FromTicks(used.UsedTicks))+" / "+Duration(TimeSpan.FromTicks(limit))+(used.QuotaWindowActive?"":"\n当前在额度时段外");
        if(used?.HasGap==true)UsageText.Text+="\n采集缺口：本周期额度保守限制";
        foreach(var slot in quotaSlots)slot.ShowUsage(status.Usage.FirstOrDefault(u=>u.GroupId==editing?.Id && (u.QuotaSlotId==slot.Id || slot.Legacy && u.QuotaSlotId is null)));
        foreach(var app in status.Applications) appNames[app.Id]=app.Name+" · "+app.FileName;
        var execution=status.Execution;var web=status.Websites;
        ExecutionText.Text=execution.Fault is {} problem?"应用执行异常："+(problem.Length>120?problem[..120]+"…（请导出诊断）":problem):execution.Confirmed?"应用策略已确认 · 拒绝 "+execution.Denied.Length+" / 白名单组 "+execution.AllowGroups:"应用策略：等待确认";
        ExecutionText.Text+=web.Required?"\n浏览器组件："+web.ConnectedProfiles+" 个配置文件已确认"+(web.GateConfirmed?(web.FirewallClosed?" · 未就绪的浏览器联网已限制":""):" · 网络执行状态未确认"):"\n网站保护：未启用，可先连接浏览器组件";
        if(web.Fault is {} webFault)ExecutionText.Text+="\n网站保护异常："+webFault;
        SubmitButton.IsEnabled=status.StorageHealthy && status.ClockConsistent && !status.TrialExpired;
        CancelButton.IsEnabled=status.StorageHealthy && status.Pending is not null;
        ReviewButton.IsEnabled=status.Pending is not null;PendingViewButton.IsEnabled=status.Pending is not null;
        if(changed) {
            UpdateRuleList();
        }
        string clock=status.ClockConsistent?"":" · 时间异常，暂不接受提交";
        if(status.Pending is {} p) {
            var remaining=p.NotBeforeUtc-status.EffectiveUtc;
            string wait=remaining>TimeSpan.Zero?"尚需等待 "+Duration(remaining)+"（到 "+Local(p.NotBeforeUtc)+"）":"等待后台自动切换";
            PendingText.Text="待生效规则已保存 · "+wait+"\n等待期间，当前规则继续运行；到期自动切换。"+clock;
        } else PendingText.Text="没有待生效修改 · 放宽限制等待 "+Duration(status.Policy.ChangeDelay)+clock;
        if(status.TrialEndsUtc is {} end)ModeNotice.Text=status.TrialExpired?"主机首轮测试已结束。已保留并停用规则；请查看执行状态确认解除，可以导出或正常卸载。":"主机短时测试 · 到 "+Local(end)+" 自动解除限制并保留规则。选定内容后才会封锁，独立管理员恢复工具随包提供。";
        if(status.Fault is {} fault) Feedback.Text=fault;
        if(status.UninstallReady) {SubmitButton.IsEnabled=false;Feedback.Text="卸载已获准，后台正在撤销本软件的限制，规则与日志将保留。";}
        if(draftToken is not null && draftToken!=status.EditToken) DraftState.Text="后台配置已变化；请重新读取后提交";
    }
    string Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value,PolicyValidation.Zone(latest?.Policy ?? new())).ToString("MM-dd HH:mm");
    PolicyConfiguration VisiblePolicy()=>editingPending && latest?.Pending is {} p?PolicyJson.Parse<PolicyConfiguration>(p.TargetJson):latest!.Policy;
    void UpdateRuleList(){loading=true;Rules.ItemsSource=VisiblePolicy().Groups.Select(g=>new RuleEntry(g)).ToArray();RuleListCaption.Text=editingPending?"待生效的规则":"正在生效的规则";CurrentViewButton.IsEnabled=editingPending;PendingViewButton.IsEnabled=latest?.Pending is not null;loading=false;}
    void ShowCurrentRules(object sender,RoutedEventArgs e){editingPending=false;UpdateRuleList();StartDraft(VisiblePolicy().Groups.FirstOrDefault(g=>g.Id==editing?.Id)??VisiblePolicy().Groups.FirstOrDefault()??NewGroup());}
    void ShowPendingRules(object sender,RoutedEventArgs e){if(latest?.Pending is null)return;editingPending=true;UpdateRuleList();StartDraft(VisiblePolicy().Groups.FirstOrDefault(g=>g.Id==editing?.Id)??VisiblePolicy().Groups.FirstOrDefault()??NewGroup());}
    static string Duration(TimeSpan value) => value.TotalHours>=1?Math.Ceiling(value.TotalHours)+" 小时":value.TotalMinutes>=1?Math.Ceiling(value.TotalMinutes)+" 分钟":Math.Ceiling(value.TotalSeconds)+" 秒";
    void Disconnected(Exception ex) {
        BackendState.Text="后台：未连接";SubmitButton.IsEnabled=false;CancelButton.IsEnabled=false;ReviewButton.IsEnabled=false;
        if(connection.IsService) ExecutionText.Text="系统执行：连接中断，当前状态尚未确认";
        Feedback.Text="连接中断，保存结果尚未确认；请重新读取后台状态。"+ex.Message;
    }
    RuleGroup NewGroup() => new() {Id=Guid.NewGuid(),Name="我的专注规则",ApplicationMode=TargetMode.BlockSelected,LockWhileBlocking=true};
    void StartDraft(RuleGroup group) {
        if(latest is null) return;
        draftBase=VisiblePolicy();draftToken=latest.EditToken;editing=group;
        RuleName.Text=group.Name;Enabled.IsChecked=group.Enabled;LockWhile.IsChecked=group.LockWhileBlocking;
        AppMode.SelectedIndex=(int)group.ApplicationMode;SiteMode.SelectedIndex=(int)group.WebsiteMode;
        Applications.Items.Clear();foreach(string id in group.ApplicationIds) Applications.Items.Add(Entry(id));
        AppExceptions.Items.Clear();foreach(string id in group.ApplicationExceptions) AppExceptions.Items.Add(Entry(id));
        Sites.Text=string.Join(Environment.NewLine,group.WebsiteDomains);SetExceptionRows(ExceptionCatalog.Rows(group));
        Always.IsChecked=group.AlwaysBlock;loadingQuota=true;QuotaEnabled.IsChecked=QuotaSchedules.HasQuota(group);QuotaMinutes.Text=(group.DailyQuota?.TotalMinutes ?? 30).ToString(CultureInfo.InvariantCulture);quotaSlots.Clear();QuotaSlotsPanel.Children.Clear();
        if(group.QuotaSlots is {} slots)foreach(var slot in slots)AddQuotaEditor(slot);
        else if(group.DailyQuotaWindow is {} legacy)AddQuotaEditor(new(QuotaSchedules.LegacySlotId(group.Id),legacy.StartMinute,legacy.EndMinute,group.DailyQuota??TimeSpan.FromMinutes(30),"时段1"),true);
        QuotaScope.SelectedIndex=quotaSlots.Count>0?1:0;loadingQuota=false;UpdateQuotaControls();
        CycleEnabled.IsChecked=group.Cycle is not null;BlockMinutes.Text=(group.Cycle?.BlockFor.TotalMinutes ?? 25).ToString(CultureInfo.InvariantCulture);
        RestMinutes.Text=(group.Cycle?.RestFor.TotalMinutes ?? 5).ToString(CultureInfo.InvariantCulture);
        anchor=group.Cycle?.AnchorUtc ?? latest.EffectiveUtc;CycleAnchor.Text=connection.IsService && !draftBase.Groups.Any(g=>g.Id==group.Id)?"新循环在首次提交时确定起点；现有循环再次编辑会保留原起点。":"固定起点："+Local(anchor)+"；现有循环的起点会保留，重开窗口不重置。";
        windows.Clear();WindowsPanel.Children.Clear();foreach(var window in group.Windows) AddWindowEditor(window);
        DelayUnit.SelectedIndex=connection.IsService?0:1;
        DelayHours.Text=(connection.IsService?(draftBase!.Groups.IsEmpty && draftBase!.ChangeDelay==TimeSpan.Zero?1:draftBase!.ChangeDelay.TotalMinutes):(draftBase!.Groups.IsEmpty && draftBase!.ChangeDelay==TimeSpan.Zero?24:draftBase!.ChangeDelay.TotalHours)).ToString(CultureInfo.InvariantCulture);
        TimeZone.SelectedIndex=draftBase.TimeZoneId=="UTC"?1:0;TimedEnabled.IsChecked=group.BlockUntilUtc is not null;TimedMinutes.Text=(group.BlockUntilUtc is {} until?Math.Max(1,(until-latest.EffectiveUtc).TotalMinutes):25).ToString("0.##",CultureInfo.InvariantCulture);Editor.IsEnabled=true;DraftState.Text=editingPending?"正在编辑待生效规则 · 重新提交会重新计时":"正在编辑当前规则 · 草稿尚未提交";
        originalTimedText=TimedMinutes.Text;
        Feedback.Text=editingPending?"这里显示已保存的待生效规则。再次修改后提交，按当前生效规则的等待时间重新计时。":"选择应用或填写网站。收紧立即生效；放宽才等待，到期自动切换。";
    }
    AppEntry Entry(string id) => new(id,appNames.GetValueOrDefault(id) ?? "已选应用 · 文件身份 "+id[^Math.Min(id.Length,8)..]);
    void NewRule(object sender,RoutedEventArgs e) => StartDraft(NewGroup());
    void SelectRule(object sender,SelectionChangedEventArgs e) {if(!loading && Rules.SelectedItem is RuleEntry row) StartDraft(row.Rule);}
    void ReloadDraft(object sender,RoutedEventArgs e) {if(latest is not null) StartDraft(VisiblePolicy().Groups.FirstOrDefault(g=>g.Id==editing?.Id) ?? VisiblePolicy().Groups.FirstOrDefault() ?? NewGroup());}
    void AddWindow(object sender,RoutedEventArgs e) => AddWindowEditor(new(Weekdays.All,9*60,18*60));
    void AddWindowEditor(WeeklyWindow window) {
        var editor=new WindowEditor(window);windows.Add(editor);WindowsPanel.Children.Add(editor.Root);
        editor.Remove.Click+=(_,_)=>{windows.Remove(editor);WindowsPanel.Children.Remove(editor.Root);};
    }
    static double Number(TextBox box,double max,string label,bool positive=false) {
        if(!double.TryParse(box.Text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double n) || !double.IsFinite(n) || n<0 || n>max || (positive && n==0)) throw new ArgumentException(label+"填写范围不正确。");
        return n;
    }
    void QuotaControlsChanged(object sender,RoutedEventArgs e)=>UpdateQuotaControls();
    void QuotaScopeChanged(object sender,SelectionChangedEventArgs e)=>UpdateQuotaControls();
    void UpdateQuotaControls(){if(QuotaTimeFields is null || QuotaDailyFields is null || QuotaScope is null || QuotaHint is null)return;bool split=QuotaScope.SelectedIndex==1;QuotaTimeFields.Visibility=split?Visibility.Visible:Visibility.Collapsed;QuotaDailyFields.Visibility=split?Visibility.Collapsed:Visibility.Visible;QuotaScope.IsEnabled=QuotaEnabled.IsChecked==true;QuotaTimeFields.IsEnabled=QuotaEnabled.IsChecked==true;QuotaDailyFields.IsEnabled=QuotaEnabled.IsChecked==true;if(split && !loadingQuota && quotaSlots.Count==0)AddQuotaEditor(new(Guid.NewGuid(),9*60,12*60,TimeSpan.FromMinutes(20),"上午"));QuotaHint.Text=split?"各行拥有独立额度和用量，互不占用；用完仅封锁到该时段结束。支持跨午夜，不允许重叠。未设置的时间不受这些额度限制，其他硬封锁仍独立生效。":"全天共用一份额度，每日零点重置。用完后封锁到次日零点；其他硬封锁仍独立生效。";}
    void AddQuotaEditor(QuotaSlot slot,bool legacy=false){var row=new QuotaSlotEditor(slot,legacy);quotaSlots.Add(row);QuotaSlotsPanel.Children.Add(row.Root);row.Remove.Click+=(_,_)=>{quotaSlots.Remove(row);QuotaSlotsPanel.Children.Remove(row.Root);};row.ShowUsage(latest?.Usage.FirstOrDefault(u=>u.GroupId==editing?.Id && (u.QuotaSlotId==slot.Id || legacy && u.QuotaSlotId is null)));}
    void AddQuotaSlot(object sender,RoutedEventArgs e){try{if(quotaSlots.Count>=24)throw new ArgumentException("每组最多24个独立额度时段。");var existing=quotaSlots.Select(s=>s.Value()).ToArray();var candidates=new[]{new QuotaWindow(18*60,22*60),new(9*60,12*60)}.Concat(Enumerable.Range(0,24).Select(h=>new QuotaWindow(h*60,(h+1)*60)));var free=candidates.FirstOrDefault(c=>existing.All(s=>!QuotaSchedules.Overlaps(c,new(s.StartMinute,s.EndMinute))));if(free is null)throw new ArgumentException("当前时段已覆盖全天，请缩短或移除一个时段后再添加。");AddQuotaEditor(new(Guid.NewGuid(),free.StartMinute,free.EndMinute,TimeSpan.FromMinutes(40),"时段"+(quotaSlots.Count+1)));}catch(ArgumentException ex){Feedback.Text=ex.Message;}}
    static ImmutableArray<string> Domains(TextBox box) {
        var result=ImmutableArray.CreateBuilder<string>();int line=0;
        foreach(string text in box.Text.Split('\n')){line++;string value=text.Trim();if(value.Length==0)continue;try{result.Add(WebsitePattern.Normalize(value));}catch(ArgumentException e){throw new ArgumentException((box.Name=="SiteExceptions"?"网站例外":"网站列表")+"第"+line+"行："+e.Message);}}
        return result.Distinct().ToImmutableArray();
    }
    ImmutableArray<WebsiteExceptionDetail> ExceptionRowsFromDraft(){var details=exceptionRows.ToDictionary(e=>WebsitePattern.Normalize(e.Pattern),StringComparer.OrdinalIgnoreCase);return Domains(SiteExceptions).Select(p=>details.GetValueOrDefault(p)??new WebsiteExceptionDetail(p)).ToImmutableArray();}
    void SetExceptionRows(ImmutableArray<WebsiteExceptionDetail> rows){exceptionRows=rows;SiteExceptions.Text=string.Join(Environment.NewLine,rows.Select(r=>r.Pattern));ExceptionSummary.Text=rows.IsEmpty?"尚未添加例外":$"{rows.Length} 条例外 · {rows.Select(r=>r.Group).Where(g=>g.Length>0).Distinct().Count()} 个分组 · {rows.Count(r=>r.Note.Length==0)} 条待备注";}
    void ManageExceptions(object sender,RoutedEventArgs e){try{Guid? id=editing?.Id;var window=new ExceptionWindow(ExceptionRowsFromDraft()){Owner=this};if(window.ShowDialog()==true){if(editing?.Id!=id){Feedback.Text="后台规则已切换，请重新打开例外列表后编辑。";return;}SetExceptionRows(window.Result);Feedback.Text="例外整理已保留在草稿。点击提交完整修改：仅改备注/分组立即保存，网址变化按限制强度处理。";}}catch(ArgumentException ex){Feedback.Text=ex.Message;}}
    PolicyConfiguration BuildPolicy(bool delete=false) {
        if(draftBase is null || editing is null) throw new InvalidOperationException("请先读取后台配置。");
        var group=editing with {Name=RuleName.Text.Trim(),Enabled=Enabled.IsChecked==true,LockWhileBlocking=LockWhile.IsChecked==true,
            ApplicationMode=(TargetMode)AppMode.SelectedIndex,WebsiteMode=(TargetMode)SiteMode.SelectedIndex,
            ApplicationIds=Applications.Items.Cast<AppEntry>().Select(x=>x.Id).ToImmutableArray(),ApplicationExceptions=AppExceptions.Items.Cast<AppEntry>().Select(x=>x.Id).ToImmutableArray(),
            WebsiteDomains=Domains(Sites),WebsiteExceptions=Domains(SiteExceptions),AlwaysBlock=Always.IsChecked==true,
            Windows=windows.Select(x=>x.Value()).ToImmutableArray(),DailyQuota=null,DailyQuotaWindow=null,QuotaSlots=null,
            BlockUntilUtc=TimedEnabled.IsChecked==true?(editing.BlockUntilUtc is {} deadline && TimedMinutes.Text==originalTimedText?deadline:latest!.EffectiveUtc+TimeSpan.FromMinutes(Number(TimedMinutes,525600,"倒计时分钟",true))):null,
            Cycle=CycleEnabled.IsChecked==true?new(connection.IsService && !draftBase.Groups.Any(g=>g.Id==editing.Id)?latest!.EffectiveUtc:anchor,TimeSpan.FromMinutes(Number(BlockMinutes,525600,"封锁分钟",true)),TimeSpan.FromMinutes(Number(RestMinutes,525600,"休息分钟",true))):null};
        if(QuotaEnabled.IsChecked==true){if(QuotaScope.SelectedIndex==0)group=group with{DailyQuota=TimeSpan.FromMinutes(Number(QuotaMinutes,1440,"每日额度"))};else{if(quotaSlots.Count==0)throw new ArgumentException("请至少添加一个额度时段。");var slots=quotaSlots.Select(s=>s.Value()).ToImmutableArray();if(quotaSlots.Count==1 && quotaSlots[0].Legacy && slots[0].Name=="时段1")group=group with{DailyQuota=slots[0].Limit,DailyQuotaWindow=new(slots[0].StartMinute,slots[0].EndMinute)};else group=group with{QuotaSlots=slots};}}
        group=ExceptionCatalog.SetRows(group,ExceptionRowsFromDraft());
        var groups=draftBase.Groups.Where(g=>g.Id!=group.Id).ToImmutableArray();
        if(!delete) groups=groups.Add(group);
        var delay=DelayUnit.SelectedIndex==0?TimeSpan.FromMinutes(Number(DelayHours,525600,"延迟分钟")):TimeSpan.FromHours(Number(DelayHours,8760,"延迟小时"));
        var policy=draftBase with {Groups=groups,ChangeDelay=delay,TimeZoneId=TimeZone.SelectedIndex==1?"UTC":"Asia/Shanghai"};
        if(group.Enabled && !((group.ApplicationMode==TargetMode.AllowSelected)||(group.ApplicationMode==TargetMode.BlockSelected&&!group.ApplicationIds.IsEmpty)||(group.WebsiteMode==TargetMode.AllowSelected)||(group.WebsiteMode==TargetMode.BlockSelected&&!group.WebsiteDomains.IsEmpty)))throw new ArgumentException("请选择应用，或填写网站并启用对应的限制方式。");
        if(group.Enabled && !group.AlwaysBlock && group.Windows.IsEmpty && group.Cycle is null && !QuotaSchedules.HasQuota(group) && group.BlockUntilUtc is null)throw new ArgumentException("请至少选择一种封锁条件：全天、每周时段、循环、额度或倒计时。");
        PolicyValidation.Validate(policy);return policy;
    }
    async Task SubmitAsync(bool delete=false) {
        try {
            var policy=BuildPolicy(delete);
            var beforeNotes=draftBase?.Groups.FirstOrDefault(g=>g.Id==editing?.Id);var afterNotes=policy.Groups.FirstOrDefault(g=>g.Id==editing?.Id);
            if(!delete && editing is not null && draftBase is not null && beforeNotes is not null && afterNotes is not null && ExceptionCatalog.SameEnforcement(draftBase,policy) && PolicyJson.Serialize(beforeNotes.WebsiteExceptionDetails)!=PolicyJson.Serialize(afterNotes.WebsiteExceptionDetails)) {
                var annotated=policy.Groups.Single(g=>g.Id==editing.Id);
                var noteResponse=await CallAsync(new(){Command=PreviewCommand.UpdateWebsiteAnnotations,EditToken=draftToken,Annotations=new(annotated.Id,ExceptionCatalog.Rows(annotated))});
                if(noteResponse.Status is {} status)ShowStatus(status);
                if(noteResponse.Ok){UpdateRuleList();StartDraft(VisiblePolicy().Groups.First(g=>g.Id==annotated.Id));}
                Feedback.Text=noteResponse.Message;return;
            }
            if(!connection.Simulated && !delete && PolicyStrength.IsAtLeastAsRestrictive(latest!.Policy,policy,latest.EffectiveUtc) && policy.Groups.Any(g=>g.Enabled) && MessageBox.Show(this,"这次修改仅收紧限制，将立即生效。受限应用可能被关闭，请先保存工作；网站保护需要浏览器组件保持连接。"+(latest.Pending is not null?"\n原来的待生效申请会取消。":"")+"\n\n保存并立即执行？","立即收紧限制",MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK) return;
            var response=await CallAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=draftToken,Policy=policy});
            if(response.Status is {} s) ShowStatus(s);
            if(response.Ok) {
                editingPending=latest!.Pending is not null && response.Code is "QUEUED" or "ALREADY_QUEUED";UpdateRuleList();StartDraft(VisiblePolicy().Groups.FirstOrDefault(g=>g.Id==editing?.Id) ?? VisiblePolicy().Groups.FirstOrDefault() ?? NewGroup());
            }
            Feedback.Text=response.Message;
        } catch(Exception ex) when(ex is ArgumentException or InvalidOperationException) {Feedback.Text="未提交："+ex.Message;}
        catch(Exception ex) {Disconnected(ex);}
    }
    async void Submit(object sender,RoutedEventArgs e) => await SubmitAsync();
    async void DeleteRule(object sender,RoutedEventArgs e) {
        if(editing is null || draftBase is null || !draftBase.Groups.Any(g=>g.Id==editing.Id)) {Feedback.Text="此草稿尚未保存，无需删除。";return;}
        await SubmitAsync(delete:true);
    }
    async void CancelPending(object sender,RoutedEventArgs e) {
        if(latest?.Pending is not {} p) return;
        try {var response=await CallAsync(new() {Command=PreviewCommand.CancelPending,EditToken=latest.EditToken,PendingId=p.Id});if(response.Status is {} s) ShowStatus(s);Feedback.Text=response.Message;}
        catch(Exception ex) {Disconnected(ex);}
    }
    void ReviewPending(object sender,RoutedEventArgs e) {
        if(latest?.Pending is not {} pending) return;
        var policy=PolicyJson.Parse<PolicyConfiguration>(pending.TargetJson);
        var text=new System.Text.StringBuilder("提交时间："+Local(pending.SubmittedUtc)+"\n最早生效："+Local(pending.NotBeforeUtc)+"（到期自动切换，当前规则继续运行到那时）\n新配置时区："+policy.TimeZoneId+"\n新配置后续修改延迟："+policy.ChangeDelay.TotalHours+" 小时\n");
        foreach(var g in policy.Groups) {
            text.AppendLine("\n"+g.Name+(g.Enabled?" · 启用":" · 停用"));
            static string Mode(TargetMode mode) => mode switch {TargetMode.None=>"不限制",TargetMode.BlockSelected=>"封锁列表",_=>"仅允许列表"};
            text.AppendLine("应用："+Mode(g.ApplicationMode)+" / "+string.Join("、",g.ApplicationIds.Select(id=>Entry(id).Label)));
            text.AppendLine("应用例外："+string.Join("、",g.ApplicationExceptions.Select(id=>Entry(id).Label)));
            text.AppendLine("网站："+Mode(g.WebsiteMode)+" / "+string.Join("、",g.WebsiteDomains));text.AppendLine("网站例外："+string.Join("、",g.WebsiteExceptions));
            text.AppendLine("全天封锁："+(g.AlwaysBlock?"是":"否"));
            foreach(var w in g.Windows) {
                string[] dayNames=["一","二","三","四","五","六","日"];
                string selected=string.Join("、",Enumerable.Range(0,7).Where(i=>((int)w.Days & (1<<i))!=0).Select(i=>dayNames[i]));
                text.AppendLine($"每周 {selected} / {w.StartMinute/60:00}:{w.StartMinute%60:00}—{w.EndMinute/60:00}:{w.EndMinute%60:00}");
            }
            if(!QuotaSchedules.HasQuota(g))text.AppendLine("共享额度：未设置");foreach(var quota in QuotaSchedules.Specs(g))text.AppendLine("独立额度："+QuotaSchedules.Label(quota)+" 内共用 "+quota.Limit.TotalMinutes+" 分钟");
            if(g.Cycle is {} c) text.AppendLine($"循环：封锁 {c.BlockFor.TotalMinutes} 分钟 / 休息 {c.RestFor.TotalMinutes} 分钟；固定起点 {Local(c.AnchorUtc)}");
        }
        var window=new Window {Owner=this,Title="完整待生效规则 · 只读预览",Width=720,Height=610,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Content=new TextBox {Text=text.ToString(),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(18)}};
        window.ShowDialog();
    }
    async void AddApplication(object sender,RoutedEventArgs e) => await PickApplication(Applications);
    async void AddAppException(object sender,RoutedEventArgs e) => await PickApplication(AppExceptions);
    void RemoveApplication(object sender,RoutedEventArgs e) {if(Applications.SelectedItem is {} item) Applications.Items.Remove(item);}
    void RemoveAppException(object sender,RoutedEventArgs e) {if(AppExceptions.SelectedItem is {} item) AppExceptions.Items.Remove(item);}
    async Task PickApplication(ListBox list) {
        var picker=new OpenFileDialog {Filter="Windows 应用 (*.exe)|*.exe",Title="选择当前版本的应用文件"};
        if(connection.IsService && connection.DemoPath is {} demo) {picker.InitialDirectory=Path.GetDirectoryName(demo);picker.FileName=demo;}
        if(picker.ShowDialog(this)!=true) return;
        try {
            string id="authenticode:sha256:"+AuthenticodeHash.Compute(picker.FileName)[2..];
            string label=FileVersionInfo.GetVersionInfo(picker.FileName).FileDescription ?? Path.GetFileName(picker.FileName);
            if(string.IsNullOrWhiteSpace(label)) label=Path.GetFileName(picker.FileName);
            if(connection.IsService) {
                if(latest is null || draftBase is null) throw new InvalidOperationException("先读取后台状态。");
                string fingerprint=PolicyJson.Fingerprint(draftBase);
                var result=await CallAsync(new() {Command=PreviewCommand.RegisterApplication,EditToken=draftToken,Application=new(id,label,Path.GetFileName(picker.FileName),new FileInfo(picker.FileName).Length)});
                if(result.Status is {} state) ShowStatus(state);
                if(!result.Ok) {Feedback.Text=result.Message;return;}
                if(PolicyJson.Fingerprint(VisiblePolicy())!=fingerprint) {Feedback.Text="应用已登记，但后台规则发生变化，请重新读取后编辑。";return;}
                draftToken=latest.EditToken;draftBase=VisiblePolicy();DraftState.Text=editingPending?"正在编辑待生效规则 · 重新提交会重新计时":"正在编辑当前规则 · 草稿尚未提交";
            }
            appNames[id]=label+" · "+Path.GetFileName(picker.FileName);
            if(!list.Items.Cast<AppEntry>().Any(x=>x.Id==id)) list.Items.Add(Entry(id));
            if(!connection.IsService) File.WriteAllText(Path.Combine(connection.DataDirectory,"app-labels.json"),PolicyJson.Serialize(appNames));
            Feedback.Text="应用文件已加入草稿；提交后才由后台判断何时执行。";
        } catch(Exception ex) {Feedback.Text="无法识别这个应用："+ex.Message;}
    }
    void LaunchDemo(object sender,RoutedEventArgs e) {
        try {if(connection.DemoPath is not {} path || !File.Exists(path)) throw new FileNotFoundException("测试应用不可用。");Process.Start(new ProcessStartInfo(path) {UseShellExecute=false});}
        catch(Exception ex) {Feedback.Text="测试应用未能启动："+ex.Message;}
    }
    async void ExportJson(object sender,RoutedEventArgs e) => await ExportAsync(PreviewCommand.ExportJson,"json");
    async void ExportCsv(object sender,RoutedEventArgs e) => await ExportAsync(PreviewCommand.ExportCsv,"csv");
    async Task ExportAsync(PreviewCommand command,string extension) {
        try {
            var response=await CallAsync(new() {Command=command});
            if(!response.Ok || response.ExportText is null) {Feedback.Text=response.Message;return;}
            var picker=new SaveFileDialog {Filter=$"数据导出 (*.{extension})|*.{extension}",FileName="专注计划-规则用量."+extension};
            if(picker.ShowDialog(this)==true) {File.WriteAllText(picker.FileName,response.ExportText,new System.Text.UTF8Encoding(true));Feedback.Text=response.Message+" 文件已导出。";}
        } catch(Exception ex) {Feedback.Text="导出失败："+ex.Message;}
    }
    async void BrowserSetup(object sender,RoutedEventArgs e)=>await ShowBrowserSetup();
    async Task ShowBrowserSetup() {new BrowserWindow(connection){Owner=this}.ShowDialog();await RefreshAsync();}
    void Statistics(object sender,RoutedEventArgs e)=>new StatisticsWindow(connection){Owner=this}.ShowDialog();
    void Diagnostics(object sender,RoutedEventArgs e)=>new DiagnosticsWindow(connection){Owner=this}.ShowDialog();
    async Task SelfTestAsync() {
        timer.Stop();var empty=NewGroup();if(empty.Cycle is not null)throw new InvalidOperationException("新规则不应默认启用循环");StartDraft(empty);
        RuleName.Text="自用版界面检查";ApplicationModeForTest();
        Sites.Text="*bilibili*\nhttps://www.youtube.com/watch?v=test";SiteMode.SelectedIndex=1;QuotaEnabled.IsChecked=true;QuotaMinutes.Text="30";Always.IsChecked=false;CycleEnabled.IsChecked=false;Enabled.IsChecked=true;LockWhile.IsChecked=false;DelayUnit.SelectedIndex=0;DelayHours.Text="1";
        QuotaScope.SelectedIndex=1;AddQuotaSlot(this,new());
        _=BuildPolicy();await SubmitAsync();if(latest?.Policy.Groups.Length!=1 || latest.Policy.Groups[0].QuotaSlots is not {Length:2} savedSlots || savedSlots[0].Limit!=TimeSpan.FromMinutes(20) || savedSlots[1].Limit!=TimeSpan.FromMinutes(40))throw new InvalidOperationException("界面未保存两个独立额度时段");
        RuleName.Text="待生效的新规则";SiteExceptions.Text="*favlist*\nbilibili.com/video/BV1Example?spm_id_from=test\nfile://*.*";await SubmitAsync();if(latest.Pending is null || !editingPending || RuleName.Text!="待生效的新规则" || latest.Policy.Groups.Single().Name==RuleName.Text)throw new InvalidOperationException("待生效规则未展示或提前覆盖当前规则");
        var frozen=latest.Pending;SetExceptionRows(ExceptionRowsFromDraft().Select((r,i)=>i==0?r with{Note="收藏夹入口",Group="学习资料"}:r).ToImmutableArray());await SubmitAsync();
        if(latest.Pending?.Id!=frozen.Id || latest.Pending.NotBeforeUtc!=frozen.NotBeforeUtc || exceptionRows[0].Note!="收藏夹入口")throw new InvalidOperationException("保存备注改变了等待或丢失备注");
        string output=Path.Combine(connection.BundleRoot,"ui-self-test");Directory.CreateDirectory(output);
        async Task Render(string file){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();var root=(FrameworkElement)Content;var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,file));enc.Save(stream);}
        await Render("editor.png");QuotaHint.BringIntoView();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Render("quota.png");EditorScroll.ScrollToEnd();await Render("schedule.png");
        var organizer=new ExceptionWindow(exceptionRows);organizer.TestSearch("学习资料");if(organizer.VisibleCount!=1)throw new InvalidOperationException("例外搜索未匹配分组");organizer.TestSearch("");
        var content=(FrameworkElement)organizer.Content;organizer.Content=null;var preview=new Border{Background=Brushes.White,Child=content};preview.Measure(new Size(952,620));preview.Arrange(new Rect(0,0,952,620));preview.UpdateLayout();var picture=new RenderTargetBitmap(952,620,96,96,PixelFormats.Pbgra32);picture.Render(preview);var image=new PngBitmapEncoder();image.Frames.Add(BitmapFrame.Create(picture));using(var file=File.Create(Path.Combine(output,"exceptions.png")))image.Save(file);
        organizer.Close();File.WriteAllText(Path.Combine(output,"result.json"),PolicyJson.Serialize(new{passed=true,customWebsites=true,quotaConfigured=true,windowQuota=true,independentQuotaSlots=2,pendingFrozen=true,exceptionNotes=true,groupSearch=true,newRulesHaveNoCycle=true,systemWrites=false}));Close();
        void ApplicationModeForTest(){AppMode.SelectedIndex=0;}
    }

}

sealed class WindowEditor {
    public StackPanel Root {get;}=new() {Margin=new Thickness(0,3,0,8)};
    readonly CheckBox[] days=new CheckBox[7];
    readonly TextBox start=new() {Width=72},end=new() {Width=72};
    public Button Remove {get;}=new() {Content="删除时段"};
    public WindowEditor(WeeklyWindow value) {
        var line=new WrapPanel();string[] names=["一","二","三","四","五","六","日"];
        for(int i=0;i<7;i++) {days[i]=new CheckBox {Content=names[i],IsChecked=((int)value.Days & (1<<i))!=0};line.Children.Add(days[i]);}
        Root.Children.Add(line);var times=new WrapPanel();start.Text=Format(value.StartMinute);end.Text=Format(value.EndMinute);
        times.Children.Add(start);times.Children.Add(new TextBlock {Text="至",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,10,0)});times.Children.Add(end);times.Children.Add(Remove);Root.Children.Add(times);
    }
    internal static string Format(int minute) => $"{minute/60:00}:{minute%60:00}";
    internal static int Minute(string text,bool ending) {
        string[] parts=text.Split(':');
        if(parts.Length!=2 || !int.TryParse(parts[0],out int h) || !int.TryParse(parts[1],out int m) || h<0 || h>23+(ending?1:0) || m is <0 or >59 || (h==24 && m!=0)) throw new ArgumentException("时段使用 HH:mm；仅结束时间可填 24:00。");
        return h*60+m;
    }
    public WeeklyWindow Value() {
        int flags=0;for(int i=0;i<7;i++) if(days[i].IsChecked==true) flags|=1<<i;
        return new((Weekdays)flags,Minute(start.Text,false),Minute(end.Text,true));
    }
}
