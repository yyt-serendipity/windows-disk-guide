using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DiskGuide
{
    internal sealed partial class Shell
    {
        internal Func<StartupInventoryResult> StartupReader = StartupInventory.Read;
        internal Func<SoftwareInventoryResult> SoftwareReader = SoftwareInventory.Read;
        private StartupInventoryResult startupInventory = new StartupInventoryResult();
        private SoftwareInventoryResult softwareInventory = new SoftwareInventoryResult();
        private bool startupLoaded, softwareLoaded, startupLoading, softwareLoading, uninstallLaunching, checkingUninstall, uninstallNeedsRestart;
        private SoftwareEntry pendingUninstall;
        private bool OnStartup { get { return tabs.SelectedItem == Get<TabItem>("StartupTab"); } }
        private bool OnSoftware { get { return tabs.SelectedItem == Get<TabItem>("SoftwareTab"); } }

        private void WireManagement()
        {
            View.SizeChanged += delegate { ArrangeManagement(); };
            ArrangeManagement();
            Get<TextBox>("StartupSearch").TextChanged += delegate { BindStartup(); };
            Get<ComboBox>("StartupKind").SelectionChanged += delegate { BindStartup(); };
            Get<DataGrid>("StartupGrid").SelectionChanged += delegate { ShowStartup(); };
            Get<TextBox>("SoftwareSearch").TextChanged += delegate { BindSoftware(); };
            Get<ComboBox>("SoftwareKind").SelectionChanged += delegate { BindSoftware(); };
            Get<DataGrid>("SoftwareGrid").SelectionChanged += delegate { ShowSoftware(); };
            Get<Button>("StartupSettingsBtn").Click += delegate { OpenSystemPage("ms-settings:startupapps"); };
            Get<Button>("SoftwareSettingsBtn").Click += delegate { OpenSystemPage("ms-settings:appsfeatures"); };
            Get<Button>("TaskSettingsBtn").Click += delegate { OpenConsole("taskschd.msc"); };
            Get<Button>("ServicesBtn").Click += delegate { OpenConsole("services.msc"); };
            Get<Button>("UninstallBtn").Click += async delegate { await BeginUninstall(); };
            Get<Button>("CheckUninstallBtn").Click += async delegate { await CheckUninstall(); };
            Get<Button>("CloseUninstallBtn").Click += delegate {
                if (uninstallLaunching || checkingUninstall) return;
                pendingUninstall = null; uninstallNeedsRestart = false;
                Get<Border>("UninstallProgress").Visibility = Visibility.Collapsed;
                ShowSoftware();
            };
        }

        private void ArrangeManagement()
        {
            bool small = (View.ActualHeight > 0 ? View.ActualHeight : View.Height) < 680;
            foreach (string page in new[] { "Startup", "Software" })
            {
                Get<TextBlock>(page + "Subtitle").Visibility = small ? Visibility.Collapsed : Visibility.Visible;
                Get<TextBlock>(page + "Heading").FontSize = small ? 20 : 23;
                Get<StackPanel>(page + "Header").Margin = new Thickness(0,0,0,small ? 9 : 14);
                Get<TextBox>(page + "Detail").MaxHeight = small ? 45 : 76;
            }
        }

        private async void UpdateNavigation()
        {
            bool machine = OnStartup || OnSoftware;
            Get<StackPanel>("DriveSwitcher").Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
            Get<StackPanel>("MachineScope").Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
            refresh.ToolTip = OnStartup ? "重新读取本机启动设置" : OnSoftware ? "重新读取本机软件登记" : "重新扫描缓存与目录";
            if (OnStartup) Status("开机启动 · 只读盘点，管理入口交由 Windows 处理。");
            else if (OnSoftware) Status("软件管理 · 调用官方卸载前会核对目标并确认。");
            else if (!busy) Status(scope + @"\ · 目录和缓存仅显示当前磁盘范围。");
            if (OnStartup && !startupLoaded) await LoadStartup();
            if (OnSoftware && !softwareLoaded) await LoadSoftware();
        }

        private void OpenSystemPage(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { Status("无法打开系统设置：" + ex.GetBaseException().Message); }
        }
        private void OpenConsole(string name)
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mmc.exe"), "\"" + Path.Combine(Environment.SystemDirectory, name) + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Status("无法打开管理窗口：" + ex.GetBaseException().Message); }
        }

        private async Task LoadStartup()
        {
            if (startupLoading || lifetime.IsCancellationRequested) return;
            startupLoading = true;
            Activity("startup","正在读取开机启动项…");
            Label("StartupCount", "读取中…");
            Label("StartupEmpty", "正在读取登录项、计划任务和服务…");
            Get<TextBlock>("StartupEmpty").Visibility = Visibility.Visible;
            try
            {
                var value = await Task.Run(StartupReader);
                if (lifetime.IsCancellationRequested) return;
                startupInventory = value; startupLoaded = true;
            }
            catch (Exception ex) { startupInventory = new StartupInventoryResult(); startupInventory.Warnings.Add(ex.GetBaseException().Message); }
            finally
            {
                startupLoading = false;
                EndActivity("startup",startupInventory.Warnings.Count>0 ? "启动项读取不完整，请查看页面提示" : "启动项已读取 · "+startupInventory.Items.Count+" 项",startupInventory.Warnings.Count>0);
                if (!lifetime.IsCancellationRequested) { BindStartup(); QueueIcons(startupInventory.Items,true); }
            }
        }
        private static bool HasText(string value, string term)
        { return !String.IsNullOrEmpty(value) && value.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0; }
        internal static bool MatchStartup(StartupEntry item, string term, int kind)
        {
            bool task = HasText(item.Source, "计划任务"), service = HasText(item.Source, "自动服务");
            return (kind == 0 || kind == 1 && !task && !service || kind == 2 && task || kind == 3 && service) &&
                (String.IsNullOrWhiteSpace(term) || HasText(item.Name, term) || HasText(item.Command, term) || HasText(item.Source, term) || HasText(item.Location, term) || HasText(item.State, term));
        }
        private void BindStartup()
        {
            var grid = Get<DataGrid>("StartupGrid");
            var old = grid.SelectedItem as StartupEntry;
            var items = startupInventory.Items.Where(x => MatchStartup(x, Get<TextBox>("StartupSearch").Text.Trim(), Get<ComboBox>("StartupKind").SelectedIndex)).ToList();
            grid.ItemsSource = items;
            grid.SelectedItem = items.FirstOrDefault(x => old != null && x.Location == old.Location && x.Name == old.Name) ?? items.FirstOrDefault();
            Label("StartupCount", items.Count + " / " + startupInventory.Items.Count + " 项");
            Label("StartupEmpty", startupLoading ? "正在读取启动项…" : "没有匹配项。可清空搜索或切换启动来源。");
            Get<TextBlock>("StartupEmpty").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetNotice("StartupNotice", startupInventory.Warnings, "这里只读取设置。任务可能受账户、电源或日期条件限制；并非所有启动机制都在此列出。");
            ShowStartup();
        }
        private void ShowStartup()
        {
            var item = Get<DataGrid>("StartupGrid").SelectedItem as StartupEntry;
            Label("StartupTitle", item == null ? "选择一条启动记录" : item.Name + "  ·  " + item.State);
            Get<TextBlock>("StartupTitle").ToolTip = item == null ? null : item.Name + " · " + item.State;
            Get<TextBox>("StartupDetail").Text = item == null ? "在上方搜索应用名，查看它通过什么方式随电脑启动。" : "设置：" + item.State + "\n来源：" + item.Location + "\n命令：" + item.Command;
        }

        private async Task LoadSoftware()
        {
            if (softwareLoading || lifetime.IsCancellationRequested) return;
            softwareLoading = true;
            Activity("software","正在读取已安装软件…");
            Label("SoftwareCount", "读取中…");
            Label("SoftwareEmpty", "正在读取桌面软件与商店应用…");
            Get<TextBlock>("SoftwareEmpty").Visibility = Visibility.Visible;
            ShowSoftware();
            try
            {
                var value = await Task.Run(SoftwareReader);
                if (lifetime.IsCancellationRequested) return;
                softwareInventory = value; softwareLoaded = true;
            }
            catch (Exception ex) { softwareInventory = new SoftwareInventoryResult(); softwareInventory.Warnings.Add(ex.GetBaseException().Message); }
            finally
            {
                softwareLoading = false;
                EndActivity("software",softwareInventory.Warnings.Count>0 ? "软件清单读取不完整，请查看页面提示" : "软件清单已读取 · "+softwareInventory.Items.Count+" 项",softwareInventory.Warnings.Count>0);
                if (!lifetime.IsCancellationRequested) { BindSoftware(); QueueIcons(softwareInventory.Items,false); }
            }
        }
        internal static bool MatchSoftware(SoftwareEntry item, string term, int kind)
        {
            bool store = HasText(item.Kind, "商店");
            return (kind == 0 || kind == 1 && !store || kind == 2 && store) &&
                (String.IsNullOrWhiteSpace(term) || HasText(item.Name, term) || HasText(item.Publisher, term) || HasText(item.InstallPath, term));
        }
        private void BindSoftware()
        {
            var grid = Get<DataGrid>("SoftwareGrid");
            var old = grid.SelectedItem as SoftwareEntry;
            var items = softwareInventory.Items.Where(x => MatchSoftware(x, Get<TextBox>("SoftwareSearch").Text.Trim(), Get<ComboBox>("SoftwareKind").SelectedIndex)).ToList();
            grid.ItemsSource = items;
            grid.SelectedItem = items.FirstOrDefault(x => old != null && x.Id == old.Id) ?? items.FirstOrDefault();
            Label("SoftwareCount", items.Count + " / " + softwareInventory.Items.Count + " 项");
            Label("SoftwareEmpty", softwareLoading ? "正在读取软件清单…" : "没有匹配软件。可清空搜索或切换软件类别。");
            Get<TextBlock>("SoftwareEmpty").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetNotice("SoftwareNotice", softwareInventory.Warnings, "免安装软件或其他账户的应用可能未登记。登记大小不等于实际占用。");
            ShowSoftware();
        }
        private void SetNotice(string name, List<string> warnings, string normal)
        {
            Label(name, warnings.Count == 0 ? normal : "清单不完整：" + warnings.Count + " 处读取失败。" + warnings[0]);
            Get<TextBlock>(name).ToolTip = warnings.Count == 0 ? normal : String.Join("\n", warnings);
        }
        private void ShowSoftware()
        {
            var item = Get<DataGrid>("SoftwareGrid").SelectedItem as SoftwareEntry;
            Label("SoftwareTitle", item == null ? "选择要管理的软件" : item.Name);
            Get<TextBox>("SoftwareDetail").Text = item == null ? "可搜索名称、发布者或安装位置。卸载前会再次显示目标软件。" :
                "发布者：" + item.Publisher + "  ·  " + item.Kind + "\n安装位置：" + (String.IsNullOrWhiteSpace(item.InstallPath) ? "未登记" : item.InstallPath) +
                "\n" + (item.CanUninstall ? "先运行官方卸载，再核查独占安装目录的残留。个人数据与共享组件不按名称删除。" : item.ProtectionReason);
            Get<Button>("UninstallBtn").IsEnabled = !softwareLoading && !uninstallLaunching && pendingUninstall == null && item != null && item.CanUninstall;
            Get<Button>("SoftwareSpaceBtn").IsEnabled = !softwareLoading && item != null;
        }

        private async Task BeginUninstall()
        {
            var entry = Get<DataGrid>("SoftwareGrid").SelectedItem as SoftwareEntry;
            if (entry == null || !entry.CanUninstall || uninstallLaunching || pendingUninstall != null || softwareLoading) return;
            string action = entry.Kind == "商店应用" ? "Windows 将移除此账户的应用包，应用数据也可能随之移除。" : "将打开该软件登记的官方卸载程序，请完成它的提示。";
            if (MessageBox.Show(View, "即将卸载：" + entry.Name + "\n发布者：" + entry.Publisher + "\n版本：" + entry.Version +
                "\n位置：" + entry.InstallPath + "\n\n" + action + "\n卸载可能删除配置或存档，请先保存需要的内容。保留规则不能约束官方卸载器。完成后检查安装目录残留，额外清除前还会显示清单。",
                "确认卸载这个软件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            pendingUninstall = entry; uninstallLaunching = true;
            Activity("uninstall","正在启动官方卸载 · "+entry.Name);
            Get<Border>("UninstallProgress").Visibility = Visibility.Visible;
            SetUninstallControls(false);
            Label("UninstallStatus", "正在启动官方卸载：" + entry.Name);
            ShowSoftware();
            Process process = null;
            try
            {
                process = await Task.Run(() => SoftwareInventory.StartUninstall(entry));
                if (process != null)
                {
                    Task<string> output = null, errors = null;
                    if (process.StartInfo.RedirectStandardOutput) output = process.StandardOutput.ReadToEndAsync();
                    if (process.StartInfo.RedirectStandardError) errors = process.StandardError.ReadToEndAsync();
                    Label("UninstallStatus", "请完成「" + entry.Name + "」的官方卸载。完成后将核查结果。");
                    Activity("uninstall","等待官方卸载完成 · "+entry.Name);
                    while (!process.HasExited)
                    {
                        if (lifetime.IsCancellationRequested) return;
                        await Task.Delay(400);
                    }
                    if (output != null) await output;
                    string error = errors == null ? "" : await errors;
                    if (process.ExitCode == 3010 || process.ExitCode == 1641)
                    {
                        uninstallNeedsRestart = true;
                        Label("UninstallStatus", "「" + entry.Name + "」要求重启后才能完成卸载。本轮不处理残留，请保存工作并在方便时自行重启。");
                        return;
                    }
                    if (process.ExitCode != 0 || !String.IsNullOrWhiteSpace(error))
                    {
                        Label("UninstallStatus", "卸载未确认成功（返回 " + process.ExitCode + "）。可检查结果或关闭记录后重试。" + (error.Length == 0 ? "" : " " + error));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested) Label("UninstallStatus", "未能完成卸载：" + ex.GetBaseException().Message);
                return;
            }
            finally
            {
                if (process != null) process.Dispose();
                uninstallLaunching = false;
                EndActivity("uninstall",Get<TextBlock>("UninstallStatus").Text,true);
                if (!lifetime.IsCancellationRequested) { SetUninstallControls(true); ShowSoftware(); }
            }
            if (!lifetime.IsCancellationRequested) await CheckUninstall();
        }

        private void SetUninstallControls(bool enabled)
        {
            Get<Button>("CheckUninstallBtn").IsEnabled = enabled && !uninstallNeedsRestart;
            Get<Button>("CloseUninstallBtn").IsEnabled = enabled;
        }
        private async Task CheckUninstall()
        {
            if (pendingUninstall == null || uninstallLaunching || checkingUninstall || softwareLoading || uninstallNeedsRestart) return;
            checkingUninstall = true; SetUninstallControls(false);
            Activity("uninstall-check","正在核对卸载结果与安装目录…");
            bool confirmed=false;
            var owner = pendingUninstall;
            try
            {
                Label("UninstallStatus", "正在重新核对「" + owner.Name + "」的软件登记与安装目录…");
                await LoadSoftware();
                if (lifetime.IsCancellationRequested) return;
                if (softwareInventory.Warnings.Count > 0)
                { Label("UninstallStatus", "软件清单未完整读取，暂不能确认卸载或处理残留。可稍后再检查。"); return; }
                if (softwareInventory.Items.Any(x => x.Id == owner.Id))
                { Label("UninstallStatus", "「" + owner.Name + "」仍登记为已安装。请完成官方卸载后再次检查；已取消则关闭这条记录。"); return; }
                var plan = await Task.Run(() => SoftwareInventory.PreviewResidual(owner, softwareInventory));
                var protection=preserveStore.Load();
                if(protection.BlocksTree(plan.Path)){plan.CanRecycle=false;plan.Reason=protection.Error??"此安装目录与永久保留规则重叠，不执行残留回收。";}
                if (lifetime.IsCancellationRequested) return;
                Label("UninstallStatus", "「" + owner.Name + "」的原安装登记已移除。" + plan.Reason);
                Activity("uninstall-check","等待确认残留清单 · "+owner.Name);
                if (ShowResidualPlan(owner, plan))
                {
                    Activity("uninstall-check","正在回收已确认的安装残留…");
                    IntPtr window = new System.Windows.Interop.WindowInteropHelper(View).Handle;
                    var completion = new TaskCompletionSource<bool>();
                    var worker = new System.Threading.Thread(() => {
                        try {
                            using(var lease=preserveStore.Acquire())
                            {
                                if(lease.Snapshot.BlocksTree(plan.Path))throw new IOException(lease.Snapshot.Error??"永久保留规则已覆盖该目录，未回收残留。");
                                SoftwareInventory.RecycleResidual(plan, window);
                            }
                            completion.SetResult(true);
                        }
                        catch (Exception ex) { completion.SetException(ex); }
                    });
                    worker.SetApartmentState(System.Threading.ApartmentState.STA); worker.IsBackground = true; worker.Start();
                    await completion.Task;
                    Label("UninstallStatus", "Windows 已处理确认的安装残留。其他位置的个人数据和未识别内容没有自动清除。");
                }
                else if(plan.CanRecycle) Label("UninstallStatus","「"+owner.Name+"」的原安装登记已移除。本次未选择回收安装目录残留。");
                confirmed=true;
            }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) Label("UninstallStatus", "处理未完成：" + ex.GetBaseException().Message); }
            finally
            {
                checkingUninstall = false;
                EndActivity("uninstall-check",Get<TextBlock>("UninstallStatus").Text,!confirmed);
                if (!lifetime.IsCancellationRequested) { SetUninstallControls(true); ShowSoftware(); }
            }
        }

        private bool ShowResidualPlan(SoftwareEntry owner, ResidualPlan plan)
        {
            var dialog = new Window { Owner = View, Title = "卸载结果与残留预览", Width = 720, Height = 530,
                MinWidth = 540, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = View.Background, FontFamily = View.FontFamily, FontSize = 13, Foreground = View.Foreground,
                ShowInTaskbar = false };
            dialog.MaxHeight = SystemParameters.WorkArea.Height;
            dialog.Resources = View.Resources;
            var body = new Grid { Margin = new Thickness(22) };
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) body.RowDefinitions.Add(new RowDefinition { Height = height });
            var title = new TextBlock { Text = owner.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            body.Children.Add(title);
            var reason = new TextBlock { Text = "原安装登记已移除。\n" + plan.Reason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,10) };
            Grid.SetRow(reason,1); body.Children.Add(reason);
            var path = new TextBox { Text = (plan.Path ?? "未登记安装目录") + "\n已读 " + plan.FileCount + " 个文件 · " + Engine.Size(plan.Bytes), IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0,0,0,10) };
            Grid.SetRow(path,2); body.Children.Add(path);
            var list = new ListBox { ItemsSource = plan.Files.Count == 0 ? new[] { "此范围没有可展示的残留文件；这不代表全磁盘没有其他数据。" } : plan.Files.ToArray() };
            Grid.SetRow(list,3); body.Children.Add(list);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
            var open = new Button { Content = "打开目录", IsEnabled = !String.IsNullOrEmpty(plan.Path) && Directory.Exists(plan.Path), Padding = new Thickness(14,8,14,8), Margin = new Thickness(0,0,10,5) };
            open.Click += delegate {
                try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + plan.Path + "\"") { UseShellExecute = false }); }
                catch (Exception ex) { MessageBox.Show(dialog, ex.GetBaseException().Message, "无法打开目录"); }
            };
            var close = new Button { Content = "保留并关闭", IsCancel = true, Padding = new Thickness(14,8,14,8), Margin = new Thickness(0,0,10,0) };
            close.Click += delegate { dialog.DialogResult = false; };
            var recycle = new Button { Content = "将上述安装残留移至回收站", IsEnabled = plan.CanRecycle, Padding = new Thickness(14,8,14,8) };
            recycle.Style = (Style)View.FindResource("Action");
            recycle.Click += delegate { dialog.DialogResult = true; };
            actions.Children.Add(open); actions.Children.Add(close); actions.Children.Add(recycle); Grid.SetRow(actions,4); body.Children.Add(actions);
            dialog.Content = body;
            return dialog.ShowDialog() == true;
        }
    }
}
