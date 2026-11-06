using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;

[assembly: AssemblyTitle("磁盘清理助手")]
[assembly: AssemblyDescription("C、D 盘的目录用途与本地缓存清理")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace DiskGuide
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            try
            {
                var app = new Application();
                app.Run(new Shell(Catalog.Rules).View);
            }
            catch (Exception ex) { MessageBox.Show("程序未能启动。\n" + ex.GetBaseException().Message, "磁盘清理助手", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    internal sealed partial class Shell
    {
        public readonly Window View;
        private readonly Func<List<Rule>> source;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly TabControl tabs;
        private readonly TabItem folderTab, cleanupTab;
        private readonly DataGrid cleanGrid;
        private readonly ListBox cleanFilter;
        private readonly Button clean, refresh;
        private readonly Dictionary<string, string> outcomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private List<Rule> rules = new List<Rule>();
        private List<ScanRow> rows = new List<ScanRow>();
        private string scope = "", cleanScopePath;
        private bool busy;

        public Shell(Func<List<Rule>> ruleSource)
        {
            source = ruleSource;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Window.xaml"))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                View = (Window)XamlReader.Load(reader);
            if (!(SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(View.Dispatcher));
            Rect work = SystemParameters.WorkArea;
            View.Width = Math.Min(View.Width, work.Width);
            View.Height = Math.Min(View.Height, work.Height);
            tabs = Get<TabControl>("Tabs");
            folderTab = Get<TabItem>("FoldersTab");
            cleanupTab = Get<TabItem>("CleanupTab");
            cleanGrid = Get<DataGrid>("CleanGrid");
            cleanFilter = Get<ListBox>("CleanFilter");
            refresh = Get<Button>("RefreshBtn");
            clean = Get<Button>("CleanBtn");
            WireBrowser();
            Get<Button>("GuideBtn").Click += delegate { OpenGuide(); };
            View.Loaded += async delegate { await Refresh(false); };
            View.Closed += delegate { lifetime.Cancel(); StopBrowser(); };
            refresh.Click += async delegate { await Refresh(false); };
            clean.Click += async delegate { await Refresh(true); };
            Get<RadioButton>("AllDrives").Checked += delegate { ChangeScope(""); };
            Get<RadioButton>("DriveC").Checked += delegate { ChangeScope("C:"); };
            Get<RadioButton>("DriveD").Checked += delegate { ChangeScope("D:"); };
            cleanGrid.SelectionChanged += delegate { ShowCleanDetail(); };
            cleanFilter.SelectionChanged += delegate { BindClean(); };
            cleanGrid.SizeChanged += delegate { FitCleanColumns(); };
            tabs.SelectionChanged += delegate(object sender, SelectionChangedEventArgs e) {
                if (e.Source != tabs) return;
                if (OnFolders) ShowBrowser();
                else
                {
                    StopBrowser();
                    View.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)FitCleanColumns);
                }
            };
            Get<Button>("ClearScopeBtn").Click += delegate { cleanScopePath = null; BindClean(); };
        }

        private bool OnFolders { get { return tabs.SelectedItem == folderTab; } }
        private void OpenGuide()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
            if (!File.Exists(path))
            {
                MessageBox.Show(View, "本工具面向 Windows 桌面，需要 .NET Framework 4.8 或 4.8.1。可扫描本地固定 C、D 盘。打开程序只会扫描，点击“一键清理”才会永久删除勾选范围中的匹配文件。\n\n完整说明在发布压缩包中的 README.md。", "使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + path + "\"") { UseShellExecute = false });
            }
            catch (Exception ex) { Status("请在程序目录打开 README.md。" + ex.GetBaseException().Message); }
        }
        private T Get<T>(string name) where T : FrameworkElement { return (T)View.FindName(name); }
        private void Label(string name, string value) { Get<TextBlock>(name).Text = value; }
        private void Status(string message)
        {
            Label("StatusText", message);
            Get<TextBlock>("StatusText").ToolTip = message;
        }

        private void ShowDrives()
        {
            foreach (string id in new[] { "C", "D" })
            {
                string path = id + @":\";
                var button = Get<RadioButton>("Drive" + id);
                bool present = Engine.ReadyFixed(path);
                string windowsRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                Label("Kind" + id, String.Equals(path, windowsRoot, Engine.CI) ? "系统盘" : "本地磁盘");
                button.IsEnabled = !busy && present;
                if (!present) { Label("Free" + id, "此盘不可用"); continue; }
                try
                {
                    var disk = new DriveInfo(path);
                    long total = disk.TotalSize, free = disk.AvailableFreeSpace;
                    Get<ProgressBar>("Bar" + id).Value = total == 0 ? 0 : 100.0 * (total - free) / total;
                    Label("Free" + id, Engine.Size(free) + " 可用");
                    button.ToolTip = "可用 " + Engine.Size(free) + "，总容量 " + Engine.Size(total);
                }
                catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; Label("Free" + id, "无法读取容量"); }
            }
        }
        private void ChangeScope(string value)
        {
            if (scope == value) return;
            scope = value;
            cleanScopePath = null;
            cleanFilter.SelectedIndex = 0;
            ResetBrowser();
            BindClean();
        }
        private bool InArea(ScanRow row)
        {
            return (scope.Length == 0 || scope == row.Drive) &&
                   (cleanScopePath == null || Folder.ContainsRule(cleanScopePath, row.PathText));
        }
        private bool InScope(ScanRow row)
        {
            if (!InArea(row)) return false;
            switch (cleanFilter.SelectedIndex)
            {
                case 1: return row.CanPick && !row.Rule.Optional;
                case 2: return row.CanPick && row.Rule.Optional;
                case 3: return row.BusyApp;
                default: return true;
            }
        }
        private void FitCleanColumns()
        {
            FitColumns(cleanGrid, 1, 44, 0, 61, 110, 105);
        }
        private static void FitColumns(DataGrid grid, int flexible, params double[] widths)
        {
            if (grid.ActualWidth < 300) return;
            double available = Math.Floor(grid.ActualWidth - SystemParameters.VerticalScrollBarWidth - 5);
            for (int i = 0; i < widths.Length; i++)
                if (i != flexible && grid.Columns[i].Visibility == Visibility.Visible) available -= widths[i];
            widths[flexible] = Math.Max(grid.Columns[flexible].MinWidth, available);
            for (int i = 0; i < widths.Length; i++)
            {
                var col = grid.Columns[i];
                if (col.Visibility != Visibility.Visible) continue;
                if (col.Width.IsAbsolute && Math.Abs(col.Width.Value - widths[i]) < 0.5) continue;
                col.Width = new DataGridLength(widths[i]);
            }
        }
        private void BindClean()
        {
            var old = cleanGrid.SelectedItem as ScanRow;
            var filtered = rows.Where(InScope).ToList();
            cleanGrid.ItemsSource = filtered;
            cleanGrid.SelectedItem = filtered.FirstOrDefault(x => old != null && x.Rule.Path == old.Rule.Path) ?? filtered.FirstOrDefault();
            Get<Button>("ClearScopeBtn").Visibility = cleanScopePath == null ? Visibility.Collapsed : Visibility.Visible;
            string areaText = cleanScopePath ?? (scope.Length == 0 ? "全部可用磁盘" : scope + @"\");
            Label("CleanScopeText", areaText);
            Get<TextBlock>("CleanScopeText").ToolTip = areaText;
            var area = rows.Where(InArea).ToList();
            int[] counts = { area.Count, area.Count(r => r.CanPick && !r.Rule.Optional), area.Count(r => r.CanPick && r.Rule.Optional), area.Count(r => r.BusyApp) };
            for (int i = 0; i < counts.Length; i++) Label("FilterCount" + i, counts[i].ToString());
            Label("CleanEmpty", cleanFilter.SelectedIndex > 0 ? "这一类别暂时没有项目。可以切换到「全部项目」。" : "当前范围没有匹配的清理项目。");
            Get<TextBlock>("CleanEmpty").Visibility = filtered.Count == 0 && !busy ? Visibility.Visible : Visibility.Collapsed;
            Summary();
            ShowCleanDetail();
        }
        private void Summary()
        {
            var selected = rows.Where(r => InScope(r) && r.Pick && r.CanPick).ToList();
            Label("ChosenSize", Engine.Size(selected.Sum(r => r.Bytes)));
            Label("ChosenCount", selected.Count + " 项，共 " + selected.Sum(r => (long)r.Files.Count).ToString("N0") + " 个文件");
            clean.ToolTip = selected.Count == 0 ? "先在当前类别中勾选可处理的文件。" :
                "只处理当前磁盘、目录和类别中已勾选的 " + selected.Count + " 项。";
            clean.IsEnabled = !busy && selected.Count > 0;
        }
        private void ShowCleanDetail()
        {
            var row = cleanGrid.SelectedItem as ScanRow;
            Label("CleanWhy", row == null ? "选择项目可查看清理条件。" : row.Detail);
            Label("CleanPath", row == null ? "" : row.PathText);
        }

        private async Task Refresh(bool delete)
        {
            if (busy) return;
            var chosen = rows.Where(r => InScope(r) && r.CanPick && r.Pick).ToList();
            if (delete && chosen.Count == 0) return;
            var previous = rows.Where(r => r.CanPick).ToDictionary(r => r.Rule.Path, r => r.Pick, StringComparer.OrdinalIgnoreCase);
            busy = true;
            StopBrowser();
            cleanGrid.IsEnabled = false;
            cleanFilter.IsEnabled = false;
            Get<Button>("ClearScopeBtn").IsEnabled = false;
            refresh.IsEnabled = false;
            Get<RadioButton>("AllDrives").IsEnabled = false;
            clean.IsEnabled = false;
            ShowDrives();
            Status(delete ? "正在清理勾选的文件…" : "正在扫描已识别的缓存目录…");
            string final = null;
            try
            {
                if (delete)
                {
                    var result = await Task.Run(() => Engine.Clean(chosen, lifetime.Token));
                    foreach (var item in result) outcomes[item.Key] = "  上次" + item.Value.Text;
                    var total = new Outcome { Removed = result.Values.Sum(v => v.Removed), Bytes = result.Values.Sum(v => v.Bytes), Skipped = result.Values.Sum(v => v.Skipped) };
                    final = "本次" + total.Text;
                    Status(final + " 正在更新清单…");
                }
                var snapshot = await Task.Run(() => {
                    var ruleList = source();
                    return Tuple.Create(ruleList, Engine.Scan(ruleList, lifetime.Token));
                });
                lifetime.Token.ThrowIfCancellationRequested();
                rules = snapshot.Item1;
                rows = snapshot.Item2;
                foreach (var row in rows)
                {
                    string last;
                    if (outcomes.TryGetValue(row.Rule.Path, out last)) row.Previous = last;
                    bool picked;
                    if (previous.TryGetValue(row.Rule.Path, out picked)) row.Pick = picked;
                    row.PropertyChanged += delegate { Summary(); };
                }
                Status(final ?? ("扫描完成  ·  " + rows.Count + " 个已识别位置  ·  " + DateTime.Now.ToString("HH:mm:ss")));
            }
            catch (OperationCanceledException) {}
            catch (Exception ex)
            {
                rows = new List<ScanRow>();
                Status((final == null ? "" : final + "  ") + "读取失败，请重新扫描。" + ex.GetBaseException().Message);
            }
            finally
            {
                busy = false;
                if (!lifetime.IsCancellationRequested)
                {
                    cleanGrid.IsEnabled = true;
                    cleanFilter.IsEnabled = true;
                    Get<Button>("ClearScopeBtn").IsEnabled = true;
                    refresh.IsEnabled = true;
                    Get<RadioButton>("AllDrives").IsEnabled = true;
                    ShowDrives();
                    BindClean();
                    RefreshBrowser();
                }
            }
        }
    }
}
