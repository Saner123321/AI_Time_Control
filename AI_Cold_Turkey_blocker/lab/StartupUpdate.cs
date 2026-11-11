using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
static class StartupUpdate {
    const string ScriptHash="0000000000000000000000000000000000000000000000000000000000000000";
    const string InstallRoot=@"C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f";
    static string Root {get{return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);}}
    [STAThread] static int Main(){try{Application.EnableVisualStyles();Application.Run(new UpdateForm());return 0;}catch(Exception e){MessageBox.Show(e.Message,"专注计划 · 开机自启动更新");return 1;}}
    sealed class UpdateForm:Form {
        readonly Button start=new Button{Text="更新并开启自启动",Width=270,Height=40};
        readonly Button launch=new Button{Text="打开专注计划",Width=270,Height=38,Visible=false};
        readonly Label status=new Label{Width=650,Height=100};
        readonly Timer timer=new Timer{Interval=1000};readonly Stopwatch clock=new Stopwatch();Process worker;
        public UpdateForm(){
            Text="专注计划 · 开机自启动更新";Width=740;Height=470;StartPosition=FormStartPosition.CenterScreen;Font=new System.Drawing.Font("Microsoft YaHei UI",10);
            var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(22)};Controls.Add(panel);
            panel.Controls.Add(new Label{Text="登录后自动驻留托盘",AutoSize=true,Font=new System.Drawing.Font(Font.FontFamily,17)});
            panel.Controls.Add(new Label{Width=650,Height=95,Text="先在专注计划托盘图标上右键选择“退出界面”。更新保留规则、额度、待生效修改和内置网页观看保护，浏览器组件无需重载。\r\n更新后默认开启界面自启动，下次登录 Windows 自动显示右下角图标，不弹出主窗口。可在“软件设置”关闭，后台封锁始终独立运行。\r\n点击后完成一次 Windows 管理员确认。"});
            panel.Controls.Add(start);panel.Controls.Add(status);panel.Controls.Add(launch);
            var copy=new Button{Text="复制更新报告路径",Width=220,Height=32};panel.Controls.Add(copy);copy.Click+=(s,e)=>{Clipboard.SetText(Path.Combine(Root,"update-result.json"));status.Text="更新报告路径已复制。";};
            start.Click+=(s,e)=>Begin();launch.Click+=(s,e)=>{try{Process.Start(new ProcessStartInfo(Path.Combine(InstallRoot,@"App\Aict.Beta.Desktop.exe")){UseShellExecute=true,WorkingDirectory=InstallRoot});}catch(Exception error){status.Text=error.Message;}};
            timer.Tick+=(s,e)=>Poll();FormClosing+=(s,e)=>{if(worker!=null&&!worker.HasExited){e.Cancel=true;status.Text="更新仍在处理，请保留窗口。";}};
        }
        void Begin(){try{
            string script=Path.Combine(Root,"Update-Startup.ps1"),hash;using(var sha=SHA256.Create())using(var stream=File.OpenRead(script))hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
            if(hash!=ScriptHash)throw new InvalidDataException("更新文件校验失败，请重新完整解压更新包。");
            string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
            worker=Process.Start(new ProcessStartInfo(powershell,"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden});start.Enabled=false;clock.Restart();timer.Start();Poll();
        }catch(Exception error){status.Text=error.Message;start.Enabled=true;}}
        void Poll(){try{
            status.Text="正在校验、更新并设置自启动，已用 "+(int)clock.Elapsed.TotalSeconds+" 秒。";
            if(worker.HasExited){timer.Stop();if(worker.ExitCode==0){
                var result=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(Root,"update-result.json"),Encoding.UTF8)) as System.Collections.Generic.Dictionary<string,object>;
                var startup=result["desktopStartup"] as System.Collections.Generic.Dictionary<string,object>;
                status.Text=Convert.ToBoolean(startup["registered"])?"更新完成，已开启开机自启动。下次登录后自动驻留托盘。现在可打开专注计划，在“软件设置”查看或关闭。":"更新完成，保留了你之前关闭界面自启动的选择。可打开专注计划，在“软件设置”重新开启。";
                start.Visible=false;launch.Visible=true;
            }else{status.Text="更新未完成，请复制更新报告路径并发给我。保留原安装和数据。";start.Enabled=true;}}
        }catch(IOException){}catch(Exception error){status.Text=error.Message;}}
    }
}
