using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Aict.Startup;
namespace Aict.Beta.Desktop;
internal sealed class SettingsWindow:Window {
    internal SettingsWindow(BetaConnection connection){
        Title="专注计划 · 软件设置";var panel=Ui.Layout(this,"启动与界面");Height=380;MinHeight=300;
        var checkbox=new CheckBox{Content="开机自启动（登录 Windows 后驻留托盘）",Margin=new Thickness(0,12,0,8),IsEnabled=connection.IsService};panel.Children.Add(checkbox);
        Ui.Text(panel,"自启动时不弹出主窗口。双击右下角图标或从开始菜单打开即可查看规则。关闭窗口仍保留托盘，右键图标可退出界面。");
        Ui.Text(panel,"这项设置只控制界面；后台封锁、使用记录采集和观看保护会继续自动运行。");
        var status=Ui.Text(panel,"");
        if(!connection.IsService){status.Text="界面预览中不修改开机启动设置。";return;}
        LoginStartup startup;string sid=WindowsIdentity.GetCurrent().User!.Value;
        try{startup=new(connection.Root!);using var store=startup.Open(sid);checkbox.IsChecked=startup.Enabled(store);}catch(Exception e){checkbox.IsEnabled=false;status.Text="无法读取自启动设置："+e.Message;return;}
        checkbox.Click+=(_,_)=>{try{using var store=startup.Open(sid);startup.Set(store,checkbox.IsChecked==true);status.Text=checkbox.IsChecked==true?"已开启：下次登录后自动显示托盘图标。":"已关闭界面自启动，后台限制继续执行。";}catch(Exception e){try{using var store=startup.Open(sid);checkbox.IsChecked=startup.Enabled(store);}catch{checkbox.IsEnabled=false;}status.Text="设置未完成："+e.Message;}};
    }
}
