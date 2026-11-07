using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DiskGuide
{
    internal sealed partial class Shell
    {
        private CancellationTokenSource ranking;
        private string rankingPath;
        private FolderUsage ranked;
        internal Func<string,CancellationToken,Action<long,long>,FolderUsage> RankingReader=FolderUsage.ReadRanking;
        private bool OnRanking { get { return tabs.SelectedItem == Get<TabItem>("RankingTab"); } }
        private void WireSpace()
        {
            rankingPath=scope+@"\";
            View.SizeChanged+=delegate{ArrangeSpace();};ArrangeSpace();
            Get<Button>("RankScanBtn").Click += async delegate { await ScanRanking(); };
            Get<Button>("RankRootBtn").Click += delegate { ResetRanking(); };
            Get<ComboBox>("RankKind").SelectionChanged += delegate { BindRanking(); };
            Get<TextBox>("RankSearch").TextChanged += delegate { BindRanking(); };
            Get<DataGrid>("RankGrid").SelectionChanged += delegate { ShowRankSelection(); };
            Get<DataGrid>("RankGrid").SizeChanged += delegate { FitColumns(Get<DataGrid>("RankGrid"),2,230,120,0); };
            Get<Button>("RankOpenBtn").Click += delegate {
                var item=Get<DataGrid>("RankGrid").SelectedItem as UsagePart;
                if(item!=null) OpenLocation(item.PathText,Get<ComboBox>("RankKind").SelectedIndex==1);
            };
            Get<Button>("RankDiveBtn").Click += async delegate {
                var item=Get<DataGrid>("RankGrid").SelectedItem as UsagePart;
                if(item==null || Get<ComboBox>("RankKind").SelectedIndex!=0 || ranking!=null)return;
                rankingPath=item.PathText; await ScanRanking();
            };
            Get<Button>("FolderRankBtn").Click += async delegate {
                if(inspected==null || inspected.IsLink || busy)return;
                if(ranking!=null && String.Equals(rankingPath,inspected.PathText,Engine.CI)){tabs.SelectedItem=Get<TabItem>("RankingTab");return;}
                if(ranking!=null){StopRanking();ranking=null;}
                rankingPath=inspected.PathText; tabs.SelectedItem=Get<TabItem>("RankingTab"); await ScanRanking();
            };
            Get<Button>("SoftwareSpaceBtn").Click += delegate { ShowAppSpace(); };
            ResetRanking();
        }
        private void ArrangeSpace()
        {
            bool small=(View.ActualHeight>0?View.ActualHeight:View.Height)<680;
            Get<Border>("RankOverview").Padding=new Thickness(15,small?7:12,15,small?7:12);
            Get<Border>("RankFooter").Padding=new Thickness(14,small?7:10,14,small?7:10);
            Get<TextBlock>("RankTotal").FontSize=small?19:25;
            Get<DataGrid>("RankGrid").RowHeight=small?34:44;
            Get<DataGrid>("RankGrid").ColumnHeaderHeight=small?29:33;
        }
        private void StopRanking() { if(ranking!=null)ranking.Cancel(); }
        private void ResetRanking()
        {
            StopRanking(); ranking=null; ranked=null;rankingPath=scope+@"\";
            UpdateScanActivity();
            Get<TextBox>("RankSearch").Clear();Get<ComboBox>("RankKind").SelectedIndex=0;
            Label("RankPath",rankingPath);Label("RankTotal","尚未统计");
            Label("RankNotice","点击开始统计。扫描只读取当前磁盘，随时可以取消。");
            Get<Button>("RankScanBtn").Content="开始统计";
            BindRanking();
        }
        private async Task ScanRanking()
        {
            if(ranking!=null){ranking.Cancel();return;}
            string path=rankingPath;
            var cts=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);ranking=cts;ranked=null;
            UpdateScanActivity();
            Label("RankPath",path);Label("RankTotal","统计中…");Label("RankNotice","正在读取文件长度，链接及无法访问的位置会跳过。");
            Get<Button>("RankScanBtn").Content="取消统计";BindRanking();
            var progress=new Progress<Tuple<long,long>>(value=> {
                if(ranking!=cts || cts.IsCancellationRequested)return;
                Label("RankTotal",Engine.Size(value.Item1));Label("RankNotice","统计中 · 已读 "+value.Item2.ToString("N0")+" 个文件；此时尚未生成完整排行。");
            });
            try
            {
                var result=await ScanWork.Run(()=>RankingReader(path,cts.Token,(bytes,files)=>((IProgress<Tuple<long,long>>)progress).Report(Tuple.Create(bytes,files))),cts.Token);
                if(ranking!=cts || cts.IsCancellationRequested)return;
                ranked=result;Label("RankTotal",result.Total.SizeText);
                RecordFolderGrowth(path,result);
                Label("RankNotice","已读 "+result.Total.Files.ToString("N0")+" 个文件 · 跳过 "+result.Total.Skipped+" 处 · "+DateTime.Now.ToString("HH:mm:ss")+" · 文件长度合计，不是可释放空间");
            }
            catch(OperationCanceledException) { if(ranking==cts){Label("RankTotal","已取消");Label("RankNotice","统计未完成，未生成排行。可重新开始。");} }
            catch(Exception ex) { if(ranking==cts){Label("RankTotal","读取失败");Label("RankNotice",ex.GetBaseException().Message);} }
            finally
            {
                if(ranking==cts){ranking=null;if(!lifetime.IsCancellationRequested){Get<Button>("RankScanBtn").Content="重新统计";BindRanking();}}
                cts.Dispose();
                UpdateScanActivity();
            }
        }
        private void BindRanking()
        {
            var grid=Get<DataGrid>("RankGrid");var old=grid.SelectedItem as UsagePart;
            bool files=Get<ComboBox>("RankKind").SelectedIndex==1;
            var source=ranked==null ? Enumerable.Empty<UsagePart>() : files ? ranked.LargestFiles : ranked.Children.Where(x=>x.PathText!=null);
            string term=Get<TextBox>("RankSearch").Text.Trim();
            var items=source.Where(x=>term.Length==0 || HasText(x.PathText,term)).OrderByDescending(x=>x.SizeBytes).ThenBy(x=>x.PathText).ToList();
            grid.ItemsSource=items;grid.SelectedItem=items.FirstOrDefault(x=>old!=null && old.PathText==x.PathText)??items.FirstOrDefault();
            Label("RankCount",items.Count+" 项"+(files?" · 全范围最大 100 个文件":" · 当前层文件夹"));
            Label("RankEmpty",ranking!=null?"正在统计，可取消…":ranked==null?"选择范围后开始统计，找出空间用在哪里。":"此类别或搜索条件没有结果。");
            Get<TextBlock>("RankEmpty").Visibility=items.Count==0?Visibility.Visible:Visibility.Collapsed;
            ShowRankSelection();
        }
        private void ShowRankSelection()
        {
            var item=Get<DataGrid>("RankGrid").SelectedItem as UsagePart;
            Label("RankSelection",item==null?"大文件不等于垃圾文件；本页只读，不提供直接删除。":item.PathText+" · "+item.SizeText);
            Get<TextBlock>("RankSelection").ToolTip=item==null?null:item.PathText;
            Get<Button>("RankOpenBtn").IsEnabled=item!=null;
            Get<Button>("RankDiveBtn").IsEnabled=item!=null && Get<ComboBox>("RankKind").SelectedIndex==0 && ranking==null;
        }
        private void OpenLocation(string path,bool file)
        {
            try
            {
                if(String.IsNullOrEmpty(path) || Engine.CheckDirectory(file?Path.GetDirectoryName(path):path)!=Boundary.Clear)throw new IOException("位置已变化、包含链接或不可访问。");
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),(file?"/select,":"")+"\""+path+"\""){UseShellExecute=false});
            }
            catch(Exception ex){Status("无法打开位置："+ex.GetBaseException().Message);}
        }

        private void ShowAppSpace()
        {
            var app=Get<DataGrid>("SoftwareGrid").SelectedItem as SoftwareEntry;
            if(app==null || softwareLoading)return;
            var inventory=softwareInventory;
            var ruleSnapshot=rules.ToList();
            var dialog=DetailWindow("软件占用 · "+app.Name,1000,680);
            var cts=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            bool finished=false;
            var body=new Grid{Margin=new Thickness(20)};
            foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})body.RowDefinitions.Add(new RowDefinition{Height=height});
            var heading=new StackPanel();heading.Children.Add(new TextBlock{Text=app.Name,FontSize=22,FontWeight=FontWeights.SemiBold});
            heading.Children.Add(new TextBlock{Text="按实际路径统计；目录链接去重。已发现位置不代表软件在全盘的全部数据。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,12)});body.Children.Add(heading);
            var totals=new TextBlock{Text="正在定位软件目录…",FontSize=17,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12),TextWrapping=TextWrapping.Wrap};Grid.SetRow(totals,1);body.Children.Add(totals);
            var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,RowHeight=44};
            grid.Columns.Add(TextColumn("内容","Category",170));grid.Columns.Add(TextColumn("磁盘","Drive",55));grid.Columns.Add(TextColumn("实测大小","SizeText",130,"SizeBytes"));grid.Columns.Add(TextColumn("归属","Attribution",80));grid.Columns.Add(TextColumn("实际目录","PathText",-1));Grid.SetRow(grid,2);body.Children.Add(grid);
            grid.SizeChanged+=delegate{FitColumns(grid,4,170,55,130,80,0);};
            var detail=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=95,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Margin=new Thickness(0,10,0,10)};Grid.SetRow(detail,3);body.Children.Add(detail);
            var footer=new DockPanel();var close=new Button{Content="关闭",IsCancel=true,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);
            var open=new Button{Content="打开选中位置",IsEnabled=false,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(open,Dock.Right);footer.Children.Add(open);
            var state=new TextBlock{Text="只读统计，可关闭窗口取消。",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};footer.Children.Add(state);Grid.SetRow(footer,4);body.Children.Add(footer);
            grid.SelectionChanged+=delegate{var item=grid.SelectedItem as AppLocation;detail.Text=item==null?"":item.Evidence+"\n原路径："+item.SourcePath+"\n"+item.Note;open.IsEnabled=item!=null && item.PathText!=null;};
            open.Click+=delegate{var item=grid.SelectedItem as AppLocation;if(item!=null)OpenLocation(item.PathText,false);};
            dialog.Closed+=delegate{cts.Cancel();};dialog.Content=new Border {Background=View.Background,Child=body};
            dialog.Loaded+=async delegate {
                var progress=new Progress<string>(text=>{if(!finished && !cts.IsCancellationRequested)state.Text=text;});
                try
                {
                    var result=await ScanWork.Run(()=>AppSpace.Read(AppSpace.Locate(app,inventory,ruleSnapshot),cts.Token,text=>((IProgress<string>)progress).Report(text)),cts.Token);
                    if(cts.IsCancellationRequested)return;
                    grid.ItemsSource=result.Locations;grid.SelectedItem=result.Locations.FirstOrDefault();
                    string qualifier=result.Partial?"（已读下限）":"（已发现位置）";
                    totals.Text="登记关联"+qualifier+"：C 盘 "+Engine.Size(result.Bytes("C:",true))+"  /  D 盘 "+Engine.Size(result.Bytes("D:",true))+"\n待核对位置：C 盘 "+Engine.Size(result.Bytes("C:",false))+"  /  D 盘 "+Engine.Size(result.Bytes("D:",false));
                    state.Text=(result.Partial?"部分位置缺失或未完整读取；数字仅为已读下限。":"统计完成。")+" 重叠目录不重复合计；硬链接按文件名计数。";
                    if(result.Locations.Count==0){totals.Text="暂无可统计的位置";detail.Text="未登记安装位置，也没有可可靠匹配的数据目录；无法提供软件占用。";}
                }
                catch(OperationCanceledException){}
                catch(Exception ex){if(!cts.IsCancellationRequested){totals.Text="统计未完成";state.Text=ex.GetBaseException().Message;}}
                finally {finished=true;if(cts.IsCancellationRequested)cts.Dispose();}
            };
            dialog.ShowDialog();
            if(finished)cts.Dispose();
        }
    }
}
