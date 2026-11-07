using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DiskGuide
{
    internal sealed class FileChoice : INotifyPropertyChanged
    {
        internal readonly Entry Original;
        public FileChoice(Entry file) { Original = file; included = file.Included; }
        private bool included;
        public bool Included { get { return included && !Original.Preserved; } set { if (Original.Preserved || included == value) return; included = value; var e = PropertyChanged; if (e != null) e(this,new PropertyChangedEventArgs("Included")); } }
        public bool CanInclude { get { return !Original.Preserved; } }
        public string Protection { get { return Original.Preserved?"永久保留":"可调整"; } }
        public void RefreshProtection(){var e=PropertyChanged;if(e!=null)foreach(string name in new[]{"Included","CanInclude","Protection"})e(this,new PropertyChangedEventArgs(name));}
        public string Name { get { return Path.GetFileName(Original.Path); } }
        public string PathText { get { return Original.Path; } }
        public long Bytes { get { return Original.Bytes; } }
        public string SizeText { get { return Engine.Size(Bytes); } }
        public string ModifiedText { get { return DateTime.FromFileTimeUtc(Original.Modified).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed partial class Shell
    {
        private Window DetailWindow(string title, double width, double height)
        {
            return new Window { Owner = View, Title = title, Width = Math.Min(width,SystemParameters.WorkArea.Width),
                Height = Math.Min(height,SystemParameters.WorkArea.Height), MinWidth = 620, MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = View.Background,
                FontFamily = View.FontFamily, FontSize = 13, Foreground = View.Foreground, Resources = View.Resources };
        }
        private static DataGridTextColumn TextColumn(string title, string binding, double width, string sort = null)
        {
            return new DataGridTextColumn { Header = title, Binding = new Binding(binding),
                Width = width < 0 ? new DataGridLength(-width,DataGridLengthUnitType.Star) : new DataGridLength(width),
                SortMemberPath = sort ?? binding, ElementStyle = new Style(typeof(TextBlock)) {
                    Setters = { new Setter(TextBlock.MarginProperty,new Thickness(8,0,8,0)), new Setter(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center),
                        new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis), new Setter(TextBlock.ToolTipProperty,new Binding(binding)) }
                } };
        }
        internal static void ApplyChoices(IEnumerable<FileChoice> choices)
        { foreach (var item in choices) if(!item.Original.Preserved)item.Original.Included = item.Included; }
        private void PreviewFiles()
        {
            var row = cleanGrid.SelectedItem as ScanRow;
            if (busy || row == null || row.Files.Count == 0) return;
            var choices = row.Files.Select(x => new FileChoice(x)).OrderByDescending(x => x.Bytes).ThenBy(x => x.PathText).ToList();
            var dialog = DetailWindow("清理前文件预览 · " + row.Name, 960, 670);
            var body = new Grid { Margin = new Thickness(20) };
            foreach (var height in new[] { GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto }) body.RowDefinitions.Add(new RowDefinition { Height=height });
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text=row.Name,FontSize=22,FontWeight=FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text="入选依据："+row.Rule.Why,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,8) });
            var count = new TextBlock { Margin=new Thickness(0,0,0,10) }; heading.Children.Add(count);body.Children.Add(heading);
            var toolbar = new DockPanel { Margin=new Thickness(0,0,0,10) };
            var all = new Button { Content="全选当前结果",Margin=new Thickness(8,0,0,0) };
            var none = new Button { Content="取消当前结果",Margin=new Thickness(8,0,0,0) };
            DockPanel.SetDock(none,Dock.Right);toolbar.Children.Add(none);DockPanel.SetDock(all,Dock.Right);toolbar.Children.Add(all);
            var searchLabel=new TextBlock {Text="搜索文件",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,9,0)};
            DockPanel.SetDock(searchLabel,Dock.Left);toolbar.Children.Add(searchLabel);
            var search = new TextBox { ToolTip="搜索本项目的文件名称或完整路径",Style=(Style)View.FindResource("SearchField") };
            System.Windows.Automation.AutomationProperties.SetName(search,"搜索预览文件"); toolbar.Children.Add(search);Grid.SetRow(toolbar,1);body.Children.Add(toolbar);
            var grid = new DataGrid { AutoGenerateColumns=false,IsReadOnly=false,CanUserAddRows=false,CanUserDeleteRows=false,RowHeight=38,SelectionMode=DataGridSelectionMode.Single };
            var box = new FrameworkElementFactory(typeof(CheckBox));
            box.SetBinding(CheckBox.IsCheckedProperty,new Binding("Included") { Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged });
            box.SetBinding(CheckBox.IsEnabledProperty,new Binding("CanInclude"));
            box.SetValue(CheckBox.VerticalAlignmentProperty,VerticalAlignment.Center);box.SetValue(CheckBox.HorizontalAlignmentProperty,HorizontalAlignment.Center);
            grid.Columns.Add(new DataGridTemplateColumn { Header="清理",CellTemplate=new DataTemplate { VisualTree=box },Width=48 });
            grid.Columns.Add(TextColumn("文件名","Name",150));
            grid.Columns.Add(TextColumn("完整路径","PathText",-1));
            grid.Columns.Add(TextColumn("大小","SizeText",95,"Bytes"));grid.Columns.Add(TextColumn("最后修改","ModifiedText",150));
            grid.Columns.Add(TextColumn("保留规则","Protection",85));
            foreach (var column in grid.Columns.Skip(1)) column.IsReadOnly=true;
            grid.SizeChanged+=delegate{FitColumns(grid,2,48,130,0,85,140,85);};
            Grid.SetRow(grid,2);body.Children.Add(grid);
            Action counts = () => { count.Text = "本项目已选 " + choices.Count(x=>x.Included).ToString("N0") + " / " + choices.Count.ToString("N0") + " 个文件 · " + Engine.Size(choices.Where(x=>x.Included).Sum(x=>x.Bytes)) + " · " + row.Status + (row.Pick ? "" : " · 主列表尚未勾选此项目"); };
            Action filter = () => { grid.ItemsSource=choices.Where(x=>HasText(x.PathText,search.Text.Trim()) || search.Text.Trim().Length==0).ToList();counts(); };
            foreach (var item in choices) item.PropertyChanged += delegate { counts(); };
            search.TextChanged += delegate { filter(); };
            all.Click += delegate { foreach(var item in grid.Items.Cast<FileChoice>())item.Included=true; };
            none.Click += delegate { foreach(var item in grid.Items.Cast<FileChoice>())item.Included=false; };
            var footer = new DockPanel { Margin=new Thickness(0,12,0,0) };
            var apply = new Button { Content="保存文件选择",Style=(Style)View.FindResource("Action"),Margin=new Thickness(10,0,0,0) };
            var cancel = new Button { Content="取消",IsCancel=true,Margin=new Thickness(10,0,0,0) };
            DockPanel.SetDock(apply,Dock.Right);footer.Children.Add(apply);DockPanel.SetDock(cancel,Dock.Right);footer.Children.Add(cancel);
            var permanent=new Button{Content="始终保留选中文件",Margin=new Thickness(10,0,0,0),IsEnabled=false};DockPanel.SetDock(permanent,Dock.Right);footer.Children.Add(permanent);
            grid.SelectionChanged+=delegate{var item=grid.SelectedItem as FileChoice;permanent.IsEnabled=item!=null && !item.Original.Preserved;};
            permanent.Click+=delegate{
                var item=grid.SelectedItem as FileChoice;
                if(item!=null && SaveKeep(item.PathText,false,dialog)){foreach(var choice in choices)choice.RefreshProtection();permanent.IsEnabled=false;counts();}
            };
            footer.Children.Add(new TextBlock { Text="普通勾选点击保存后生效；永久保留立即保存。\n本窗口不删除文件。",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center });
            Grid.SetRow(footer,3);body.Children.Add(footer);dialog.Content=new Border {Background=View.Background,Child=body};
            apply.Click += delegate { grid.CommitEdit(DataGridEditingUnit.Cell,true);grid.CommitEdit(DataGridEditingUnit.Row,true);ApplyChoices(choices);dialog.DialogResult=true; };
            filter();dialog.ShowDialog();Summary();ShowCleanDetail();
        }
    }
}
