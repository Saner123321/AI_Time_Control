using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
static class BetaSetupRepair {
    const string ScriptHash="REPAIR_SCRIPT_SHA256";
    static string Root {get{return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);}}
    [STAThread] static int Main(string[] args){
        try{using(var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))if(bios==null || !Convert.ToString(bios.GetValue("SystemManufacturer")).Contains("VMware") || !Convert.ToString(bios.GetValue("SystemProductName")).Contains("VMware"))throw new InvalidOperationException("VMware guest required；仅修复虚拟机，不修改主机。");
            Application.EnableVisualStyles();Application.Run(new RepairForm());return 0;
        }catch(Exception e){if(args.Length==1 && args[0]=="--guard-check")Console.Error.WriteLine(e.Message);else MessageBox.Show(e.Message,"专注计划 · 安装修复");return 1;}
    }
    sealed class RepairForm:Form {
        readonly Button start=new Button{Text="修复并继续安装",Width=220,Height=38};
        readonly Label status=new Label{AutoSize=false,Width=620,Height=100};
        readonly Timer timer=new Timer{Interval=1000};readonly Stopwatch clock=new Stopwatch();Process worker;
        public RepairForm(){Text="专注计划 · 安装修复";Width=700;Height=390;StartPosition=FormStartPosition.CenterScreen;Font=new System.Drawing.Font("Microsoft YaHei UI",10);var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(22)};Controls.Add(flow);flow.Controls.Add(new Label{Text="补齐安装目录，接着原来的安装继续",AutoSize=true,Font=new System.Drawing.Font(Font.FontFamily,16)});flow.Controls.Add(new Label{Width=620,Height=65,Text="对应你回传的安装报告。此修复补齐缺失目录，再运行原安装程序；不清空规则、不新建会话。请先关闭旧安装窗口。完成一次管理员确认即可。"});flow.Controls.Add(start);flow.Controls.Add(status);var reports=new Button{Text="打开修复报告位置",Width=220,Height=32};flow.Controls.Add(reports);reports.Click+=(s,e)=>Process.Start(new ProcessStartInfo("explorer.exe",Root){UseShellExecute=true});start.Click+=(s,e)=>Begin();timer.Tick+=(s,e)=>Poll();FormClosing+=(s,e)=>{if(worker!=null && !worker.HasExited){e.Cancel=true;status.Text="安装仍在处理，请保留此窗口。";}};}
        void Begin(){try{string script=Path.Combine(Root,"Repair-BetaSetup.ps1");string actual;using(var sha=SHA256.Create())using(var stream=File.OpenRead(script))actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();if(actual!=ScriptHash)throw new InvalidDataException("修复脚本校验失败，请重新完整解压修复包。");string exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");worker=Process.Start(new ProcessStartInfo(exe,"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden});start.Enabled=false;clock.Restart();timer.Start();Poll();}catch(Exception e){status.Text=e.Message;start.Enabled=true;}}
        void Poll(){try{string phase="正在检查并继续原安装";string file=Path.Combine(Root,"repair-result.json");if(File.Exists(file)){var report=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file,Encoding.UTF8)) as System.Collections.Generic.Dictionary<string,object>;string p=Convert.ToString(report["phase"]);phase=p=="checking"?"正在校验已有安装文件":p=="creating-runtime-directory"?"正在补齐目录":p=="continuing-existing-installation"?"正在继续迁移规则、启动后台并确认状态":p=="completed"?"安装已完成":p=="failed"?"本次安装未完成":"正在处理";}status.Text=phase+" · 已用 "+((int)clock.Elapsed.TotalSeconds)+" 秒。\r\n文件校验和后台确认可能需要几分钟。";if(worker.HasExited){timer.Stop();if(worker.ExitCode==0){status.Text="安装已完成。现在可从桌面“专注计划”打开；原规则保留。";start.Visible=false;}else{status.Text="安装未完成。请把本文件夹的 repair-result.json 和 setup-result-after-repair.json 回传；无需删除旧配置。";start.Enabled=true;}}}catch(IOException){}catch(Exception e){status.Text=e.Message;}}
    }
}
