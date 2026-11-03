using System;
using System.Reflection;
using System.Windows.Forms;
[assembly:AssemblyTitle("AICT 无害测试应用")]
[assembly:AssemblyDescription("仅用于验证应用封锁，没有文件编辑或个人数据")]
static class InteractiveDemo {
    [STAThread] static void Main(){
        Application.EnableVisualStyles();var form=new Form {Text="AICT 无害测试应用",Width=500,Height=240,StartPosition=FormStartPosition.CenterScreen};
        var text=new Label {Dock=DockStyle.Fill,TextAlign=System.Drawing.ContentAlignment.MiddleCenter,Font=new System.Drawing.Font("Microsoft YaHei UI",13)};
        form.Controls.Add(text);int elapsed=0;var timer=new Timer {Interval=1000};timer.Tick+=delegate{elapsed++;text.Text="测试应用正在运行："+elapsed+" 秒\r\n\r\n没有待保存内容，可以安全结束。";};timer.Start();Application.Run(form);
    }
}
