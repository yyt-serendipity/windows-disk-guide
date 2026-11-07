using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskGuide
{
    internal sealed partial class Shell
    {
        private DataGrid folderGrid;
        private TextBox find;
        private ComboBox policy;
        private Folder current, inspected;
        private List<Folder> folders = new List<Folder>();
        private readonly Dictionary<string, FolderUsage> usage = new Dictionary<string, FolderUsage>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UsagePart> sizes = new Dictionary<string, UsagePart>(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<Tuple<Folder, string, int, string>> parents = new Stack<Tuple<Folder, string, int, string>>();
        private CancellationTokenSource browsing, measuring;
        private bool dirty = true, changing, compact;
        private int folderErrors;
        private string progressText = "";
        internal Func<string,CancellationToken,Action<long,long>,FolderUsage> FolderReader=FolderUsage.Read;

        private void WireBrowser()
        {
            folderGrid = Get<DataGrid>("FolderGrid");
            find = Get<TextBox>("FindBox");
            policy = Get<ComboBox>("PolicyBox");
            folderGrid.SizeChanged += delegate { FitFolderColumns(); };
            find.TextChanged += delegate { if (!changing) FilterFolders(null); };
            policy.SelectionChanged += delegate { if (!changing) FilterFolders(null); };
            folderGrid.SelectionChanged += delegate {
                if (!changing && OnFolders) Inspect(SelectedFolder());
            };
            folderGrid.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e) {
                var element = e.OriginalSource as DependencyObject;
                if (element != null && ItemsControl.ContainerFromElement(folderGrid, element) is DataGridRow) EnterFolder();
            };
            folderGrid.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Enter) { e.Handled = true; EnterFolder(); }
                else if (e.Key == Key.Back) { e.Handled = true; GoUp(); }
            };
            View.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    tabs.SelectedItem = folderTab;
                    find.Focus(); find.SelectAll(); e.Handled = true;
                }
                if (OnFolders && e.Key == Key.Escape && find.IsKeyboardFocusWithin) { find.Clear(); e.Handled = true; }
            };
            Get<Button>("UpBtn").Click += delegate { GoUp(); };
            Get<Button>("ResetFilterBtn").Click += delegate { SetFilters("", 0); FilterFolders(null); };
            Get<Button>("HomeBtn").Click += delegate { ResetBrowser(); };
            Get<Button>("SizeBtn").Click += delegate {
                if (inspected == null || inspected.IsLink) return;
                if (measuring != null) { StopSize(); progressText = "统计已停止，可重新统计。"; RenderDetail(); }
                else Measure(inspected, true);
            };
            Get<Button>("RelatedBtn").Click += delegate {
                if (busy || inspected == null || inspected.Related.Count == 0) return;
                cleanScopePath = inspected.PathText;
                cleanFilter.SelectedIndex = 0;
                BindClean();
                tabs.SelectedItem = cleanupTab;
            };
            Get<Button>("OpenBtn").Click += delegate {
                if (inspected == null || inspected.IsLink || Engine.CheckDirectory(inspected.PathText) != Boundary.Clear) return;
                try
                {
                    string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Process.Start(new ProcessStartInfo(explorer, "\"" + inspected.PathText + "\"") { UseShellExecute = false });
                }
                catch (Exception ex) { Status("无法打开目录。" + ex.GetBaseException().Message); }
            };
            View.SizeChanged += delegate { ArrangeBrowser(); };
            ArrangeBrowser();
        }
        private static void Place(UIElement view, int row, int col)
        { Grid.SetRow(view, row); Grid.SetColumn(view, col); }
        private void ArrangeBrowser()
        {
            double width = View.ActualWidth > 0 ? View.ActualWidth : View.Width;
            double height = View.ActualHeight > 0 ? View.ActualHeight : View.Height;
            var layout=Get<Grid>("AppLayout");
            layout.RowDefinitions[0].Height=new GridLength(height<680?68:80);
            layout.RowDefinitions[2].Height=new GridLength(height<680?32:38);
            bool rail=width>=1100 && height>=680;
            string navigation=rail?"Rail":"Top";
            if(!Object.Equals(tabs.Tag,navigation))
            {
                tabs.Tag=navigation;tabs.TabStripPlacement=rail?Dock.Left:Dock.Top;
                tabs.Template=(ControlTemplate)View.FindResource(rail?"SidebarTabs":"TopTabs");
            }
            layout.ColumnDefinitions[0].Width=new GridLength(rail?116:0);
            Get<Border>("SideBackdrop").Visibility=rail?Visibility.Visible:Visibility.Collapsed;
            Get<Image>("HeaderIcon").Visibility=rail?Visibility.Collapsed:Visibility.Visible;
            tabs.Margin=rail?new Thickness(0):new Thickness(20,9,20,12);
            Get<DockPanel>("FolderHeading").Visibility=rail && height>=680?Visibility.Visible:Visibility.Collapsed;
            Get<DockPanel>("CleanupHeading").Visibility=rail && height>=680?Visibility.Visible:Visibility.Collapsed;
            width-=rail?156:0;
            bool narrow = width < 990;
            compact = narrow && height >= 685;
            var body = Get<Grid>("FolderLayout");
            body.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : narrow ? 9 : 14);
            body.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : narrow ? 235 : width >= 1190 ? 326 : 298);
            var pane = Get<Border>("DetailPane");
            Place(pane, compact ? 1 : 0, compact ? 0 : 2);
            pane.Margin = compact ? new Thickness(0, 9, 0, 0) : new Thickness(0);
            var detail = Get<Grid>("DetailGrid");
            detail.ColumnDefinitions[1].Width = new GridLength(compact ? 15 : 0);
            detail.ColumnDefinitions[2].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Place(Get<StackPanel>("DetailIdentity"), 0, 0);
            Place(Get<StackPanel>("DetailPurpose"), 1, 0);
            Place(Get<StackPanel>("DetailFacts"), compact ? 0 : 2, compact ? 2 : 0);
            Place(Get<StackPanel>("DetailParts"), 3, 0);
            Place(Get<WrapPanel>("DetailActions"), compact ? 1 : 4, compact ? 2 : 0);
            folderGrid.Columns[3].Visibility = narrow && !compact ? Visibility.Collapsed : Visibility.Visible;
            View.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)FitFolderColumns);
            RenderDetail();
        }
        private void FitFolderColumns()
        {
            FitColumns(folderGrid, 0, 0, 88, 96, 99);
        }
        private void SetFilters(string term, int index)
        {
            changing = true;
            try { find.Text = term; policy.SelectedIndex = index; }
            finally { changing = false; }
        }
        private void ResetBrowser()
        {
            parents.Clear();
            current = null;
            dirty = true;
            SetFilters("", 0);
            StopBrowser();
            if (OnFolders && !busy) ReloadFolders(null);
        }
        private void RefreshBrowser(bool invalidate=true)
        {
            if(invalidate){sizes.Clear();usage.Clear();}dirty = true;
            foreach(var f in folders)Prepare(f);
            if(current!=null)Prepare(current);
            if (OnFolders && measuring==null && browsing==null) ReloadFolders(inspected == null ? null : inspected.PathText);
        }
        private void ShowBrowser()
        {
            if (busy || lifetime.IsCancellationRequested) return;
            if(measuring!=null || browsing!=null){RenderDetail();return;}
            if (dirty) ReloadFolders(inspected == null ? null : inspected.PathText);
            else Inspect(SelectedFolder());
        }
        private void StopSize()
        {
            if (measuring != null) { measuring.Cancel(); measuring = null; }
            progressText = "";
            UpdateScanActivity();
        }
        private void StopBrowser()
        {
            StopSize();
            if (browsing != null) { browsing.Cancel(); browsing = null; dirty = true; }
            UpdateScanActivity();
        }
        private Folder SelectedFolder()
        {
            var selected = folderGrid.SelectedItem as Folder;
            return selected ?? (folders.Count == 0 && find.Text.Trim().Length == 0 && policy.SelectedIndex == 0 ? current : null);
        }
        private void Prepare(Folder f)
        {
            f.Related = f.IsLink ? new List<ScanRow>() : rows.Where(r => Folder.ContainsRule(f.PathText, r.PathText)).ToList();
            UsagePart value;
            f.Measured = sizes.TryGetValue(f.PathText, out value) ? value : null;
        }
        private async void ReloadFolders(string prefer)
        {
            StopBrowser();
            if (busy || !OnFolders || lifetime.IsCancellationRequested) return;
            string path = current == null ? null : current.PathText;
            var drives = new[] { @"C:\", @"D:\" }.Where(r => Engine.ReadyFixed(r) && (scope.Length == 0 || r.StartsWith(scope, Engine.CI))).ToArray();
            var rulesNow = rules;
            Label("LocationText", path ?? (scope.Length == 0 ? "C、D 盘根目录" : scope + @"\"));
            Get<TextBlock>("LocationText").ToolTip = path;
            Get<Button>("UpBtn").IsEnabled = current != null;
            Get<Button>("HomeBtn").IsEnabled = current != null;
            Get<Button>("HomeBtn").Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
            changing = true;
            try { folders = new List<Folder>(); folderGrid.ItemsSource = null; inspected = null; }
            finally { changing = false; }
            RenderDetail();
            Get<StackPanel>("FolderEmpty").Visibility = Visibility.Collapsed;
            Label("FolderCount", "正在读取目录…");
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                browsing = cts;
                UpdateScanActivity();
                try
                {
                    var result = await Task.Run(() => {
                        int errors;
                        var entries = Catalog.Children(path, drives, rulesNow, out errors, cts.Token);
                        return Tuple.Create(entries, errors);
                    });
                    if (cts.IsCancellationRequested) return;
                    if (current != null) Prepare(current);
                    folders = result.Item1;
                    folderErrors = result.Item2;
                    foreach (var f in folders) Prepare(f);
                    folders = folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                    dirty = false;
                    FilterFolders(prefer);
                }
                catch (OperationCanceledException) {}
                catch (Exception ex)
                {
                    if (!cts.IsCancellationRequested)
                    {
                        dirty = true;
                        Label("FolderCount", "读取失败。可返回上一级或重新扫描。");
                        Label("FolderEmptyTitle", "无法读取目录");
                        Label("FolderEmptyHint", ex.GetBaseException().Message);
                        Get<Button>("ResetFilterBtn").Visibility = Visibility.Collapsed;
                        Get<StackPanel>("FolderEmpty").Visibility = Visibility.Visible;
                    }
                }
                finally { if (browsing == cts) browsing = null;UpdateScanActivity(); }
            }
        }
        private bool Included(Folder f)
        {
            switch (policy.SelectedIndex)
            {
                case 1: return f.Related.Count > 0;
                case 2: return f.Policy == "保留" || f.Policy == "系统保护" || f.IsLink;
                case 3: return f.Policy == "需要判断" || f.Policy == "手动勾选" || f.Policy == "访问受限";
                default: return true;
            }
        }
        private void FilterFolders(string prefer)
        {
            string term = find.Text.Trim();
            if (prefer == null && inspected != null) prefer = inspected.PathText;
            var matches = folders.Where(f => Included(f) && (term.Length == 0 ||
                f.PathText.IndexOf(term, Engine.CI) >= 0 || f.Purpose.IndexOf(term, Engine.CI) >= 0 ||
                f.Policy.IndexOf(term, Engine.CI) >= 0)).ToList();
            var sorts = folderGrid.Items.SortDescriptions.ToArray();
            changing = true;
            try
            {
                folderGrid.ItemsSource = matches;
                var view = CollectionViewSource.GetDefaultView(matches) as ListCollectionView;
                if (view != null && view.CanChangeLiveSorting)
                {
                    view.LiveSortingProperties.Add("SizeBytes");
                    view.IsLiveSorting = true;
                }
                foreach (var sort in sorts) folderGrid.Items.SortDescriptions.Add(sort);
                var selected = matches.FirstOrDefault(f => String.Equals(f.PathText, prefer, Engine.CI)) ?? folderGrid.Items.Cast<Folder>().FirstOrDefault();
                folderGrid.SelectedItem = selected;
                if (selected != null) folderGrid.ScrollIntoView(selected);
            }
            finally { changing = false; }
            bool filtered = term.Length > 0 || policy.SelectedIndex > 0;
            Label("FolderCount", (filtered ? matches.Count + " / " + folders.Count : folders.Count.ToString()) + " 个目录" +
                (folderErrors > 0 ? " · 有位置无法访问" : matches.Count > 0 ? " · 双击进入" : ""));
            Get<StackPanel>("FolderEmpty").Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Label("FolderEmptyTitle", filtered ? "没有匹配的文件夹" : folderErrors > 0 ? "当前账户无法完整读取" : "这一级没有子文件夹");
            Label("FolderEmptyHint", filtered ? "换一个关键词或重置筛选。" : folderErrors > 0 ? "可返回上一级查看其他目录。" : "可在资源管理器查看本层文件。");
            Get<Button>("ResetFilterBtn").Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
            Inspect(SelectedFolder());
        }
        private void EnterFolder()
        {
            var selected = folderGrid.SelectedItem as Folder;
            if (selected == null || busy) return;
            if (selected.IsLink) { Status("这是目录链接，请从实际所在磁盘查看。"); return; }
            parents.Push(Tuple.Create(current, find.Text, policy.SelectedIndex, selected.PathText));
            current = selected;
            SetFilters("", 0);
            ReloadFolders(null);
        }
        private void GoUp()
        {
            if (busy || parents.Count == 0) return;
            var previous = parents.Pop();
            current = previous.Item1;
            SetFilters(previous.Item2, previous.Item3);
            ReloadFolders(previous.Item4);
        }
        private void Inspect(Folder f)
        {
            if (!ReferenceEquals(f, inspected)) StopSize();
            inspected = f;
            RenderDetail();
            if (f != null && !f.IsLink && OnFolders && !busy && measuring == null && !usage.ContainsKey(f.PathText))
                Measure(f, false);
        }
        private void RenderDetail()
        {
            var f = inspected;
            Label("FolderTitle", f == null ? "目录详情" : f.Name);
            Label("FolderPath", f == null ? "" : f.PathText);
            Get<TextBlock>("FolderPath").ToolTip = f == null ? null : f.PathText;
            Label("FolderWhy", f == null ? "从列表选择一个文件夹，查看用途和清理建议。" : f.Purpose);
            Label("FolderPolicy", f == null ? "" : f.Policy);
            var tag = Get<Border>("FolderPolicyBox");
            tag.Visibility = f == null ? Visibility.Collapsed : Visibility.Visible;
            if (f != null)
            {
                tag.Background = (Brush)new BrushConverter().ConvertFromString(f.Tint);
                Get<TextBlock>("FolderPolicy").Foreground = (Brush)new BrushConverter().ConvertFromString(f.Ink);
            }
            Label("FolderSizeValue", f == null ? "—" : f.Measured != null || f.IsLink ? f.SizeText : measuring != null ? "统计中" : "待统计");
            Label("FolderReadyValue", f == null || busy ? "—" : Engine.Size(f.ReadyBytes));
            bool showRatio = !busy && f != null && f.Measured != null && f.Measured.Readable &&
                             f.ReadyBytes > 0 && f.Measured.Bytes >= f.ReadyBytes;
            Get<StackPanel>("RatioPanel").Visibility = showRatio ? Visibility.Visible : Visibility.Collapsed;
            if (showRatio)
            {
                double share = 100.0 * f.ReadyBytes / f.Measured.Bytes;
                Get<ProgressBar>("EligibleRatio").Value = share;
                Label("RatioText", "可直接清理占已读大小 " + (share < 0.1 ? "< 0.1" : share.ToString("0.#")) + "%");
            }
            Get<Button>("SizeBtn").Content = measuring == null ? (f != null && f.Measured != null ? "重新统计" : "统计") : "取消";
            Get<Button>("SizeBtn").IsEnabled = f != null && !f.IsLink && !busy;
            Get<Button>("OpenBtn").IsEnabled = f != null && !f.IsLink;
            Get<Button>("FolderRankBtn").IsEnabled = f != null && !f.IsLink && !busy;
            Get<Button>("KeepFolderBtn").IsEnabled = f != null && !f.IsLink && !busy;
            Get<Button>("RelatedBtn").IsEnabled = !busy && f != null && f.Related.Count != 0;
            Get<Button>("RelatedBtn").Content = f != null && f.Related.Count > 0 ? "查看清理项 (" + f.Related.Count + ")" : "查看清理项";
            string note = progressText;
            if (note.Length == 0 && f != null && f.Measured != null)
            {
                var value = f.Measured;
                note = value.Readable ? "已读 " + value.Files.ToString("N0") + " 个文件" : "当前账户无法读取完整大小";
                if (value.Skipped != 0) note += " · 跳过 " + value.Skipped.ToString("N0") + " 处";
            }
            Label("MeasureNote", note);
            var extra = new List<string>();
            if (f != null)
            {
                long optional = f.Related.Where(r => r.CanPick && r.Rule.Optional).Sum(r => r.Bytes);
                long blocked = f.Related.Where(r => r.BusyApp).Sum(r => r.Bytes);
                if (optional > 0) extra.Add("可选 " + Engine.Size(optional));
                if (blocked > 0) extra.Add("需退出应用 " + Engine.Size(blocked));
            }
            Label("OtherMatches", String.Join(" · ", extra));
            Get<TextBlock>("OtherMatches").Visibility = extra.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            FolderUsage report;
            var top = f != null && usage.TryGetValue(f.PathText, out report) ? report.Largest : null;
            Get<ItemsControl>("PartsList").ItemsSource = top;
            bool showParts = top != null && top.Count > 0;
            Get<StackPanel>("DetailParts").Visibility = !compact && showParts ? Visibility.Visible : Visibility.Collapsed;
            var compactPart = Get<TextBlock>("CompactPart");
            compactPart.Visibility = compact && showParts ? Visibility.Visible : Visibility.Collapsed;
            compactPart.Text = showParts ? "最大占用    " + top[0].Name + "   " + top[0].SizeText : "";
            compactPart.ToolTip = showParts ? String.Join(Environment.NewLine, top.Select(p => p.Name + "   " + p.SizeText)) : null;
        }
        private async void Measure(Folder f, bool immediate)
        {
            StopSize();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                measuring = cts;
                UpdateScanActivity();
                progressText = "正在统计选中的目录…";
                RenderDetail();
                try
                {
                    if (!immediate) await Task.Delay(450, cts.Token);
                    IProgress<Tuple<long,long>> reporter = new Progress<Tuple<long,long>>(v => {
                        if (measuring != cts) return;
                        progressText = "已读 " + Engine.Size(v.Item1) + " · " + v.Item2.ToString("N0") + " 个文件";
                        Label("MeasureNote", progressText);
                    });
                    var report = await ScanWork.Run(() => FolderReader(f.PathText, cts.Token,
                        (bytes, files) => reporter.Report(Tuple.Create(bytes, files))), cts.Token);
                    if (cts.IsCancellationRequested || !ReferenceEquals(f, inspected)) return;
                    usage[f.PathText] = report;
                    sizes[f.PathText] = report.Total;
                    f.Measured = report.Total;
                    foreach (var child in report.Children)
                        if (child.PathText != null) sizes[child.PathText] = child;
                    foreach (var visible in folders)
                    {
                        UsagePart known;
                        if (sizes.TryGetValue(visible.PathText, out known)) visible.Measured = known;
                    }
                    progressText = "";
                }
                catch (OperationCanceledException) {}
                catch (Exception ex) { if (!cts.IsCancellationRequested) progressText = "统计失败。" + ex.GetBaseException().Message; }
                finally
                {
                    if (measuring == cts) { measuring = null; if (!lifetime.IsCancellationRequested) RenderDetail(); }
                    UpdateScanActivity();
                }
            }
        }
    }
}
