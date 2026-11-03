using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
static class Entry {
    [STAThread] static int Main(){try{string root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);bool recovery=Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location).Contains("恢复");string exe=Path.Combine(root,@"App\Aict.Beta.Desktop.exe");if(!File.Exists(exe))throw new IOException("请先完整解压安装包，再双击入口。");Process.Start(new ProcessStartInfo(exe,recovery?"--recovery":""){UseShellExecute=true,WorkingDirectory=root});return 0;}catch(Exception e){MessageBox.Show(e.Message,"专注计划");return 1;}}
}
