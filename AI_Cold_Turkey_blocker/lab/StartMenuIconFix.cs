using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Drawing;
static class StartMenuIconFix {
    const string InstalledRoot=@"C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f";
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern void SHChangeNotify(uint kind,uint flags,string path,IntPtr unused);
    static byte[] IconBytes(){using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("AICT.FocusIcon")){if(s==null)throw new IOException("图标资源缺失。");using(var m=new MemoryStream()){s.CopyTo(m);return m.ToArray();}}}
    static string Digest(byte[] b){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
    static bool EqualPath(string a,string b){return Path.GetFullPath(a).Equals(Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);}
    static string Apply(string programs,string store,string target,bool notify){
        if(!File.Exists(target))throw new IOException("没有找到已安装的专注计划，请先完成原程序安装。");
        Directory.CreateDirectory(programs);Directory.CreateDirectory(store);string link=Path.Combine(programs,"专注计划.lnk");
        object shellObject=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shell=shellObject;object shortcutObject=null;
        try{
            shortcutObject=shell.CreateShortcut(link);dynamic shortcut=shortcutObject;
            if(File.Exists(link) && !EqualPath((string)shortcut.TargetPath,target))throw new IOException("开始菜单中存在同名但指向其他程序的快捷方式，已保留，请先手动改名。");
            byte[] image=IconBytes();string icon=Path.Combine(store,"focus-"+Digest(image).Substring(0,20)+".ico");
            if(File.Exists(icon)){if(Digest(File.ReadAllBytes(icon))!=Digest(image))throw new IOException("已有图标文件内容异常，未覆盖。");}else File.WriteAllBytes(icon,image);
            bool existing=File.Exists(link);
            if(!existing){shortcut.TargetPath=target;shortcut.WorkingDirectory=Path.GetDirectoryName(Path.GetDirectoryName(target));shortcut.Description="专注计划：应用与网站规则、日程、额度和锁定";}
            shortcut.IconLocation=icon+",0";shortcut.Save();
            if(notify)SHChangeNotify(0x00002000,0x0005,link,IntPtr.Zero);
            return link;
        }finally{if(shortcutObject!=null)Marshal.FinalReleaseComObject(shortcutObject);Marshal.FinalReleaseComObject(shellObject);}
    }
    static void SelfTest(string root){
        root=Path.GetFullPath(root);Directory.CreateDirectory(root);string programs=Path.Combine(root,"Programs"),store=Path.Combine(root,"icons"),target=Path.Combine(root,"App","Aict.Beta.Desktop.exe");Directory.CreateDirectory(Path.GetDirectoryName(target));File.WriteAllText(target,"inert link fixture; never executed");
        string link=Apply(programs,store,target,false);object so=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shell=so;object lo=null;
        try{
            lo=shell.CreateShortcut(link);dynamic shortcut=lo;shortcut.Arguments="--uninstall";shortcut.Save();Marshal.FinalReleaseComObject(lo);lo=null;
            Apply(programs,store,target,false);lo=shell.CreateShortcut(link);shortcut=lo;
            if(!EqualPath((string)shortcut.TargetPath,target)||(string)shortcut.Arguments!="--uninstall")throw new Exception("Existing shortcut metadata was not preserved");
            string location=(string)shortcut.IconLocation;string icon=location.Substring(0,location.LastIndexOf(','));if(Digest(File.ReadAllBytes(icon))!=Digest(IconBytes()))throw new Exception("Icon content mismatch");using(var image=new Icon(icon,48,48))if(image.Width!=48)throw new Exception("Icon size invalid");
            shortcut.TargetPath=Path.Combine(root,"Unrelated.exe");shortcut.Save();Marshal.FinalReleaseComObject(lo);lo=null;byte[] before=File.ReadAllBytes(link);bool refused=false;try{Apply(programs,store,target,false);}catch(IOException){refused=true;}
            if(!refused||Digest(File.ReadAllBytes(link))!=Digest(before))throw new Exception("Unrelated shortcut changed");
            File.WriteAllText(Path.Combine(root,"result.json"),"{\"passed\":true,\"createdStartMenuFixture\":true,\"preservedExistingTargetAndArguments\":true,\"unrelatedShortcutUntouched\":true,\"embeddedIconVerified\":true,\"realStartMenuOrDesktopChanges\":false}");
        }finally{if(lo!=null)Marshal.FinalReleaseComObject(lo);Marshal.FinalReleaseComObject(so);}
    }
    [STAThread] static int Main(string[] args){
        try{
            if(args.Length==2&&args[0]=="--self-test"){SelfTest(args[1]);return 0;}
            Application.EnableVisualStyles();using(var form=new Form{Text="专注计划 · 开始菜单图标",Width=600,Height=295,StartPosition=FormStartPosition.CenterScreen,Font=new Font("Microsoft YaHei UI",10)}){
                using(var stream=new MemoryStream(IconBytes()))form.Icon=new Icon(stream);
                var text=new Label{Left=24,Top=22,Width=535,Height=115,Text="给开始菜单中的“专注计划”添加青色时钟图标。\r\n保留现有入口的目标和参数；缺少入口时创建一个。\r\n不创建桌面快捷方式，不改规则，也不需要关闭软件或管理员确认。"};
                var button=new Button{Left=24,Top=145,Width=240,Height=42,Text="添加开始菜单图标"};form.Controls.Add(text);form.Controls.Add(button);
                button.Click+=(s,e)=>{try{Apply(Environment.GetFolderPath(Environment.SpecialFolder.Programs),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AICT","Assets"),Path.Combine(InstalledRoot,@"App\Aict.Beta.Desktop.exe"),true);text.Text="已添加开始菜单图标。\r\n如果固定区域仍显示旧图标，请取消固定，再从开始菜单的应用列表重新固定一次。\r\n更新包删除后，图标仍保留。";button.Enabled=false;}catch(Exception error){MessageBox.Show(form,error.Message,"未完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}};
                Application.Run(form);
            }return 0;
        }catch(Exception e){if(args.Length>0)return 1;MessageBox.Show(e.Message,"专注计划 · 开始菜单图标");return 1;}
    }
}
