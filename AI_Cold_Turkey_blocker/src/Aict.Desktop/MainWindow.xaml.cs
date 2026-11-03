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
using Aict.Core;
using Microsoft.Win32;

namespace Aict.Desktop;

public partial class MainWindow : Window {
    readonly PreviewConnection connection=new();
    readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromSeconds(1)};
    readonly SemaphoreSlim calls=new(1,1);
    readonly List<WindowEditor> windows=[];
    readonly Dictionary<string,string> appNames=new(StringComparer.Ordinal);
    PreviewStatus? latest;
    PolicyConfiguration? draftBase;
    string? draftToken;
    RuleGroup? editing;
    DateTimeOffset anchor;
    bool loading,polling;
    sealed record AppEntry(string Id,string Label) {public override string ToString()=>Label;}
    sealed record RuleEntry(RuleGroup Rule) {public override string ToString()=>Rule.Name+(Rule.Enabled?"":" · 停用");}
    public MainWindow() {
        InitializeComponent();
        Loaded+=OnLoaded;timer.Tick+=async(_,_)=>await RefreshAsync();Closing+=(_,_)=>timer.Stop();
    }
    async void OnLoaded(object sender,RoutedEventArgs e) {
        try {
            await connection.EnsureStartedAsync();
            if(connection.IsService) {
                Title="专注计划 · 虚拟机应用联调";ModeNotice.Text="虚拟机应用联调：会按规则阻止启动并结束选中的应用。网站、额度和白名单尚未接入；请先用无害测试应用。";
                FooterNotice.Text="关闭界面不会解除系统规则。修改由后台校验；同一次开机内服务恢复后继续计时。";DemoButton.Visibility=Visibility.Visible;
                DelayNotice.Text="本次修改等待多久，由后台当前配置决定。首次空白配置的延迟为0；短时实验默认将后续修改设为1分钟。锁定期间，等待结束后还需等旧规则允许修改。";
                SiteMode.IsEnabled=false;Sites.IsEnabled=false;SiteExceptions.IsEnabled=false;QuotaEnabled.IsEnabled=false;QuotaMinutes.IsEnabled=false;((ComboBoxItem)AppMode.Items[2]).IsEnabled=false;
            }
            string names=Path.Combine(connection.DataDirectory,"app-labels.json");
            try {if(!connection.IsService && File.Exists(names)) foreach(var pair in PolicyJson.Parse<Dictionary<string,string>>(File.ReadAllText(names))) appNames[pair.Key]=pair.Value;}
            catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException) {Feedback.Text="应用显示名称未能读取；规则身份仍从后台读取。";}
            await RefreshAsync();timer.Start();
            if(Environment.GetCommandLineArgs().Contains("--self-test")) await SelfTestAsync();
        } catch(Exception ex) {Disconnected(ex);if(Environment.GetCommandLineArgs().Contains("--self-test")) {Environment.ExitCode=1;Close();}}
    }
    async Task<PreviewResponse> CallAsync(PreviewRequest request) {
        await calls.WaitAsync();
        try {return await connection.CallAsync(request);} finally {calls.Release();}
    }
    async Task RefreshAsync() {
        if(polling) return;polling=true;
        try {
            var response=await CallAsync(new());
            if(!response.Ok || response.Status is null) throw new IOException(response.Message);
            ShowStatus(response.Status);
            if(draftBase is null) StartDraft(latest!.Policy.Groups.FirstOrDefault() ?? NewGroup());
        } catch(Exception ex) {Disconnected(ex);} finally {polling=false;}
    }
    void ShowStatus(PreviewStatus status) {
        bool changed=latest?.EditToken!=status.EditToken;latest=status;
        BackendState.Text=status.StorageHealthy?(connection.IsService?"后台：已连接 · Windows服务":"后台：已连接 · 普通进程"):"后台：存储故障";
        foreach(var app in status.Applications) appNames[app.Id]=app.Name+" · "+app.FileName;
        if(status.Execution is {} execution) {
            ExecutionText.Text=execution.Fault is {} problem?"执行异常："+problem:execution.Verified?(execution.VerifiedDeniedIdentities.IsEmpty?"系统策略：已确认当前无需封锁":"系统策略：已确认 "+execution.VerifiedDeniedIdentities.Length+" 个应用身份受限"):"系统策略：等待执行确认";
        }
        SubmitButton.IsEnabled=status.StorageHealthy && status.ClockConsistent;
        CancelButton.IsEnabled=status.StorageHealthy && status.Pending is not null;
        ReviewButton.IsEnabled=status.Pending is not null;
        if(changed) {
            loading=true;Rules.ItemsSource=status.Policy.Groups.Select(g=>new RuleEntry(g)).ToArray();loading=false;
        }
        string clock=status.ClockConsistent?"":" · 时间异常，暂不接受提交";
        if(status.Pending is {} p) {
            var remaining=p.NotBeforeUtc-status.EffectiveUtc;
            string wait=remaining>TimeSpan.Zero?"尚需等待 "+Duration(remaining)+"（到 "+Local(p.NotBeforeUtc)+"）":"延迟已结束，等待旧规则允许修改";
            PendingText.Text="已有完整冻结修改 · "+wait+clock;
        } else PendingText.Text="没有待生效修改 · 当前修改延迟 "+Duration(status.Policy.ChangeDelay)+clock;
        if(status.Fault is {} fault) Feedback.Text=fault;
        if(draftToken is not null && draftToken!=status.EditToken) DraftState.Text="后台配置已变化；请重新读取后提交";
    }
    string Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value,PolicyValidation.Zone(latest?.Policy ?? new())).ToString("MM-dd HH:mm");
    static string Duration(TimeSpan value) => value.TotalHours>=1?Math.Ceiling(value.TotalHours)+" 小时":value.TotalMinutes>=1?Math.Ceiling(value.TotalMinutes)+" 分钟":Math.Ceiling(value.TotalSeconds)+" 秒";
    void Disconnected(Exception ex) {
        BackendState.Text="后台：未连接";SubmitButton.IsEnabled=false;CancelButton.IsEnabled=false;ReviewButton.IsEnabled=false;
        if(connection.IsService) ExecutionText.Text="系统执行：连接中断，当前状态尚未确认";
        Feedback.Text="连接中断，保存结果尚未确认；请重新读取后台状态。"+ex.Message;
    }
    RuleGroup NewGroup() => connection.IsService?new() {Id=Guid.NewGuid(),Name="我的应用限制",ApplicationMode=TargetMode.BlockSelected,Cycle=new(latest?.EffectiveUtc ?? DateTimeOffset.UtcNow,TimeSpan.FromMinutes(2),TimeSpan.FromMinutes(1)),LockWhileBlocking=true}:new() {Id=Guid.NewGuid(),Name="新的专注规则",WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["bilibili.com"],Windows=[new(Weekdays.Monday|Weekdays.Tuesday|Weekdays.Wednesday|Weekdays.Thursday|Weekdays.Friday,9*60,18*60)],LockWhileBlocking=true};
    void StartDraft(RuleGroup group) {
        if(latest is null) return;
        draftBase=latest.Policy;draftToken=latest.EditToken;editing=group;
        RuleName.Text=group.Name;Enabled.IsChecked=group.Enabled;LockWhile.IsChecked=group.LockWhileBlocking;
        AppMode.SelectedIndex=(int)group.ApplicationMode;SiteMode.SelectedIndex=(int)group.WebsiteMode;
        Applications.Items.Clear();foreach(string id in group.ApplicationIds) Applications.Items.Add(Entry(id));
        AppExceptions.Items.Clear();foreach(string id in group.ApplicationExceptions) AppExceptions.Items.Add(Entry(id));
        Sites.Text=string.Join(Environment.NewLine,group.WebsiteDomains);SiteExceptions.Text=string.Join(Environment.NewLine,group.WebsiteExceptions);
        Always.IsChecked=group.AlwaysBlock;QuotaEnabled.IsChecked=group.DailyQuota is not null;QuotaMinutes.Text=(group.DailyQuota?.TotalMinutes ?? 30).ToString(CultureInfo.InvariantCulture);
        CycleEnabled.IsChecked=group.Cycle is not null;BlockMinutes.Text=(group.Cycle?.BlockFor.TotalMinutes ?? 25).ToString(CultureInfo.InvariantCulture);
        RestMinutes.Text=(group.Cycle?.RestFor.TotalMinutes ?? 5).ToString(CultureInfo.InvariantCulture);
        anchor=group.Cycle?.AnchorUtc ?? latest.EffectiveUtc;CycleAnchor.Text=connection.IsService && !latest.Policy.Groups.Any(g=>g.Id==group.Id)?"新循环在首次提交时确定起点；现有循环再次编辑会保留原起点。":"固定起点："+Local(anchor)+"；现有循环的起点会保留，重开窗口不重置。";
        windows.Clear();WindowsPanel.Children.Clear();foreach(var window in group.Windows) AddWindowEditor(window);
        DelayUnit.SelectedIndex=connection.IsService?0:1;
        DelayHours.Text=(connection.IsService?(latest.Policy.Groups.IsEmpty && latest.Policy.ChangeDelay==TimeSpan.Zero?1:latest.Policy.ChangeDelay.TotalMinutes):(latest.Policy.Groups.IsEmpty && latest.Policy.ChangeDelay==TimeSpan.Zero?24:latest.Policy.ChangeDelay.TotalHours)).ToString(CultureInfo.InvariantCulture);
        TimeZone.SelectedIndex=latest.Policy.TimeZoneId=="UTC"?1:0;Editor.IsEnabled=true;DraftState.Text="本地草稿 · 尚未提交";
        Feedback.Text=connection.IsService?"先选择应用，再提交。首次实验默认循环封锁2分钟、休息1分钟，后续修改等待1分钟。":"编辑规则后点击“提交完整修改”。预览不会执行系统封锁。";
    }
    AppEntry Entry(string id) => new(id,appNames.GetValueOrDefault(id) ?? "已选应用 · 文件身份 "+id[^Math.Min(id.Length,8)..]);
    void NewRule(object sender,RoutedEventArgs e) => StartDraft(NewGroup());
    void SelectRule(object sender,SelectionChangedEventArgs e) {if(!loading && Rules.SelectedItem is RuleEntry row) StartDraft(row.Rule);}
    void ReloadDraft(object sender,RoutedEventArgs e) {if(latest is not null) StartDraft(latest.Policy.Groups.FirstOrDefault(g=>g.Id==editing?.Id) ?? latest.Policy.Groups.FirstOrDefault() ?? NewGroup());}
    void AddWindow(object sender,RoutedEventArgs e) => AddWindowEditor(new(Weekdays.All,9*60,18*60));
    void AddWindowEditor(WeeklyWindow window) {
        var editor=new WindowEditor(window);windows.Add(editor);WindowsPanel.Children.Add(editor.Root);
        editor.Remove.Click+=(_,_)=>{windows.Remove(editor);WindowsPanel.Children.Remove(editor.Root);};
    }
    static double Number(TextBox box,double max,string label,bool positive=false) {
        if(!double.TryParse(box.Text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double n) || !double.IsFinite(n) || n<0 || n>max || (positive && n==0)) throw new ArgumentException(label+"填写范围不正确。");
        return n;
    }
    static ImmutableArray<string> Domains(TextBox box) => box.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(PolicyValidation.Domain).Distinct().ToImmutableArray();
    PolicyConfiguration BuildPolicy(bool delete=false) {
        if(draftBase is null || editing is null) throw new InvalidOperationException("请先读取后台配置。");
        var group=editing with {Name=RuleName.Text.Trim(),Enabled=Enabled.IsChecked==true,LockWhileBlocking=LockWhile.IsChecked==true,
            ApplicationMode=(TargetMode)AppMode.SelectedIndex,WebsiteMode=(TargetMode)SiteMode.SelectedIndex,
            ApplicationIds=Applications.Items.Cast<AppEntry>().Select(x=>x.Id).ToImmutableArray(),ApplicationExceptions=AppExceptions.Items.Cast<AppEntry>().Select(x=>x.Id).ToImmutableArray(),
            WebsiteDomains=Domains(Sites),WebsiteExceptions=Domains(SiteExceptions),AlwaysBlock=Always.IsChecked==true,
            Windows=windows.Select(x=>x.Value()).ToImmutableArray(),DailyQuota=QuotaEnabled.IsChecked==true?TimeSpan.FromMinutes(Number(QuotaMinutes,1440,"每日额度")):null,
            Cycle=CycleEnabled.IsChecked==true?new(connection.IsService && !draftBase.Groups.Any(g=>g.Id==editing.Id)?latest!.EffectiveUtc:anchor,TimeSpan.FromMinutes(Number(BlockMinutes,525600,"封锁分钟",true)),TimeSpan.FromMinutes(Number(RestMinutes,525600,"休息分钟",true))):null};
        var groups=draftBase.Groups.Where(g=>g.Id!=group.Id).ToImmutableArray();
        if(!delete) groups=groups.Add(group);
        var delay=DelayUnit.SelectedIndex==0?TimeSpan.FromMinutes(Number(DelayHours,525600,"延迟分钟")):TimeSpan.FromHours(Number(DelayHours,8760,"延迟小时"));
        var policy=draftBase with {Groups=groups,ChangeDelay=delay,TimeZoneId=TimeZone.SelectedIndex==1?"UTC":"Asia/Shanghai"};
        PolicyValidation.Validate(policy);return policy;
    }
    async Task SubmitAsync(bool delete=false) {
        try {
            var policy=BuildPolicy(delete);
            if(connection.IsService && !delete && policy.Groups.Any(g=>g.Enabled && !g.ApplicationIds.IsEmpty) && MessageBox.Show(this,"封锁开始时会强制结束匹配的应用，未保存内容可能丢失。请先保存工作；首次实验建议只选择无害测试应用。\n\n提交这份完整修改？","应用封锁确认",MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK) return;
            var response=await CallAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=draftToken,Policy=policy});
            if(response.Status is {} s) ShowStatus(s);
            if(response.Ok) {
                StartDraft(latest!.Policy.Groups.FirstOrDefault(g=>g.Id==editing?.Id) ?? latest.Policy.Groups.FirstOrDefault() ?? NewGroup());
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
        var text=new System.Text.StringBuilder("冻结提交："+Local(pending.SubmittedUtc)+"\n最早生效："+Local(pending.NotBeforeUtc)+"（还需符合旧规则的解锁条件）\n新配置时区："+policy.TimeZoneId+"\n新配置后续修改延迟："+policy.ChangeDelay.TotalHours+" 小时\n");
        foreach(var g in policy.Groups) {
            text.AppendLine("\n"+g.Name+(g.Enabled?" · 启用":" · 停用"));
            static string Mode(TargetMode mode) => mode switch {TargetMode.None=>"不限制",TargetMode.BlockSelected=>"封锁列表",_=>"仅允许列表"};
            text.AppendLine("应用："+Mode(g.ApplicationMode)+" / "+string.Join("、",g.ApplicationIds.Select(id=>Entry(id).Label)));
            text.AppendLine("应用例外："+string.Join("、",g.ApplicationExceptions.Select(id=>Entry(id).Label)));
            text.AppendLine("网站："+Mode(g.WebsiteMode)+" / "+string.Join("、",g.WebsiteDomains));text.AppendLine("网站例外："+string.Join("、",g.WebsiteExceptions));
            text.AppendLine("全天封锁："+(g.AlwaysBlock?"是":"否")+"；封锁时锁定修改："+(g.LockWhileBlocking?"是":"否"));
            foreach(var w in g.Windows) {
                string[] dayNames=["一","二","三","四","五","六","日"];
                string selected=string.Join("、",Enumerable.Range(0,7).Where(i=>((int)w.Days & (1<<i))!=0).Select(i=>dayNames[i]));
                text.AppendLine($"每周 {selected} / {w.StartMinute/60:00}:{w.StartMinute%60:00}—{w.EndMinute/60:00}:{w.EndMinute%60:00}");
            }
            text.AppendLine("每日共享额度："+(g.DailyQuota is {} q?q.TotalMinutes+" 分钟":"未设置"));
            if(g.Cycle is {} c) text.AppendLine($"循环：封锁 {c.BlockFor.TotalMinutes} 分钟 / 休息 {c.RestFor.TotalMinutes} 分钟；固定起点 {Local(c.AnchorUtc)}");
        }
        var window=new Window {Owner=this,Title="冻结修改 · 只读预览",Width=720,Height=610,WindowStartupLocation=WindowStartupLocation.CenterOwner,
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
                if(PolicyJson.Fingerprint(latest!.Policy)!=fingerprint || latest.Pending is not null) {Feedback.Text="应用已登记，但后台规则发生变化，请重新读取后编辑。";return;}
                draftToken=latest.EditToken;draftBase=latest.Policy;DraftState.Text="本地草稿 · 尚未提交";
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
            var picker=new SaveFileDialog {Filter=$"预览数据 (*.{extension})|*.{extension}",FileName="专注计划-开发预览."+extension};
            if(picker.ShowDialog(this)==true) {File.WriteAllText(picker.FileName,response.ExportText,new System.Text.UTF8Encoding(true));Feedback.Text=response.Message+" 文件已导出。";}
        } catch(Exception ex) {Feedback.Text="导出失败："+ex.Message;}
    }
    async Task SelfTestAsync() {
        // Instrumented rendering of this application's own window; no OS enforcement or desktop automation.
        timer.Stop();StartDraft(NewGroup());
        string executable=Path.Combine(AppContext.BaseDirectory,"Aict.Desktop.exe");
        string identity=AuthenticodeHash.Compute(executable);
        if(identity.Length!=66 || !identity.StartsWith("0x",StringComparison.Ordinal)) throw new InvalidOperationException("Read-only PE identity failed");
        RuleName.Text="工作日 · 保持专注";Sites.Text="bilibili.com\nweibo.com";QuotaEnabled.IsChecked=true;Enabled.IsChecked=false;DelayHours.Text="24";
        _=BuildPolicy();await SubmitAsync();
        if(latest?.Policy.Groups.Length!=1) throw new InvalidOperationException("UI submission was not persisted");
        RuleName.Text="工作日 · 专注计划调整";await SubmitAsync();
        if(latest.Pending is null) throw new InvalidOperationException("UI modification was not queued");
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        string output=Path.Combine(connection.BundleRoot,"ui-self-test");Directory.CreateDirectory(output);
        void Render(string name) {
            UpdateLayout();var root=(FrameworkElement)Content;var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,name));encoder.Save(stream);
        }
        Render("preview.png");EditorScroll.ScrollToEnd();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Render("editor-schedule.png");
        var response=await CallAsync(new() {Command=PreviewCommand.ExportJson});
        if(!response.Ok || response.Status?.SystemEnforcementActive!=false) throw new InvalidOperationException("Preview self-test failed");
        File.WriteAllText(Path.Combine(output,"result.json"),PolicyJson.Serialize(new {passed=true,mode=response.Status.Mode,editorValidated=true,backendConnected=true,readOnlyPeIdentityValidated=true,uiSubmissionPersisted=true,uiModificationFrozen=true,systemEnforcementActive=false}));
        Close();
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
    static string Format(int minute) => $"{minute/60:00}:{minute%60:00}";
    static int Minute(string text,bool ending) {
        string[] parts=text.Split(':');
        if(parts.Length!=2 || !int.TryParse(parts[0],out int h) || !int.TryParse(parts[1],out int m) || h<0 || h>23+(ending?1:0) || m is <0 or >59 || (h==24 && m!=0)) throw new ArgumentException("时段使用 HH:mm；仅结束时间可填 24:00。");
        return h*60+m;
    }
    public WeeklyWindow Value() {
        int flags=0;for(int i=0;i<7;i++) if(days[i].IsChecked==true) flags|=1<<i;
        return new((Weekdays)flags,Minute(start.Text,false),Minute(end.Text,true));
    }
}
