using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DiskGuide
{
    internal sealed partial class Shell
    {
        private void ApplyKeepFlags()
        {
            foreach(var row in rows)
            {
                foreach(var file in row.Files)file.Preserved=keep.Contains(file.Path);
                row.Pick=row.Pick;
            }
            BindClean();
            if(folderGrid!=null){folderGrid.Items.Refresh();RenderDetail();}
        }
        private bool SaveKeep(string path,bool directory,Window owner=null)
        {
            try
            {
                preserveStore.Add(path,directory);keep=preserveStore.Load();ApplyKeepFlags();
                Status(keep.Error??"已添加永久保留规则，可在「保留规则」中撤销。");return true;
            }
            catch(Exception ex){MessageBox.Show(owner??View,"未能保存规则："+ex.GetBaseException().Message,"保留规则",MessageBoxButton.OK,MessageBoxImage.Warning);return false;}
        }
        private void ShowKeepRules()
        {
            if(busy)return;
            var dialog=DetailWindow("永久保留规则",1000,650);
            var body=new Grid{Margin=new Thickness(20)};
            foreach(var h in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})body.RowDefinitions.Add(new RowDefinition{Height=h});
            var header=new StackPanel();header.Children.Add(new TextBlock{Text="这些位置，清理时始终保留",FontSize=22,FontWeight=FontWeights.SemiBold});
            header.Children.Add(new TextBlock{Text="规则跨会话保存。目录规则包含全部后代；移除规则不会立即删除文件。\n适用于缓存清理和本工具的残留回收，不能约束软件自身的官方卸载器。",Style=(Style)View.FindResource("Muted"),TextWrapping=TextWrapping.Wrap,LineHeight=19,Margin=new Thickness(0,7,0,16)});body.Children.Add(header);
            var toolbar=new DockPanel{Margin=new Thickness(0,0,0,10)};
            var add=new Button{Content="添加保留",Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(add,Dock.Right);toolbar.Children.Add(add);
            var kind=new ComboBox{Width=140,SelectedIndex=0,Margin=new Thickness(8,0,0,0),VerticalContentAlignment=VerticalAlignment.Center};kind.Items.Add("目录及其内容");kind.Items.Add("单个文件");DockPanel.SetDock(kind,Dock.Right);toolbar.Children.Add(kind);
            var pathLabel=new TextBlock{Text="完整路径",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,9,0)};DockPanel.SetDock(pathLabel,Dock.Left);toolbar.Children.Add(pathLabel);
            var input=new TextBox{Style=(Style)View.FindResource("SearchField"),ToolTip="输入 C 或 D 盘的完整路径，例如 D:\\Projects\\Example"};
            System.Windows.Automation.AutomationProperties.SetName(input,"保留路径");toolbar.Children.Add(input);Grid.SetRow(toolbar,1);body.Children.Add(toolbar);
            var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,RowHeight=42};grid.Columns.Add(TextColumn("类型","Kind",130));grid.Columns.Add(TextColumn("原路径","PathText",-1));grid.Columns.Add(TextColumn("实际路径（添加时）","PhysicalPath",-1));
            grid.SizeChanged+=delegate{FitColumns(grid,2,130,Math.Max(160,grid.ActualWidth*0.4),0);};
            var table=new Grid();table.Children.Add(grid);
            var empty=new TextBlock{Text="还没有保留规则\n在上方输入完整路径，保护需要长期保留的文件。",TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(18),LineHeight=23,Style=(Style)View.FindResource("Muted"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};table.Children.Add(empty);
            var tableFrame=new Border{Style=(Style)View.FindResource("Frame"),Padding=new Thickness(1),Child=table};Grid.SetRow(tableFrame,2);body.Children.Add(tableFrame);
            var notice=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Margin=new Thickness(0,10,0,10),MaxHeight=85,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(notice,3);body.Children.Add(notice);
            var footer=new DockPanel();var close=new Button{Content="关闭",IsCancel=true,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);
            var remove=new Button{Content="移除选中规则",IsEnabled=false,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(remove,Dock.Right);footer.Children.Add(remove);
            var reload=new Button{Content="重新读取",Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(reload,Dock.Right);footer.Children.Add(reload);
            footer.Children.Add(new TextBlock{Text="不支持通配符。路径移动或链接目标变化后，请更新规则。",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center});Grid.SetRow(footer,4);body.Children.Add(footer);
            Action bind=()=>{
                keep=preserveStore.Load();ApplyKeepFlags();grid.ItemsSource=keep.Rules.ToList();grid.SelectedItem=grid.Items.Cast<KeepRule>().FirstOrDefault();
                add.IsEnabled=keep.Error==null;empty.Visibility=keep.Rules.Count==0?Visibility.Visible:Visibility.Collapsed;
                empty.Text=keep.Error==null?"还没有保留规则\n在上方输入完整路径，保护需要长期保留的文件。":"保留规则无法读取\n请查看下方原因，修复后点击「重新读取」。";
                notice.Text=keep.Error??("已保存 "+keep.Rules.Count+" 条规则。存储位置："+preserveStore.FilePath);
            };
            grid.SelectionChanged+=delegate{remove.IsEnabled=grid.SelectedItem!=null && keep.Error==null;};
            add.Click+=delegate{if(SaveKeep(input.Text,kind.SelectedIndex==0,dialog)){input.Clear();bind();}};
            remove.Click+=delegate{
                var item=grid.SelectedItem as KeepRule;if(item==null)return;
                try{preserveStore.Remove(item);bind();}catch(Exception ex){notice.Text="移除失败："+ex.GetBaseException().Message;}
            };
            reload.Click+=delegate{bind();};dialog.Content=new Border{Background=View.Background,Child=body};bind();dialog.ShowDialog();
        }
        private void ShowCleanupResults()
        {
            if(lastCleanup.Count==0)return;
            var dialog=DetailWindow("最近一次缓存清理结果",1040,680);var body=new Grid{Margin=new Thickness(20)};
            foreach(var h in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})body.RowDefinitions.Add(new RowDefinition{Height=h});
            var heading=new StackPanel();heading.Children.Add(new TextBlock{Text="每个文件，都有处理结果",FontSize=22,FontWeight=FontWeights.SemiBold});
            heading.Children.Add(new TextBlock{Text=lastCleanupTime.ToString("yyyy-MM-dd HH:mm:ss")+" · 已删除 "+lastCleanup.Count(x=>x.Code==CleanCode.Deleted)+" 个 · 仅记录本次启动最近一次缓存清理",Style=(Style)View.FindResource("Muted"),Margin=new Thickness(0,7,0,16),TextWrapping=TextWrapping.Wrap});body.Children.Add(heading);
            var toolbar=new DockPanel{Margin=new Thickness(0,0,0,10)};var filter=new ComboBox{Width=140,SelectedIndex=0,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0)};
            foreach(var text in new[]{"全部结果","未完成 / 已跳过","已删除","已保留"})filter.Items.Add(text);DockPanel.SetDock(filter,Dock.Right);toolbar.Children.Add(filter);
            var searchLabel=new TextBlock{Text="搜索结果",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,9,0)};DockPanel.SetDock(searchLabel,Dock.Left);toolbar.Children.Add(searchLabel);
            var search=new TextBox{Style=(Style)View.FindResource("SearchField"),ToolTip="搜索路径、项目、结果或原因"};System.Windows.Automation.AutomationProperties.SetName(search,"搜索清理结果");toolbar.Children.Add(search);Grid.SetRow(toolbar,1);body.Children.Add(toolbar);
            var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,RowHeight=42};grid.Columns.Add(TextColumn("结果","Status",130));grid.Columns.Add(TextColumn("扫描大小","SizeText",100,"Bytes"));grid.Columns.Add(TextColumn("文件路径","PathText",-1));grid.Columns.Add(TextColumn("原因","Reason",-1));grid.SizeChanged+=delegate{FitColumns(grid,2,130,100,0,Math.Max(180,grid.ActualWidth*0.3));};
            var table=new Grid();table.Children.Add(grid);
            var empty=new TextBlock{Text="没有匹配的清理结果\n试试其他关键词，或切换到「全部结果」。",TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(18),LineHeight=23,Style=(Style)View.FindResource("Muted"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};table.Children.Add(empty);
            var tableFrame=new Border{Style=(Style)View.FindResource("Frame"),Padding=new Thickness(1),Child=table};Grid.SetRow(tableFrame,2);body.Children.Add(tableFrame);
            var detail=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=90,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Margin=new Thickness(0,10,0,10)};Grid.SetRow(detail,3);body.Children.Add(detail);
            var footer=new DockPanel();var close=new Button{Content="关闭",IsCancel=true,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);
            var open=new Button{Content="打开所在目录",IsEnabled=false,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(open,Dock.Right);footer.Children.Add(open);
            var count=new TextBlock{VerticalAlignment=VerticalAlignment.Center};footer.Children.Add(count);Grid.SetRow(footer,4);body.Children.Add(footer);
            Action bind=()=>{
                string term=search.Text.Trim();var entries=lastCleanup.Where(x=>(term.Length==0 || HasText(x.PathText,term) || HasText(x.Project,term) || HasText(x.Reason,term) || HasText(x.Status,term)) &&
                    (filter.SelectedIndex==0 || filter.SelectedIndex==1 && x.Code!=CleanCode.Deleted && x.Code!=CleanCode.Kept && x.Code!=CleanCode.Excluded || filter.SelectedIndex==2 && x.Code==CleanCode.Deleted || filter.SelectedIndex==3 && (x.Code==CleanCode.Kept || x.Code==CleanCode.Excluded))).ToList();
                grid.ItemsSource=entries;grid.SelectedItem=entries.FirstOrDefault();empty.Visibility=entries.Count==0?Visibility.Visible:Visibility.Collapsed;count.Text=entries.Count+" / "+lastCleanup.Count+" 个结果 · 关闭程序后不保留此明细";
            };
            grid.SelectionChanged+=delegate{var item=grid.SelectedItem as FileOutcome;open.IsEnabled=item!=null;detail.Text=item==null?"没有匹配的结果。":item.Project+" · "+item.Status+"\n"+item.PathText+"\n"+item.Reason;};
            open.Click+=delegate{var item=grid.SelectedItem as FileOutcome;if(item!=null)OpenLocation(Path.GetDirectoryName(item.PathText),false);};
            search.TextChanged+=delegate{bind();};filter.SelectionChanged+=delegate{bind();};dialog.Content=new Border{Background=View.Background,Child=body};bind();dialog.ShowDialog();
        }
    }
}
