using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
static class ManualViewingUpdate {
    const string ScriptHash="0000000000000000000000000000000000000000000000000000000000000000";
    static string Root {get{return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);}}
    const string InstallRoot=@"C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f";
    [STAThread] static int Main(){try{Application.EnableVisualStyles();Application.Run(new UpdateForm());return 0;}catch(Exception e){MessageBox.Show(e.Message,"专注计划 · 内置浏览器保护更新");return 1;}}
    sealed class UpdateForm:Form {
        readonly Button start=new Button{Text="更新并继续使用",Width=270,Height=40};
        readonly Button launch=new Button{Text="打开专注计划",Width=270,Height=38,Visible=false};
        readonly Label status=new Label{Width=650,Height=95};
        readonly Timer timer=new Timer{Interval=1000};readonly Stopwatch clock=new Stopwatch();Process worker;
        public UpdateForm(){Text="专注计划 · 内置浏览器保护更新";Width=740;Height=510;StartPosition=FormStartPosition.CenterScreen;Font=new System.Drawing.Font("Microsoft YaHei UI",10);var p=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(22)};Controls.Add(p);
            p.Controls.Add(new Label{Text="正式接入 ChatGPT 内置网页观看限制",AutoSize=true,Font=new System.Drawing.Font(Font.FontFamily,17)});
            p.Controls.Add(new Label{Width=650,Height=105,Text="请先退出旧界面；已有托盘图标时右键选择“退出界面”。更新保留规则、用量和待生效申请，继续持续使用。已有网站日程、例外、每日及分段额度将作用于前台可见的ChatGPT网页。后台观看组件自动运行，AI仍可访问和操作网页。请同时关闭之前的独立验证工具。浏览器组件0.5.0无需重载。\r\n点击更新后完成一次 Windows 管理员确认。"});
            p.Controls.Add(start);p.Controls.Add(status);p.Controls.Add(launch);
            var folder=new Button{Text="打开更新报告位置",Width=220,Height=32};p.Controls.Add(folder);folder.Click+=(s,e)=>Process.Start(new ProcessStartInfo("explorer.exe",Root){UseShellExecute=true});
            var ext=new Button{Text="复制新版浏览器组件路径",Width=270,Height=32};p.Controls.Add(ext);ext.Click+=(s,e)=>{Clipboard.SetText(Path.Combine(InstallRoot,"Extension"));status.Text="已复制安装目录的组件路径。已有0.5.0组件无需重载；旧目录加载的组件请改用这个安装目录。";};
            start.Click+=(s,e)=>Begin();launch.Click+=(s,e)=>{try{string app=Path.Combine(InstallRoot,@"App\Aict.Beta.Desktop.exe");Process.Start(new ProcessStartInfo(app){UseShellExecute=true,WorkingDirectory=InstallRoot});}catch(Exception ex){status.Text=ex.Message;}};
            timer.Tick+=(s,e)=>Poll();FormClosing+=(s,e)=>{if(worker!=null&&!worker.HasExited){e.Cancel=true;status.Text="更新仍在处理，请保留窗口。";}};
        }
        void Begin(){try{string script=Path.Combine(Root,"Update-ManualViewing.ps1"),hash;using(var sha=SHA256.Create())using(var stream=File.OpenRead(script))hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();if(hash!=ScriptHash)throw new InvalidDataException("更新文件校验失败，请重新完整解压更新包。");string exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");worker=Process.Start(new ProcessStartInfo(exe,"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden});start.Enabled=false;clock.Restart();timer.Start();Poll();}catch(Exception e){status.Text=e.Message;start.Enabled=true;}}
        void Poll(){try{status.Text="正在校验并更新，已用 "+(int)clock.Elapsed.TotalSeconds+" 秒。";string path=Path.Combine(Root,"update-result.json");if(File.Exists(path)){var d=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path,Encoding.UTF8)) as System.Collections.Generic.Dictionary<string,object>;status.Text+="\r\n阶段："+Convert.ToString(d["phase"]);}if(worker.HasExited){timer.Stop();if(worker.ExitCode==0){status.Text="更新完成。内置网页观看保护已接入现有规则，按日程与额度持续执行，没有60秒测试截止时间。请从开始菜单打开专注计划，查看“ChatGPT观看保护”执行状态。";start.Visible=false;launch.Visible=true;}else{status.Text="更新未完成。请提供本文件夹的 update-result.json，保留原安装和数据。";start.Enabled=true;}}}catch(IOException){}catch(Exception e){status.Text=e.Message;}}
    }
}
