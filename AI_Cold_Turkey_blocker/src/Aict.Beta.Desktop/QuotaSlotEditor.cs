using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Aict.Core;
namespace Aict.Beta.Desktop;
internal sealed class QuotaSlotEditor {
    public Guid Id{get;}public bool Legacy{get;}public Grid Root{get;}=new(){Margin=new(0,2,0,6)};
    readonly TextBox name=new(),start=new(),end=new(),minutes=new();readonly TextBlock usage=new(){Foreground=System.Windows.Media.Brushes.SlateGray,FontSize=11};
    public Button Remove{get;}=new(){Content="删除",Padding=new(8,5,8,5),Margin=new(4)};
    public QuotaSlotEditor(QuotaSlot slot,bool legacy=false){Id=slot.Id;Legacy=legacy;foreach(var width in new[]{new GridLength(1,GridUnitType.Star),new(80),new(80),new(90),new(60)})Root.ColumnDefinitions.Add(new(){Width=width});Root.RowDefinitions.Add(new(){Height=GridLength.Auto});Root.RowDefinitions.Add(new(){Height=GridLength.Auto});name.Text=slot.Name;name.MaxLength=60;start.Text=WindowEditor.Format(slot.StartMinute);end.Text=WindowEditor.Format(slot.EndMinute);minutes.Text=slot.Limit.TotalMinutes.ToString(CultureInfo.InvariantCulture);FrameworkElement[] fields=[name,start,end,minutes,Remove];for(int i=0;i<fields.Length;i++){Grid.SetColumn(fields[i],i);fields[i].Margin=new(0,3,6,3);Root.Children.Add(fields[i]);}Grid.SetRow(usage,1);Grid.SetColumnSpan(usage,5);Root.Children.Add(usage);}
    public QuotaSlot Value(){if(!double.TryParse(minutes.Text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double n)||!double.IsFinite(n)||n<0||n>1440)throw new ArgumentException("每个时段的限额须为0—1440分钟。");return new(Id,WindowEditor.Minute(start.Text,false),WindowEditor.Minute(end.Text,true),TimeSpan.FromMinutes(n),name.Text.Trim());}
    public void ShowUsage(Aict.Beta.UsageRow? row){usage.Text=row is null?"新时段 · 用量将在提交生效后单独统计":(row.QuotaWindowActive?"当前时段 · ":"本周期 · ")+$"已用 {TimeSpan.FromTicks(row.UsedTicks).TotalMinutes:0.#} / {TimeSpan.FromTicks(row.LimitTicks??0).TotalMinutes:0.#} 分钟"+(row.HasGap?" · 存在采集缺口":"");}
}
