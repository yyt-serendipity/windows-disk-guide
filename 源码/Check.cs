using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiskGuide
{
    // Only the test executable injects fixture rules. Optional real-machine previews never invoke cleanup.
    internal static class Check
    {
        private static string box;
        private static int assertions;
        private static readonly DateTime Old = DateTime.UtcNow.AddDays(-12);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool CreateHardLink(string link, string existing, IntPtr reserved);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetLongPathName(string path, StringBuilder destination, uint capacity);

        private static string CanonicalDirectory(string directory)
        {
            // Windows CI often spells the user profile RUNNER~1. Handle-based safety checks
            // compare the actual long path; test paths and folder navigation must use that spelling too.
            var buffer = new StringBuilder(32768);
            string path = Path.GetFullPath(directory);
            uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }

        private static void Expect(bool value,string label)
        { if(!value) throw new InvalidOperationException(label); assertions++; }
        private static string Inside(string relative)
        {
            string path=Path.GetFullPath(Path.Combine(box,relative));
            if(!Engine.Below(path,box))throw new InvalidOperationException("Outside fixture: "+path);
            return path;
        }
        private static string Put(string rel,string data,bool old)
        {
            string path=Inside(rel); Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,data); if(old)File.SetLastWriteTimeUtc(path,Old); return path;
        }
        private static Rule R(string sub, FileKind kind,int days)
        { return new Rule {Path=Inside(sub),Name="测试 "+sub,Kind=kind,Days=days,Why="仅使用测试文件",Processes=new string[0]}; }
        private static void DeleteFixture(string dir)
        {
            if(!String.Equals(dir,box,Engine.CI) && !Engine.Below(dir,box))throw new InvalidOperationException("Unexpected fixture path");
            if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0){Directory.Delete(dir,false);return;}
            foreach (string file in Directory.GetFiles(dir))File.Delete(file);
            foreach (string child in Directory.GetDirectories(dir))DeleteFixture(child);
            Directory.Delete(dir,false);
        }
        private static void Junction(string link,string target)
        {
            if(!Engine.Below(link,box)||!Engine.Below(target,box))throw new InvalidOperationException("Unsafe fixture");
            string command="$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' | Out-Null";
            var psi=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))) {UseShellExecute=false,CreateNoWindow=true};
            using(var process=Process.Start(psi)){process.WaitForExit();Expect(process.ExitCode==0,"Fixture junction created");}
        }
        private static void VerifyEngine()
        {
            var web=R("web",FileKind.Web,0);var temp=R("tmp",FileKind.OldTemp,7);var shader=R("shader",FileKind.Shader,0);
            var logs=R("logs",FileKind.OldLog,7);var changedRoot=R("move",FileKind.Web,0);
            string webOk=Put(@"web\f_000001","cache",true);string privateDoc=Put(@"web\project.docx","doc",true);
            string nested=Put(@"web\photos\f_000003","private",true);string index=Put(@"web\index-dir\the-real-index","index",true);
            string old=Put(@"tmp\old.tmp","old",true);string fresh=Put(@"tmp\new.tmp","new",false);
            string linked=Put(@"tmp\linked.tmp","links",true);string locked=Put(@"tmp\locked.tmp","locked",true);
            string modified=Put(@"tmp\modified.tmp","change",true);string swap=Put(@"tmp\swap.tmp","original",true);
            string gpu=Put(@"shader\0123456789abcdef.nvph","gpu",true);string log=Put(@"logs\service\old.log","old log",true);
            string currentLog=Put(@"logs\service\current.log","current",false);
            Put(@"move\f_000001","snapshot",true);string decoy=Put(@"decoy\f_000001","external",true);
            var scanned=Engine.Scan(new[]{web,temp,shader,logs,changedRoot},CancellationToken.None);
            Expect(scanned.Count==5 && !scanned.Single(x=>x.Rule==shader).Pick,"Optional default off");
            DateTime was=File.GetLastWriteTimeUtc(swap);File.Move(swap,Inside(@"tmp\original.keep"));
            File.WriteAllText(swap,"replaced");File.SetLastWriteTimeUtc(swap,was);
            Expect(CreateHardLink(Inside("another.link"),linked,IntPtr.Zero),"Fixture hard link created");
            File.AppendAllText(modified,"-new version");
            Directory.Move(Inside("move"),Inside("original-move")); Junction(Inside("move"),Inside("decoy"));
            Expect(Engine.CheckDirectory(Inside("move"))==Boundary.Link,"Junction excluded");
            Expect(Engine.Scan(new[]{changedRoot},CancellationToken.None).Count==0,"No directory link traversed");
            string late=Put(@"tmp\created-after-scan.tmp","late",true);
            Dictionary<string,Outcome> done;
            using(var held=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.None))
                done=Engine.Clean(scanned,CancellationToken.None);
            Expect(done.Values.Sum(v=>v.Removed)==4,"Only four verified cache files deleted");
            Expect(done.Values.Sum(v=>v.Skipped)>=5,"Replaced, locked, changed, hardlinked and redirected files skipped");
            Expect(new[]{webOk,index,old,log}.All(x=>!File.Exists(x)),"Removable test files gone");
            Expect(new[]{privateDoc,nested,fresh,linked,locked,modified,swap,gpu,currentLog,decoy,late}.All(File.Exists),"Protected files survived");
            Expect(Engine.Clean(scanned,CancellationToken.None).Values.Sum(v=>v.Removed)<=1,"Retry targets old snapshot only");
            Expect(File.Exists(late),"Retry did not add new files");
            Put(@"backup\manifest.json","{}",false);
            Put(@"backup\database.sql","-- synthetic fixture",false);
            Expect(Catalog.Describe(Inside("backup"),false,new List<Rule>()).Policy=="保留","Database backup protected");
            Expect(Catalog.Describe(Path.Combine(Path.GetPathRoot(box),"Windows"),false,new List<Rule>()).Policy=="系统保护","Windows folder policy");
            Directory.CreateDirectory(Inside("random"));
            Expect(Catalog.Describe(Inside("random"),false,new List<Rule>()).Policy=="需要判断","Unknown folder kept");
            var busy=R("shader",FileKind.Shader,0);busy.Processes=new[]{Process.GetCurrentProcess().ProcessName};
            Expect(!Engine.Scan(new[]{busy},CancellationToken.None).Single().CanPick,"Running application gate");
            Expect(!Engine.ReadyFixed(@"E:\") && !Engine.ReadyFixed(@"\\example.invalid\test"),"Unsupported or network volumes stay out of scope");
            var migrated=R(@"relocated\cache",FileKind.Web,0);
            string migratedFile=Put(@"relocated\cache\f_000001","cache",true);
            migrated.PhysicalAppPath=Inside("relocated");
            migrated.OriginalAppPath=Inside("app-link");
            Expect(Engine.Scan(new[]{migrated},CancellationToken.None).Count==0,"Unlinked lookalike cache excluded");
            Junction(migrated.OriginalAppPath,migrated.PhysicalAppPath);
            var linkedRows=Engine.Scan(new[]{migrated},CancellationToken.None);
            Expect(linkedRows.Count==1 && linkedRows[0].Files.Count==1,"Migrated cache included after origin proof");
            Directory.Delete(migrated.OriginalAppPath,false);
            Junction(migrated.OriginalAppPath,Inside("decoy"));
            var refused=Engine.Clean(linkedRows,CancellationToken.None)[migrated.Path];
            Expect(refused.Removed==0 && refused.Skipped==1 && File.Exists(migratedFile),"Changed migration link invalidates snapshot");
        }

        private static void Pump(Func<bool> predicate,string failure)
        {
            var sw=Stopwatch.StartNew();
            do {
                var frame=new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,(Action)(()=>frame.Continue=false));
                Dispatcher.PushFrame(frame);
                if(predicate())return;
                Thread.Sleep(15);
            } while(sw.Elapsed<TimeSpan.FromSeconds(100));
            throw new TimeoutException(failure);
        }
        private static T C<T>(Window w,string id) where T:class {return (T)w.FindName(id);}
        private static T Child<T>(DependencyObject element) where T:DependencyObject
        {
            if(element==null) return null;
            if(element is T) return (T)element;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){
                T found=Child<T>(VisualTreeHelper.GetChild(element,i));if(found!=null)return found;
            }
            return null;
        }
        private static void Hidden(Window w)
        {
            w.WindowStartupLocation=WindowStartupLocation.Manual;w.Left=-20000;w.Top=-20000;
            w.ShowActivated=false;w.ShowInTaskbar=false;w.Show();
        }
        private static void Image(Window w,string dir,string name)
        {
            if(String.IsNullOrWhiteSpace(dir))return;
            var content=(FrameworkElement)w.Content;content.UpdateLayout();
            var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
            image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var output=File.Create(Path.Combine(dir,name)))encoder.Save(output);
        }
        private static void VerifyUsage()
        {
            string root = Inside("usage");
            Put(@"usage\direct.txt", "12", false);
            Put(@"usage\large\nest\file.bin", "123456789", false);
            Put(@"usage\small\file.bin", "123", false);
            Directory.CreateDirectory(Inside(@"usage\empty"));
            Junction(Inside(@"usage\redirect"), Inside("decoy"));
            var usage = FolderUsage.Read(root, CancellationToken.None, null);
            Expect(usage.Total.Readable && usage.Total.Bytes == 14 && usage.Total.Files == 3 && usage.Total.Skipped == 1, "Usage excludes redirected content");
            Expect(usage.Largest.Select(x=>x.Name).SequenceEqual(new[]{"large","small","本层文件"}), "Top-level attribution and ranking");
            Expect(usage.Children.Single(x=>x.Name=="empty").Readable && usage.Children.Single(x=>x.Name=="empty").Bytes==0, "Empty child measured");
            Put(@"wrap\owner\first\cache.bin","1234",false);
            Put(@"wrap\owner\second\cache.bin","12",false);
            var wrapped=FolderUsage.Read(Inside("wrap"),CancellationToken.None,null);
            Expect(wrapped.Children.Single().Name=="owner" && wrapped.Total.Bytes==6 && wrapped.Largest.First().Name==@"owner\first","Single wrapper shows meaningful nested usage");
            Expect(!FolderUsage.Read(Inside("missing"),CancellationToken.None,null).Total.Readable, "Unavailable folder never reported as empty");
            bool cancelled=false;
            try {FolderUsage.Read(root,new CancellationToken(true),null);} catch(OperationCanceledException){cancelled=true;}
            Expect(cancelled,"Read-only sizing can be cancelled");
            Expect(Folder.ContainsRule(Inside("button"),Inside(@"button\cache")) &&
                !Folder.ContainsRule(Inside("button"),Inside("button-copy")), "Folder scope respects path boundaries");
        }
        private static void PressEnter(DataGrid grid)
        {
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, Key.Enter) {RoutedEvent=Keyboard.PreviewKeyDownEvent});
        }
        private static void SelectPath(Window w, string path)
        {
            string full=Path.GetFullPath(path), root=Path.GetPathRoot(full), through=root;
            var grid=C<DataGrid>(w,"FolderGrid");
            foreach(string component in full.Substring(root.Length).Split('\\'))
            {
                through=Path.Combine(through,component);
                string part=through;
                Pump(()=>grid.Items.Cast<Folder>().Any(f=>String.Equals(f.PathText,part,Engine.CI)),"Folder missing: "+part);
                grid.SelectedItem=grid.Items.Cast<Folder>().Single(f=>String.Equals(f.PathText,part,Engine.CI));
                if(!String.Equals(through,full,Engine.CI))PressEnter(grid);
            }
        }
        private static void VerifyUi(string preview)
        {
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            string eligible=Put(@"button\button.tmp","check",true),keep=Put(@"button\notes.txt","notes",true);
            string sibling=Put(@"button-copy\keep.tmp","sibling",true);
            string busyFile=Put(@"busy-app\in-use.tmp","active",true);
            var busyRule=R("busy-app",FileKind.OldTemp,7);
            using(var process=Process.GetCurrentProcess()) busyRule.Processes=new[]{process.ProcessName};
            var fixture=new Shell(()=>new List<Rule>{R("button",FileKind.OldTemp,7),R("shader",FileKind.Shader,0),R("button-copy",FileKind.OldTemp,7),busyRule});
            Window w=fixture.View;Hidden(w);var button=C<Button>(w,"CleanBtn");
            Pump(()=>button.IsEnabled,"Fixture UI scan: "+C<TextBlock>(w,"StatusText").Text);
            string other=Path.GetPathRoot(box).StartsWith("C",StringComparison.OrdinalIgnoreCase)?"DriveD":"DriveC";
            if(C<RadioButton>(w,other).IsEnabled) {
                C<RadioButton>(w,other).IsChecked=true;Expect(!button.IsEnabled,"Other disk scope excludes fixture");
                C<RadioButton>(w,"AllDrives").IsChecked=true;
            }
            Expect(button.IsEnabled,"One-click enabled for selected files");
            Expect(C<TabControl>(w,"Tabs").SelectedItem==C<TabItem>(w,"FoldersTab"),"Directory browser is the landing page");
            SelectPath(w,Inside("button"));
            Pump(()=>C<TextBlock>(w,"FolderSizeValue").Text=="10 B","Automatic folder size with personal and candidate files");
            Expect(C<TextBlock>(w,"FolderReadyValue").Text=="5 B","Cleanable size differs from total");
            PressEnter(C<DataGrid>(w,"FolderGrid"));
            Pump(()=>C<TextBlock>(w,"LocationText").Text==Inside("button") && C<StackPanel>(w,"FolderEmpty").Visibility==Visibility.Visible,"Empty leaf directory view");
            Expect(C<Button>(w,"RelatedBtn").IsEnabled && C<TextBlock>(w,"FolderTitle").Text=="button","A folder with only files keeps its cleanup action");
            C<Button>(w,"RelatedBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(C<TabControl>(w,"Tabs").SelectedItem==C<TabItem>(w,"CleanupTab") && C<DataGrid>(w,"CleanGrid").Items.Count==1 &&
                C<TextBlock>(w,"CleanScopeText").Text==Inside("button"),"Real directory action scopes cleanup");
            var grid=C<DataGrid>(w,"CleanGrid");
            var filter=C<ListBox>(w,"CleanFilter");
            var selectedSize=C<TextBlock>(w,"ChosenSize");
            filter.SelectedIndex=2;
            Expect(grid.Items.Count==0 && !button.IsEnabled && selectedSize.Text=="0 B","Category and directory restrictions intersect");
            filter.SelectedIndex=0;
            Expect(grid.Items.Count==1 && button.IsEnabled && selectedSize.Text=="5 B","Restoring all categories keeps directory scope and prior check");
            var toggle=grid.Items.Cast<ScanRow>().Single(x=>x.Rule.Kind==FileKind.OldTemp);
            grid.ScrollIntoView(toggle);grid.UpdateLayout();
            var check=Child<CheckBox>(grid.ItemContainerGenerator.ContainerFromItem(toggle));
            Expect(check!=null && check.IsEnabled,"Visible row has an actionable checkbox");
            var togglePeer=(IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle);
            togglePeer.Toggle();Expect(!button.IsEnabled,"Unchecking clears the selection");togglePeer.Toggle();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>C<Button>(w,"RefreshBtn").IsEnabled && C<TextBlock>(w,"StatusText").Text.StartsWith("本次已删除"),"UI cleanup did not finish");
            Expect(!File.Exists(eligible) && File.Exists(keep) && File.Exists(sibling) && File.Exists(Inside(@"shader\0123456789abcdef.nvph")),"Scoped one-click preserves other checked folders, personal files and optional files");
            C<Button>(w,"ClearScopeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(grid.Items.Count==4 && grid.Items.Cast<ScanRow>().Single(x=>x.Rule.Path==Inside("button-copy")).Pick,"Clearing scope restores other selections");
            filter.SelectedIndex=2;
            var optional=grid.Items.Cast<ScanRow>().Single();
            Expect(optional.Rule.Optional && !optional.Pick && !button.IsEnabled && selectedSize.Text=="0 B","Optional view excludes hidden checked regular files");
            optional.Pick=true;
            Expect(button.IsEnabled && selectedSize.Text=="3 B","Only opted-in visible shader contributes to selected bytes");
            filter.SelectedIndex=1;
            Expect(grid.Items.Cast<ScanRow>().All(r=>!r.Rule.Optional && !r.BusyApp) && button.IsEnabled && selectedSize.Text=="7 B","Default view counts the sibling cache only");
            filter.SelectedIndex=3;
            var busy=grid.Items.Cast<ScanRow>().Single();
            Expect(busy.BusyApp && !busy.CanPick && !button.IsEnabled && selectedSize.Text=="0 B","Busy-only view is informational");
            filter.SelectedIndex=0;
            Expect(grid.Items.Count==4 && grid.Items.Cast<ScanRow>().Count(r=>r.Pick)==2 && selectedSize.Text=="10 B","Returning to all restores both prior checks");
            filter.SelectedIndex=2;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>C<Button>(w,"RefreshBtn").IsEnabled,"Filtered opt-in cleanup did not finish");
            Expect(!File.Exists(Inside(@"shader\0123456789abcdef.nvph")) && File.Exists(keep) && File.Exists(sibling) && File.Exists(busyFile),"Filtering restricts actual deletion even when another category is checked");
            filter.SelectedIndex=0;
            Expect(grid.Items.Cast<ScanRow>().Single(r=>r.Rule.Path==Inside("button-copy")).Pick && selectedSize.Text=="7 B","Rescan preserves untouched hidden selection");
            Pump(()=>grid.ActualWidth>400 && grid.Columns[1].ActualWidth>grid.ActualWidth*0.4 && grid.Columns[2].ActualWidth>=50,"Cleanup columns fit");
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"FoldersTab");
            string drive=Path.GetPathRoot(box).Substring(0,2).ToUpperInvariant();
            C<RadioButton>(w,"Drive"+drive[0]).IsChecked=true;
            var list=C<DataGrid>(w,"FolderGrid");
            Pump(()=>list.Items.Count>0 && list.Items.Cast<Folder>().All(x=>x.Drive==drive),"Fixture drive filter");
            SelectPath(w,box);
            PressEnter(list);
            Pump(()=>C<TextBlock>(w,"LocationText").Text==box && list.Items.Cast<Folder>().Any(x=>x.Name=="backup"),"Fixture folder navigation");
            int allCount=list.Items.Count;
            C<ComboBox>(w,"PolicyBox").SelectedIndex=1;
            Expect(list.Items.Count>0 && list.Items.Cast<Folder>().All(x=>x.Related.Count>0),"Has cleanup locations filter");
            C<TextBox>(w,"FindBox").Text="___no_search_match___";
            Expect(list.Items.Count==0 && C<StackPanel>(w,"FolderEmpty").Visibility==Visibility.Visible,"Recoverable empty filter state");
            C<Button>(w,"ResetFilterBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(list.Items.Count==allCount,"Clearing filters restores directory list");
            C<TextBox>(w,"FindBox").Text="backup";
            var backup=list.Items.Cast<Folder>().Single(x=>x.Name=="backup");
            Expect(backup.Policy=="保留","Search shows reason to keep synthetic backup");
            list.SelectedItem=backup;
            PressEnter(list);
            Pump(()=>C<TextBlock>(w,"LocationText").Text==Inside("backup") && C<StackPanel>(w,"FolderEmpty").Visibility==Visibility.Visible,"Open fixture backup");
            C<Button>(w,"UpBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>C<TextBlock>(w,"LocationText").Text==box && list.Items.Count>0,"Parent navigation");
            w.Width=860;w.Height=740;w.UpdateLayout();
            Pump(()=>w.ActualWidth<865 && Grid.GetRow(C<Border>(w,"DetailPane"))==1 && list.ActualHeight>200,"Tall narrow layout moves details below the list");
            w.Width=810;w.Height=580;w.UpdateLayout();
            Pump(()=>w.ActualWidth<=811 && list.ActualHeight>=220 && list.Columns[0].ActualWidth>list.ActualWidth*0.48 && list.Columns[1].ActualWidth>=82,"Compact table keeps folder and policy labels readable");
            Expect(Grid.GetColumn(C<Border>(w,"DetailPane"))==2,"Short window keeps room for list rows");
            w.Close();

            if (!String.IsNullOrWhiteSpace(preview)) SaveRealPreviews(preview);
            app.Shutdown();
        }
        private static void SaveRealPreviews(string preview)
        {
            // Explicit local opt-in only. Screenshots may contain local account or folder names.
            // Never click the delete action in a window constructed with production rules.
            Directory.CreateDirectory(preview);
            Window w = new Shell(Catalog.Rules).View;
            Hidden(w);
            try
            {
                Pump(()=>C<TextBlock>(w,"StatusText").Text.StartsWith("扫描完成"),"Local preview scan");
                var live=C<DataGrid>(w,"CleanGrid").Items.Cast<ScanRow>().ToList();
                Expect(live.All(x=>!x.Rule.Optional || !x.Pick),"Optional production rules default off");
                C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"CleanupTab");
                Pump(()=>C<DataGrid>(w,"CleanGrid").ActualWidth>400,"Preview cleanup layout");
                Image(w,preview,"clean.png");
                C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"FoldersTab");
                Pump(()=>C<TextBlock>(w,"FolderCount").Text.Contains("个目录"),"Preview directory list");
                Image(w,preview,"folders.png");
                w.Width=810;w.Height=580;w.UpdateLayout();
                Pump(()=>w.ActualWidth<=811,"Compact preview layout");
                Image(w,preview,"compact.png");
                Console.WriteLine("Saved read-only local previews to the requested directory.");
            }
            finally {w.Close();}
        }
        private static void VerifyBuildVolume()
        {
            string stage=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(stage),@"^\.build-[0-9a-f]{32}$"))return;
            stage=CanonicalDirectory(stage);
            if(Engine.CheckDirectory(stage)!=Boundary.Clear)return;
            string test=Path.Combine(stage,"DiskScope-"+Guid.NewGuid().ToString("N"));
            if(!Engine.Below(test,stage)||Directory.Exists(test))throw new InvalidOperationException("Unexpected test path");
            Directory.CreateDirectory(test);
            string file=Path.Combine(test,"scope.tmp");
            try
            {
                File.WriteAllText(file,"check");File.SetLastWriteTimeUtc(file,Old);
                var r=new Rule{Path=test,Kind=FileKind.OldTemp,Days=7,Processes=new string[0],Name="test",Why="fixture"};
                var row=Engine.Scan(new[]{r},CancellationToken.None).Single();var done=Engine.Clean(new[]{row},CancellationToken.None);
                Expect(done[r.Path].Removed==1 && !File.Exists(file),"Cleanup on supported build volume in isolated fixture");
            }
            finally {if(File.Exists(file))File.Delete(file);Directory.Delete(test,false);}
        }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string parent=new[] {Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}
                    .Where(Directory.Exists).Select(CanonicalDirectory).FirstOrDefault(p=>Engine.CheckDirectory(p)==Boundary.Clear);
                if(parent==null)throw new IOException("The checks require a writable test directory on a supported fixed drive.");
                string fresh=Path.Combine(parent,"DiskGuideCheck-"+Guid.NewGuid().ToString("N"));
                if(!Engine.Below(fresh,parent)||Directory.Exists(fresh))throw new IOException("Invalid fixture root");
                Directory.CreateDirectory(fresh);
                box=fresh;
                System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
                VerifyEngine(); VerifyUsage(); VerifyBuildVolume(); VerifyUi(args.FirstOrDefault());Console.WriteLine("PASS: "+assertions+" checks");return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
            finally {if(box!=null && Directory.Exists(box))DeleteFixture(box);}
        }
    }
}
