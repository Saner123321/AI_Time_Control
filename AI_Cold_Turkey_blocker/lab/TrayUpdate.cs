using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
static class TrayUpdate {
    const string ScriptHash="0000000000000000000000000000000000000000000000000000000000000000";
    static string Root {get{return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);}}
    const string InstallRoot=@"C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f";
    [STAThread] static int Main(){try{Application.EnableVisualStyles();Application.Run(new UpdateForm());return 0;}catch(Exception e){MessageBox.Show(e.Message,"专注计划 · 托盘与诊断更新");return 1;}}
    sealed class UpdateForm:Form {
        readonly Button start=new Button{Text="更新并继续使用",Width=270,Height=40};
        readonly Button launch=new Button{Text="打开专注计划",Width=270,Height=38,Visible=false};
        readonly Label status=new Label{Width=650,Height=95};
        readonly Timer timer=new Timer{Interval=1000};readonly Stopwatch clock=new Stopwatch();Process worker;
        public UpdateForm(){Text="专注计划 · 托盘与诊断更新";Width=740;Height=510;StartPosition=FormStartPosition.CenterScreen;Font=new System.Drawing.Font("Microsoft YaHei UI",10);var p=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(22)};Controls.Add(p);
            p.Controls.Add(new Label{Text="关闭窗口留在托盘，右键可打开或退出界面",AutoSize=true,Font=new System.Drawing.Font(Font.FontFamily,17)});
            p.Controls.Add(new Label{Width=650,Height=105,Text="请先退出旧界面；已有托盘图标时右键选择“退出界面”。更新保留规则、用量和待生效申请，继续持续使用。新增托盘入口、重复启动唤回，并修正空应用列表引发的误扣时，将诊断序列化移到后台。浏览器组件0.4.0无需重载。\r\n点击更新后完成一次 Windows 管理员确认。"});
            p.Controls.Add(start);p.Controls.Add(status);p.Controls.Add(launch);
            var folder=new Button{Text="打开更新报告位置",Width=220,Height=32};p.Controls.Add(folder);folder.Click+=(s,e)=>Process.Start(new ProcessStartInfo("explorer.exe",Root){UseShellExecute=true});
            var ext=new Button{Text="复制新版浏览器组件路径",Width=270,Height=32};p.Controls.Add(ext);ext.Click+=(s,e)=>{Clipboard.SetText(Path.Combine(InstallRoot,"Extension"));status.Text="已复制安装目录的组件路径。已有0.4.0无需重载；旧组件请改用这个路径加载至0.4.0。";};
            start.Click+=(s,e)=>Begin();launch.Click+=(s,e)=>{try{string app=Path.Combine(InstallRoot,@"App\Aict.Beta.Desktop.exe");Process.Start(new ProcessStartInfo(app){UseShellExecute=true,WorkingDirectory=InstallRoot});}catch(Exception ex){status.Text=ex.Message;}};
            timer.Tick+=(s,e)=>Poll();FormClosing+=(s,e)=>{if(worker!=null&&!worker.HasExited){e.Cancel=true;status.Text="更新仍在处理，请保留窗口。";}};
        }
        void Begin(){try{string script=Path.Combine(Root,"Update-Tray.ps1"),hash;using(var sha=SHA256.Create())using(var stream=File.OpenRead(script))hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();if(hash!=ScriptHash)throw new InvalidDataException("更新文件校验失败，请重新完整解压更新包。");string exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");worker=Process.Start(new ProcessStartInfo(exe,"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden});start.Enabled=false;clock.Restart();timer.Start();Poll();}catch(Exception e){status.Text=e.Message;start.Enabled=true;}}
        void Poll(){try{status.Text="正在校验并更新，已用 "+(int)clock.Elapsed.TotalSeconds+" 秒。";string path=Path.Combine(Root,"update-result.json");if(File.Exists(path)){var d=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path,Encoding.UTF8)) as System.Collections.Generic.Dictionary<string,object>;status.Text+="\r\n阶段："+Convert.ToString(d["phase"]);}if(worker.HasExited){timer.Stop();if(worker.ExitCode==0){status.Text="更新完成。打开专注计划后，叉号会隐藏到托盘；双击托盘图标或桌面入口可恢复，右键“退出界面”才退出界面。后台封锁不受退出界面影响。";start.Visible=false;launch.Visible=true;}else{status.Text="更新未完成。请提供本文件夹的 update-result.json，保留原安装和数据。";start.Enabled=true;}}}catch(IOException){}catch(Exception e){status.Text=e.Message;}}
    }
}
