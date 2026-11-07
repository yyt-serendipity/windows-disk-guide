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
        internal Func<SoftwareEntry,SoftwareInventoryResult,ResidualPlan> ResidualReader = SoftwareInventory.PreviewResidual;
        private StartupInventoryResult startupInventory = new StartupInventoryResult();
        private SoftwareInventoryResult softwareInventory = new SoftwareInventoryResult();
        private bool startupLoaded, softwareLoaded, startupLoading, softwareLoading, uninstallLaunching, checkingUninstall;
        private SoftwareEntry pendingUninstall;
        private SoftwareEntry unconfirmedUninstall;
        private ResidualPlan uninstallResidual;
        private bool OnStartup { get { return tabs.SelectedItem == Get<TabItem>("StartupTab"); } }
        private bool OnSoftware { get { return tabs.SelectedItem == Get<TabItem>("SoftwareTab"); } }

        private void WireManagement()
        {
            View.SizeChanged += delegate { ArrangeManagement(); };
            Get<ScrollViewer>("UninstallResult").IsVisibleChanged+=delegate{ArrangeManagement();};
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
            Get<Button>("ReviewResidualBtn").Click += async delegate { await ReviewUninstallResidual(); };
        }

        private void ArrangeManagement()
        {
            bool small = (View.ActualHeight > 0 ? View.ActualHeight : View.Height) < 680;
            Get<Border>("SoftwareDetailCard").Padding=new Thickness(15,small?6:12,15,small?6:12);
            Get<TextBlock>("SoftwareNotice").Visibility=small && Get<ScrollViewer>("UninstallResult").Visibility==Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            Get<ScrollViewer>("UninstallResult").MaxHeight=small?76:110;
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
            if (OnStartup) Status("查看哪些软件会随电脑启动。可以打开 Windows 设置关闭。");
            else if (OnSoftware) Status("不再用的软件可以卸载；不认识的先保留。");
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
            SetNotice("StartupNotice", startupInventory.Warnings, "关闭开机启动不会卸载软件，需要时仍可手动打开。这里不一定能找到所有启动方式。");
            Get<Button>("TaskSettingsBtn").Visibility=Get<ComboBox>("StartupKind").SelectedIndex==2 ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("ServicesBtn").Visibility=Get<ComboBox>("StartupKind").SelectedIndex==3 ? Visibility.Visible : Visibility.Collapsed;
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
            SetNotice("SoftwareNotice", softwareInventory.Warnings, "大小由软件提供，仅供参考。想看实际占用，请点「查看占用」。部分软件可能不在此列表。");
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
            Label("SoftwareTitle", item == null ? "选择一个软件，看看是否还需要" : item.Name);
            Label("SoftwareAdvice",item==null ? "不认识的软件先保留。想知道占多少空间，可以点「查看占用」。" : item.CanUninstall ? "如果已经不用，可以卸载。请先保存这个软件里的重要文件。" : "建议先保留。"+item.ProtectionReason);
            Get<TextBox>("SoftwareDetail").Text = item == null ? "可搜索名称、发布者或安装位置。卸载前会再次显示目标软件。" :
                "发布者：" + item.Publisher + "  ·  " + item.Kind + "\n安装位置：" + (String.IsNullOrWhiteSpace(item.InstallPath) ? "未登记" : item.InstallPath) +
                "\n" + (item.CanUninstall ? "先运行官方卸载，再核查独占安装目录的残留。个人数据与共享组件不按名称删除。" : item.ProtectionReason);
            Get<Button>("UninstallBtn").IsEnabled = !softwareLoading && !uninstallLaunching && !checkingUninstall && pendingUninstall == null && item != null && item.CanUninstall;
            Get<Button>("ReviewResidualBtn").Visibility=uninstallResidual!=null && uninstallResidual.CanRecycle ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("ReviewResidualBtn").IsEnabled=!softwareLoading && !uninstallLaunching && !checkingUninstall;
            Get<Button>("SoftwareSpaceBtn").IsEnabled = !softwareLoading && item != null;
        }

        private async Task BeginUninstall()
        {
            var entry = Get<DataGrid>("SoftwareGrid").SelectedItem as SoftwareEntry;
            if (entry == null || !entry.CanUninstall || uninstallLaunching || checkingUninstall || pendingUninstall != null || softwareLoading) return;
            string action = entry.Kind == "商店应用" ? "Windows 将移除此账户的应用包，应用数据也可能随之移除。" : "将打开该软件登记的官方卸载程序，请完成它的提示。";
            if (MessageBox.Show(View, "即将卸载：" + entry.Name + "\n发布者：" + entry.Publisher + "\n版本：" + entry.Version +
                "\n位置：" + entry.InstallPath + "\n\n" + action + "\n卸载可能删除配置或存档，请先保存需要的内容。保留规则不能约束官方卸载器。完成后检查安装目录残留，额外清除前还会显示清单。",
                "确认卸载这个软件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            pendingUninstall = entry; uninstallLaunching = true;
            uninstallResidual=null;unconfirmedUninstall=null;
            Activity("uninstall","正在启动官方卸载 · "+entry.Name);
            PresentUninstallResult(entry,"正在打开卸载程序，请按它的提示完成操作。完成后会自动显示结果。");
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
                        Label("UninstallStatus", "「" + entry.Name + "」要求重启后才能完成卸载。本轮不处理残留，请保存工作并在方便时自行重启。");
                        return;
                    }
                    if (process.ExitCode != 0 || !String.IsNullOrWhiteSpace(error))
                    {
                        unconfirmedUninstall=entry;
                        Label("UninstallStatus", "暂未确认卸载成功。请查看卸载程序的提示，或稍后重试。（返回 " + process.ExitCode + "）" + (error.Length == 0 ? "" : " " + error));
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
                pendingUninstall=null;
                EndActivity("uninstall",Get<TextBlock>("UninstallStatus").Text,true);
                if (!lifetime.IsCancellationRequested) ShowSoftware();
            }
            if (!lifetime.IsCancellationRequested) await CheckUninstall(entry);
        }

        private void PresentUninstallResult(SoftwareEntry owner,string text)
        {
            Label("UninstallResultTitle",owner.Name+" · 卸载结果");
            Label("UninstallStatus",text);
            Get<ScrollViewer>("UninstallResult").Visibility=Visibility.Visible;
        }
        internal Task RefreshSoftware()
        {
            return unconfirmedUninstall==null ? LoadSoftware() : CheckUninstall(unconfirmedUninstall);
        }
        internal async Task CheckUninstall(SoftwareEntry owner)
        {
            if (owner == null || uninstallLaunching || checkingUninstall || lifetime.IsCancellationRequested) return;
            checkingUninstall = true;uninstallResidual=null;unconfirmedUninstall=owner;ShowSoftware();
            Activity("uninstall-check","正在核对卸载结果与安装目录…");
            bool confirmed=false;
            try
            {
                PresentUninstallResult(owner,"正在自动检查卸载结果…");
                while(softwareLoading)await Task.Delay(100,lifetime.Token);
                await LoadSoftware();
                if (lifetime.IsCancellationRequested) return;
                if (softwareInventory.Warnings.Count > 0)
                { Label("UninstallStatus", "部分软件信息没有读到，暂时无法确认是否卸载完成。未删除残留文件。"); return; }
                if (softwareInventory.Items.Any(x => x.Id == owner.Id))
                { Label("UninstallStatus", "软件仍在已安装列表中，暂未确认卸载完成。如果卸载窗口还在，请先完成其中的操作。"); return; }
                var plan = await Task.Run(() => ResidualReader(owner, softwareInventory));
                var protection=preserveStore.Load();
                if(protection.BlocksTree(plan.Path)){plan.CanRecycle=false;plan.Reason=protection.Error??"此安装目录与永久保留规则重叠，不执行残留回收。";}
                if (lifetime.IsCancellationRequested) return;
                uninstallResidual=plan;
                Label("UninstallStatus", "已从软件列表中移除。"+(plan.CanRecycle ? "找到可检查的安装残留（"+Engine.Size(plan.Bytes)+"）。可查看清单后决定是否清理。" :
                    String.IsNullOrWhiteSpace(plan.Path) && owner.PackageFullName==null ? "未找到可确认的安装位置，无法判断是否还有残留。" : plan.Reason));
                confirmed=true;unconfirmedUninstall=null;
            }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) Label("UninstallStatus", "检查没有完成，未删除残留文件。" + ex.GetBaseException().Message); }
            finally
            {
                checkingUninstall = false;
                EndActivity("uninstall-check",Get<TextBlock>("UninstallStatus").Text,!confirmed);
                if (!lifetime.IsCancellationRequested) ShowSoftware();
            }
        }

        private async Task ReviewUninstallResidual()
        {
            var plan=uninstallResidual;
            if(plan==null || !plan.CanRecycle || checkingUninstall || uninstallLaunching || softwareLoading)return;
            checkingUninstall=true;ShowSoftware();
            bool running=false,completed=false;
            try
            {
                if(!ShowResidualPlan(plan.Owner,plan))return;
                running=true;Activity("uninstall-residual","正在回收已确认的安装残留…");
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
                    uninstallResidual=null;completed=true;
            }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) Label("UninstallStatus", "残留处理没有完成：" + ex.GetBaseException().Message); }
            finally
            {
                checkingUninstall = false;
                if(running)EndActivity("uninstall-residual",Get<TextBlock>("UninstallStatus").Text,!completed);
                if (!lifetime.IsCancellationRequested) ShowSoftware();
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
