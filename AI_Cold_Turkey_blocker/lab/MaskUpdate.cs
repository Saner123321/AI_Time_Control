using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
static class MaskUpdate {
    const string ScriptHash="0000000000000000000000000000000000000000000000000000000000000000";
    const string InstallRoot=@"C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f";
    static string Root {get{return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);}}
    [STAThread] static int Main(){try{Application.EnableVisualStyles();Application.Run(new UpdateForm());return 0;}catch(Exception e){MessageBox.Show(e.Message,"专注计划 · 网页遮挡修复");return 1;}}
    sealed class UpdateForm:Form {
        readonly Button start=new Button{Text="更新并修复遮挡",Width=270,Height=40};
        readonly Button launch=new Button{Text="打开专注计划",Width=270,Height=38,Visible=false};
        readonly Label status=new Label{Width=650,Height=100};
        readonly Timer timer=new Timer{Interval=1000};readonly Stopwatch clock=new Stopwatch();Process worker;
        public UpdateForm(){
            Text="专注计划 · 网页遮挡修复";Width=740;Height=470;StartPosition=FormStartPosition.CenterScreen;Font=new System.Drawing.Font("Microsoft YaHei UI",10);
            var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(22)};Controls.Add(panel);
            panel.Controls.Add(new Label{Text="修复网页未被实际遮挡的问题",AutoSize=true,Font=new System.Drawing.Font(Font.FontFamily,17)});
            panel.Controls.Add(new Label{Width=650,Height=95,Text="先在专注计划托盘图标上右键选择“退出界面”。更新保留规则、额度、待生效修改和内置网页观看保护，浏览器组件无需重载。\r\n修复遮挡窗口失去实际置顶却仍显示已执行的问题。显示时检查真实可见、置顶和位置，仅在不满足时恢复；保留原规则与自启动选择，不改变例外匹配方式。\r\n点击后完成一次 Windows 管理员确认。"});
            panel.Controls.Add(start);panel.Controls.Add(status);panel.Controls.Add(launch);
            var copy=new Button{Text="复制更新报告路径",Width=220,Height=32};panel.Controls.Add(copy);copy.Click+=(s,e)=>{Clipboard.SetText(Path.Combine(Root,"update-result.json"));status.Text="更新报告路径已复制。";};
            start.Click+=(s,e)=>Begin();launch.Click+=(s,e)=>{try{Process.Start(new ProcessStartInfo(Path.Combine(InstallRoot,@"App\Aict.Beta.Desktop.exe")){UseShellExecute=true,WorkingDirectory=InstallRoot});}catch(Exception error){status.Text=error.Message;}};
            timer.Tick+=(s,e)=>Poll();FormClosing+=(s,e)=>{if(worker!=null&&!worker.HasExited){e.Cancel=true;status.Text="更新仍在处理，请保留窗口。";}};
        }
        void Begin(){try{
            string script=Path.Combine(Root,"Update-Mask.ps1"),hash;using(var sha=SHA256.Create())using(var stream=File.OpenRead(script))hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
            if(hash!=ScriptHash)throw new InvalidDataException("更新文件校验失败，请重新完整解压更新包。");
            string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
            worker=Process.Start(new ProcessStartInfo(powershell,"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden});start.Enabled=false;clock.Restart();timer.Start();Poll();
        }catch(Exception error){status.Text=error.Message;start.Enabled=true;}}
        void Poll(){try{
            status.Text="正在校验并更新遮挡执行，已用 "+(int)clock.Elapsed.TotalSeconds+" 秒。";
            if(worker.HasExited){timer.Stop();if(worker.ExitCode==0){
                var result=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(Root,"update-result.json"),Encoding.UTF8)) as System.Collections.Generic.Dictionary<string,object>;
                var startup=result["desktopStartup"] as System.Collections.Generic.Dictionary<string,object>;
                status.Text="更新完成。已启用实际窗口可见与置顶检查；原规则、额度和自启动选择保留。无需重启电脑或ChatGPT，可打开专注计划继续使用。";
                start.Visible=false;launch.Visible=true;
            }else{status.Text="更新未完成，请复制更新报告路径并发给我。保留原安装和数据。";start.Enabled=true;}}
        }catch(IOException){}catch(Exception error){status.Text=error.Message;}}
    }
}
