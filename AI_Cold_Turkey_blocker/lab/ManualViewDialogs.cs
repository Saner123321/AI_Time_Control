using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

internal sealed class ReportPathDialog:Form {
    internal bool Copied {get;private set;}
    internal ReportPathDialog(string folder){
        Text="验证结果已导出";Width=700;Height=225;StartPosition=FormStartPosition.CenterParent;Font=new Font("Microsoft YaHei UI",10);MinimizeBox=false;MaximizeBox=false;
        Controls.Add(new Label{Text="可以直接复制文件夹路径发到聊天里：",Left=20,Top=20,Width=640,Height=25});
        var path=new TextBox{Text=folder,ReadOnly=true,Left=20,Top=55,Width=640};Controls.Add(path);
        var copy=new Button{Text="复制报告路径",Left=20,Top=100,Width=180,Height=38};Controls.Add(copy);
        var open=new Button{Text="打开报告文件夹",Left=215,Top=100,Width=180,Height=38};Controls.Add(open);
        copy.Click+=(s,e)=>{try{Clipboard.SetText(folder);Copied=true;copy.Text="已复制";}catch(Exception error){MessageBox.Show(this,error.Message,"复制未完成");}};
        open.Click+=(s,e)=>{try{Process.Start(new ProcessStartInfo("explorer.exe","\""+folder+"\""){UseShellExecute=true});}catch(Exception error){MessageBox.Show(this,error.Message,"未打开");}};
        Shown+=(s,e)=>{path.Focus();path.SelectAll();};
    }
}
internal sealed class RegionSelectionDialog:Form {
    Point start,end;bool dragging;
    internal Rectangle Selected {get;private set;}
    internal RegionSelectionDialog(){
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;Bounds=SystemInformation.VirtualScreen;
        BackColor=Color.Black;Opacity=0.38;Cursor=Cursors.Cross;DoubleBuffered=true;KeyPreview=true;
        KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}};
        MouseDown+=(s,e)=>{if(e.Button!=MouseButtons.Left){DialogResult=DialogResult.Cancel;Close();return;}start=end=e.Location;dragging=true;Invalidate();};
        MouseMove+=(s,e)=>{if(dragging){end=e.Location;Invalidate();}};
        MouseUp+=(s,e)=>{if(!dragging)return;end=e.Location;var box=Selection();if(box.Width<160||box.Height<100){dragging=false;Invalidate();return;}Selected=new Rectangle(box.X+Left,box.Y+Top,box.Width,box.Height);DialogResult=DialogResult.OK;Close();};
    }
    Rectangle Selection(){return Rectangle.FromLTRB(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),Math.Max(start.X,end.X),Math.Max(start.Y,end.Y));}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var font=new Font("Microsoft YaHei UI",16))using(var pen=new Pen(Color.Cyan,3)){
        TextRenderer.DrawText(e.Graphics,"仅框选 ChatGPT 网页区域，勿框聊天栏 · Esc取消 · 只验证遮挡，不确认网址",font,new Rectangle(30,25,Math.Min(1100,Width-60),80),Color.White,TextFormatFlags.WordBreak);
        if(dragging)e.Graphics.DrawRectangle(pen,Selection());
    }}
}
