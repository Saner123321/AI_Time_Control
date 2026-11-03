using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Aict.Core;
namespace Aict.Beta.Desktop;

// A local draft editor. Only the main window submits changes to the protected backend.
internal sealed class ExceptionWindow:Window {
    readonly ObservableCollection<Row> rows=[];readonly DataGrid table=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Extended,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeight=38,BorderThickness=new(0),GridLinesVisibility=DataGridGridLinesVisibility.Horizontal};
    readonly TextBox search=new(){MinWidth=260};readonly ComboBox groupFilter=new(){MinWidth=150};readonly TextBlock count=new(),message=new(){TextWrapping=TextWrapping.Wrap};
    readonly ICollectionView view;
    public ImmutableArray<WebsiteExceptionDetail> Result {get;private set;}=[];
    internal sealed record Row(string Pattern,string Note,string Group){public string Category=>Group.Length==0?"未分组":Group;public string DisplayNote=>Note.Length==0?"未添加备注":Note;}
    public ExceptionWindow(ImmutableArray<WebsiteExceptionDetail> initial) {
        Title="网站例外 · 备注与分组";Width=1000;Height=700;MinWidth=820;MinHeight=540;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=System.Windows.Media.Brushes.White;FontFamily=new("Microsoft YaHei UI");
        foreach(var e in initial)rows.Add(new(e.Pattern,e.Note,e.Group));
        view=CollectionViewSource.GetDefaultView(rows);view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Category)));view.SortDescriptions.Add(new(nameof(Row.Category),ListSortDirection.Ascending));
        view.Filter=o=>o is Row r && (groupFilter.SelectedIndex<=0 || r.Category==groupFilter.SelectedItem?.ToString()) && (search.Text.Length==0 || (r.Pattern+"\n"+r.Note+"\n"+r.Category).Contains(search.Text,StringComparison.OrdinalIgnoreCase));
        var p=new DockPanel{Margin=new(24)};Content=p;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);p.Children.Add(top);
        top.Children.Add(new TextBlock{Text="给每个例外一个明确的用途",FontSize=24,FontWeight=FontWeights.SemiBold});
        top.Children.Add(new TextBlock{Text="备注和分组用于整理，不改变放行范围。完成后回到主窗口提交；新增或修改网址仍按原来的等待时间生效。",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,12),Foreground=System.Windows.Media.Brushes.SlateGray});
        var filters=new DockPanel();var searchLabel=new TextBlock{Text="搜索网址 / 备注 / 分组",VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,10,0)};filters.Children.Add(searchLabel);filters.Children.Add(search);filters.Children.Add(new TextBlock{Text="分组",Margin=new(16,0,8,0),VerticalAlignment=VerticalAlignment.Center});filters.Children.Add(groupFilter);top.Children.Add(filters);
        var bar=new WrapPanel();top.Children.Add(bar);AddButton(bar,"＋ 添加例外",()=>Edit(null));AddButton(bar,"编辑选中项",()=>{if(table.SelectedItem is Row r)Edit(r);else message.Text="请先选择一条例外。";});AddButton(bar,"批量粘贴…",Bulk);AddButton(bar,"移动到分组…",MoveGroup);AddButton(bar,"移除选中项",()=>{var selected=table.SelectedItems.Cast<Row>().ToArray();foreach(var r in selected)rows.Remove(r);Refresh();message.Text="已从草稿移除 "+selected.Length+" 条，提交前不会改变后台规则。";});
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);p.Children.Add(bottom);bottom.Children.Add(count);bottom.Children.Add(message);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};bottom.Children.Add(actions);AddButton(actions,"取消",()=>{DialogResult=false;});AddButton(actions,"完成编辑，回到规则草稿",()=>{Result=rows.Select(r=>new WebsiteExceptionDetail(r.Pattern,r.Note,r.Group)).ToImmutableArray();ExceptionCatalog.ValidateEntries(Result);DialogResult=true;});
        var urlStyle=new Style(typeof(TextBlock));urlStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(nameof(Row.Pattern))));
        foreach(var col in new[]{new DataGridTextColumn{Header="分组",Binding=new Binding(nameof(Row.Category)),Width=145},new DataGridTextColumn{Header="备注 / 用途",Binding=new Binding(nameof(Row.DisplayNote)),Width=230},new DataGridTextColumn{Header="网址或通配符",Binding=new Binding(nameof(Row.Pattern)),Width=490,ElementStyle=urlStyle}})table.Columns.Add(col);
        var header=new FrameworkElementFactory(typeof(TextBlock));header.SetBinding(TextBlock.TextProperty,new Binding("Name"));header.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);header.SetValue(TextBlock.MarginProperty,new Thickness(8,10,0,5));table.GroupStyle.Add(new(){HeaderTemplate=new DataTemplate{VisualTree=header}});
        table.ItemsSource=view;table.MouseDoubleClick+=(_,_)=>{if(table.SelectedItem is Row r)Edit(r);};table.KeyDown+=(_,e)=>{if(e.Key==Key.Enter && table.SelectedItem is Row r){Edit(r);e.Handled=true;}};p.Children.Add(table);
        search.TextChanged+=(_,_)=>Refresh(false);groupFilter.SelectionChanged+=(_,_)=>Refresh(false);Refresh();
    }
    static Button AddButton(Panel p,string text,Action action){var b=new Button{Content=text,Padding=new(12,6,12,6),Margin=new(0,7,8,7)};b.Click+=(_,_)=>{try{action();}catch(ArgumentException e){MessageBox.Show(e.Message,"网站例外",MessageBoxButton.OK,MessageBoxImage.Information);}};p.Children.Add(b);return b;}
    void Refresh(bool groups=true){if(groups){string selected=groupFilter.SelectedItem?.ToString()??"全部分组";groupFilter.ItemsSource=new[]{"全部分组"}.Concat(rows.Select(r=>r.Category).Distinct().Order()).ToArray();groupFilter.SelectedItem=selected;if(groupFilter.SelectedIndex<0)groupFilter.SelectedIndex=0;}view.Refresh();count.Text="共 "+rows.Count+" 条例外 · 当前显示 "+rows.Count(r=>view.Filter(r))+" 条";}
    IEnumerable<string> Groups()=>rows.Where(r=>r.Group.Length>0).Select(r=>r.Group).Distinct().Order();
    void Edit(Row? original){var d=new ExceptionEditWindow(original,Groups()){Owner=this};if(d.ShowDialog()!=true)return;var row=d.Result!;if(rows.Any(r=>r!=original && WebsitePattern.Normalize(r.Pattern).Equals(row.Pattern,StringComparison.OrdinalIgnoreCase))){message.Text="这个网址已在列表中，请编辑原条目的备注或分组。";return;}if(original is null)rows.Add(row);else rows[rows.IndexOf(original)]=row;Refresh();table.SelectedItem=row;table.ScrollIntoView(row);message.Text="更改已留在草稿，点击完成后回主窗口提交。";}
    void Bulk(){var d=new ExceptionEditWindow(null,Groups(),true){Owner=this};if(d.ShowDialog()!=true)return;var seen=rows.Select(r=>WebsitePattern.Normalize(r.Pattern)).ToHashSet(StringComparer.OrdinalIgnoreCase);int added=0,duplicates=0;foreach(var row in d.BulkRows){if(seen.Add(row.Pattern)){rows.Add(row);added++;}else duplicates++;}Refresh();message.Text="已加入 "+added+" 条，跳过 "+duplicates+" 条重复网址；原有备注保留。";}
    void MoveGroup(){var selected=table.SelectedItems.Cast<Row>().ToArray();if(selected.Length==0){message.Text="请先选择条目，可用 Ctrl / Shift 多选。";return;}var d=new GroupNameWindow(Groups()){Owner=this};if(d.ShowDialog()!=true)return;foreach(var row in selected)rows[rows.IndexOf(row)]=row with{Group=d.Value};Refresh();message.Text="已调整 "+selected.Length+" 条例外的分组。";}
    internal void TestSearch(string text){search.Text=text;}
    internal int VisibleCount=>rows.Count(r=>view.Filter(r));
}
internal sealed class GroupNameWindow:Window {
    readonly ComboBox name=new(){IsEditable=true};public string Value=>name.Text.Trim();
    public GroupNameWindow(IEnumerable<string> groups){Title="调整分组";var p=Ui.Layout(this,"选择或新建分组");Height=330;Ui.Text(p,"输入新分组名称，或从已有分组中选择；留空表示未分组。");name.ItemsSource=groups.ToArray();p.Children.Add(name);Ui.Button(p,"应用到选中条目",()=>{if(Value.Length>60)throw new ArgumentException("分组最多60字。");DialogResult=true;});}
}
internal sealed class ExceptionEditWindow:Window {
    readonly TextBox pattern=new(),note=new(){MaxLength=500};readonly ComboBox group=new(){IsEditable=true};readonly TextBlock error=new(){TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.Firebrick};
    public ExceptionWindow.Row? Result{get;private set;}public ImmutableArray<ExceptionWindow.Row> BulkRows{get;private set;}=[];
    public ExceptionEditWindow(ExceptionWindow.Row? row,IEnumerable<string> groups,bool bulk=false){Title=bulk?"批量添加例外":row is null?"添加例外":"编辑例外";var p=Ui.Layout(this,Title);Height=bulk?670:580;
        Ui.Text(p,bulk?"每行一条网址或通配符。下方备注和分组会应用到本次新增条目，重复网址保留原备注。":"填写你想放行的具体页面或网站，并注明用途。");p.Children.Add(new TextBlock{Text=bulk?"网址列表":"网址 / 通配符"});pattern.Text=row?.Pattern??"";pattern.AcceptsReturn=bulk;pattern.Height=bulk?170:32;pattern.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;p.Children.Add(pattern);
        Ui.Text(p,"具体页面须包含实际主机名：www.bilibili.com 与 bilibili.com 不同。普通页面链接忽略 ? 后的参数。");p.Children.Add(new TextBlock{Text="备注 / 用途（可选，例如：英语课程第3讲）"});note.Text=row?.Note??"";p.Children.Add(note);p.Children.Add(new TextBlock{Text="分组（可选，可直接输入新名称）"});group.ItemsSource=groups.ToArray();group.Text=row?.Group??"";p.Children.Add(group);p.Children.Add(error);
        Ui.Button(p,bulk?"加入草稿":"保存这条到草稿",()=>{try{var source=pattern.Text.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);if(source.Length==0 || !bulk && source.Length!=1)throw new ArgumentException("请填写网址，每行一条。");var entries=source.Select(s=>new WebsiteExceptionDetail(WebsitePattern.Normalize(s),note.Text.Trim(),group.Text.Trim())).DistinctBy(e=>e.Pattern).ToImmutableArray();ExceptionCatalog.ValidateEntries(entries);BulkRows=entries.Select(e=>new ExceptionWindow.Row(e.Pattern,e.Note,e.Group)).ToImmutableArray();Result=BulkRows[0];DialogResult=true;}catch(ArgumentException e){error.Text=e.Message;}});
    }
}
