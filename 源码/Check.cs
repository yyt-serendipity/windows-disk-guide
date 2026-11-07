using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
            var details=done.Values.SelectMany(v=>v.Details).ToList();
            Expect(details.Single(x=>x.PathText==locked).Code==CleanCode.Busy,"Locked file has an actionable busy result");
            Expect(details.Single(x=>x.PathText==modified).Code==CleanCode.Changed && details.Single(x=>x.PathText==swap).Code==CleanCode.Changed,"Changed and replaced files have specific results");
            Expect(details.Count(x=>x.Code==CleanCode.Deleted)==4 && details.All(x=>!String.IsNullOrEmpty(x.Reason)),"Every attempted file has a reason and totals match details");
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

        private static void VerifyPreserve()
        {
            var store=new PreserveStore(Inside(@"settings\keep-rules.xml"));
            Expect(store.Load().Error==null && store.Load().Rules.Count==0,"Missing settings means no exclusions");
            string protectedFile=Put(@"preserve\protected.tmp","keep",true), deleted=Put(@"preserve\delete.tmp","delete",true);
            string excluded=Put(@"preserve\excluded.tmp","exclude",true), missing=Put(@"preserve\missing.tmp","missing",true);
            var rule=R("preserve",FileKind.OldTemp,7);
            var rows=Engine.Scan(new[]{rule},CancellationToken.None);
            rows.Single().Files.Single(x=>x.Path==excluded).Included=false;File.Delete(missing);
            store.Add(protectedFile,false);
            var loaded=new PreserveStore(store.FilePath).Load();
            Expect(loaded.Contains(protectedFile) && !loaded.Contains(deleted),"Permanent file rule survives new store instance");
            var result=Engine.Clean(rows,CancellationToken.None,loaded)[rule.Path];
            Expect(result.Removed==1 && result.Preserved==2 && result.Skipped==1,"Per-file totals distinguish deletion, preservation and failure");
            Expect(result.Details.Single(x=>x.PathText==protectedFile).Code==CleanCode.Kept && File.Exists(protectedFile),"Rule added after scan prevents deletion");
            Expect(result.Details.Single(x=>x.PathText==excluded).Code==CleanCode.Excluded && File.Exists(excluded),"Temporary exclusion reported separately");
            Expect(result.Details.Single(x=>x.PathText==missing).Code==CleanCode.Missing,"Disappeared file has specific result");
            rows=Engine.Scan(new[]{rule},CancellationToken.None,loaded);
            var entry=rows.Single().Files.Single(x=>x.Path==protectedFile);var choice=new FileChoice(entry);choice.Included=true;
            Expect(entry.Preserved && !choice.Included && !choice.CanInclude,"Protected file cannot be reselected in preview");
            store.Add(Inside("preserve"),true);loaded=store.Load();
            Expect(loaded.Contains(Inside(@"preserve\future\new.tmp")) && !loaded.Contains(Inside(@"preserve-copy\new.tmp")),"Directory rules cover future descendants but not siblings");
            Expect(loaded.BlocksTree(box) && loaded.BlocksTree(Inside("preserve")) && !loaded.BlocksTree(Inside("preserve-copy")),"Residual protection covers ancestors and descendants only");
            var protectedRow=Engine.Scan(new[]{rule},CancellationToken.None,loaded).Single();
            Expect(!protectedRow.CanPick && protectedRow.EligibleBytes==0,"Entire protected group is not eligible");
            store.Remove(loaded.Rules.Single(x=>x.Directory));
            Expect(store.Load().Rules.Count==1 && store.Load().Contains(protectedFile),"Removing one rule keeps other rules");
            bool locked=false;
            using(var lease=store.Acquire()) {try{store.Add(excluded,false);}catch(IOException){locked=true;}}
            Expect(locked && store.Load().Rules.Count==1,"Active cleanup lease blocks concurrent setting mutation");
            store.Add(excluded,false);Expect(store.Load().Rules.Count==2,"Settings can be changed after cleanup lease ends");
            string physical=Inside("preserve-target"), alias=Inside("preserve-alias");Directory.CreateDirectory(physical);Junction(alias,physical);
            store.Add(Path.Combine(alias,"future"),true);loaded=store.Load();
            Expect(loaded.Contains(Path.Combine(physical,@"future\new.tmp")) && loaded.Contains(Path.Combine(alias,@"future\new.tmp")),"Rule saves original and resolved paths including future descendants");
            var rootRule=PreserveStore.Create(Path.GetPathRoot(box),true);
            Expect(rootRule.Matches(protectedFile),"Root directory preservation covers descendants");
            foreach(string invalid in new[]{"relative",@"E:\unsupported",@"C:\cache\*",@"C:\cache\?",@"C:\cache:stream"})
            {
                bool refused=false;try{PreserveStore.Normalize(invalid);}catch(ArgumentException){refused=true;}
                Expect(refused,"Reject ambiguous preserve path "+invalid);
            }
            foreach(string xml in new[]{"<broken", "<keepRules version='99'/>","<!DOCTYPE keepRules [<!ENTITY x 'data'>]><keepRules version='1'/>"})
            {
                File.WriteAllText(store.FilePath,xml);loaded=store.Load();
                Expect(loaded.Error!=null && loaded.Contains(deleted),"Invalid settings fail closed");
                var freshRows=Engine.Scan(new[]{rule},CancellationToken.None);
                result=Engine.Clean(freshRows,CancellationToken.None,loaded)[rule.Path];
                Expect(result.Removed==0 && result.Details.All(x=>x.Code==CleanCode.ConfigError) && File.Exists(protectedFile),"Invalid settings prevent deletion with explicit result");
                bool refused=false;try{store.Add(deleted,false);}catch(IOException){refused=true;}
                Expect(refused && File.ReadAllText(store.FilePath)==xml,"Invalid settings are never silently replaced");
            }
            result=Engine.Clean(Engine.Scan(new[]{rule},CancellationToken.None),new CancellationToken(true))[rule.Path];
            Expect(result.Removed==0 && result.Details.All(x=>x.Code==CleanCode.Cancelled),"Cancelled cleanup accounts for every remaining file");
            Expect(Engine.ErrorCode(5)==CleanCode.Permission && Engine.ErrorCode(32)==CleanCode.Busy && Engine.ErrorCode(2)==CleanCode.Missing,"Native error mapping distinguishes actionable failures");
            var linkedStore=new PreserveStore(Path.Combine(alias,@"new-settings\keep-rules.xml"));bool rejected=false;
            try{linkedStore.Add(protectedFile,false);}catch(IOException){rejected=true;}
            Expect(rejected && !Directory.Exists(Path.Combine(physical,"new-settings")),"Settings link rejected before creating any directories");
        }
        private static void VerifyScanWork()
        {
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cancel=new CancellationTokenSource())
            {
                int active=0,maximum=0;bool queuedRan=false;
                var first=ScanWork.Run(()=>{maximum=Math.Max(maximum,Interlocked.Increment(ref active));entered.Set();try{if(!release.Wait(10000))throw new TimeoutException();return 1;}finally{Interlocked.Decrement(ref active);}},CancellationToken.None);
                try
                {
                    Expect(entered.Wait(5000),"First scan starts");
                    var queued=ScanWork.Run(()=>{queuedRan=true;return 2;},cancel.Token);cancel.Cancel();
                    bool cancelled=false;try{queued.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
                    Expect(cancelled && !queuedRan,"Queued scan cancels without executing");
                    var next=ScanWork.Run(()=>{maximum=Math.Max(maximum,Interlocked.Increment(ref active));Interlocked.Decrement(ref active);return 3;},CancellationToken.None);
                    Expect(!next.IsCompleted,"Second scan waits for active scan");release.Set();
                    Expect(first.GetAwaiter().GetResult()==1 && next.GetAwaiter().GetResult()==3 && maximum==1,"Heavy scans execute serially");
                }
                finally{release.Set();first.GetAwaiter().GetResult();}
            }
            bool failed=false;try{ScanWork.Run<int>(()=>{throw new IOException("fixture");},CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){failed=true;}
            Expect(failed && ScanWork.Run(()=>7,CancellationToken.None).GetAwaiter().GetResult()==7,"Failed worker releases scan gate");
        }
        private static void VerifyKeepUi(Shell shell,PreserveStore store,string preview)
        {
            var w=shell.View;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"CleanupTab");
            InspectModal(w,()=>C<Button>(w,"KeepRulesBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                var input=Descendants<TextBox>(modal).Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="保留路径");
                input.Text=Inside("button-copy");
                Descendants<Button>(modal).Single(b=>Object.Equals(b.Content,"添加保留")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Expect(Child<DataGrid>(modal).Items.Count==1 && store.Load().Contains(Inside(@"button-copy\keep.tmp")),"Keep rules UI persists folder rule");
                if(!String.IsNullOrWhiteSpace(preview))Image(modal,preview,"keep-rules.png");
                modal.Width=620;modal.Height=420;modal.UpdateLayout();
                Expect(Child<DataGrid>(modal).ActualHeight>70,"Keep rules remain usable in a small window");
                if(!String.IsNullOrWhiteSpace(preview))Image(modal,preview,"keep-rules-compact.png");
            });
            var grid=C<DataGrid>(w,"CleanGrid");var row=grid.Items.Cast<ScanRow>().Single(x=>x.Rule.Path==Inside("button-copy"));grid.SelectedItem=row;
            Expect(!row.CanPick && row.Files.Single().Preserved,"New rule immediately protects existing snapshot");
            InspectModal(w,()=>C<Button>(w,"PreviewFilesBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                var files=Child<DataGrid>(modal);files.UpdateLayout();var check=Child<CheckBox>(files.ItemContainerGenerator.ContainerFromIndex(0));
                Expect(check!=null && !check.IsEnabled,"Protected preview checkbox is disabled");
                if(!String.IsNullOrWhiteSpace(preview))Image(modal,preview,"protected-preview.png");
            });
            InspectModal(w,()=>C<Button>(w,"KeepRulesBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                Descendants<Button>(modal).Single(b=>Object.Equals(b.Content,"移除选中规则")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Expect(Child<DataGrid>(modal).Items.Count==0 && store.Load().Rules.Count==0,"Keep rule removal updates durable state");
            });
            Expect(File.Exists(Inside(@"button-copy\keep.tmp")),"Rule changes never delete files");
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
            var uiStore=new PreserveStore(Inside(@"ui-settings\keep-rules.xml"));
            var fixture=new Shell(()=>new List<Rule>{R("button",FileKind.OldTemp,7),R("shader",FileKind.Shader,0),R("button-copy",FileKind.OldTemp,7),busyRule},uiStore);
            // Exercise both window classes even when the CI desktop initially clamps to a small work area.
            Window w=fixture.View;w.Width=1320;w.Height=840;Hidden(w);var button=C<Button>(w,"CleanBtn");
            Expect(C<RadioButton>(w,"DriveC").IsChecked==true || !Engine.ReadyFixed(@"C:\"),"Landing page starts at C root when available");
            string fixtureDrive="Drive"+Path.GetPathRoot(box)[0];
            C<RadioButton>(w,fixtureDrive).IsChecked=true;
            Pump(()=>button.IsEnabled,"Fixture UI scan: "+C<TextBlock>(w,"StatusText").Text);
            string other=Path.GetPathRoot(box).StartsWith("C",StringComparison.OrdinalIgnoreCase)?"DriveD":"DriveC";
            if(C<RadioButton>(w,other).IsEnabled) {
                C<RadioButton>(w,other).IsChecked=true;Expect(!button.IsEnabled,"Other disk scope excludes fixture");
                C<RadioButton>(w,fixtureDrive).IsChecked=true;
            }
            Expect(button.IsEnabled,"One-click enabled for selected files");
            Expect(C<TabControl>(w,"Tabs").TabStripPlacement==Dock.Left,"Wide window uses vertical navigation");
            var policyCombo=C<ComboBox>(w,"PolicyBox");policyCombo.ApplyTemplate();
            var comboPeer=new ComboBoxAutomationPeer(policyCombo);
            var comboAutomation=(IExpandCollapseProvider)comboPeer.GetPattern(PatternInterface.ExpandCollapse);
            comboAutomation.Expand();
            Pump(()=>policyCombo.IsDropDownOpen && policyCombo.ItemContainerGenerator.ContainerFromIndex(1)!=null,"Styled dropdown opens");
            var option=comboPeer.GetChildren().Select(p=>p.GetPattern(PatternInterface.SelectionItem)).OfType<ISelectionItemProvider>().ElementAt(1);
            option.Select();
            Expect(policyCombo.SelectedIndex==1,"Styled dropdown retains native accessible selection");
            comboAutomation.Collapse();Expect(!policyCombo.IsDropDownOpen,"Styled dropdown closes");policyCombo.SelectedIndex=0;
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
            Expect(C<Button>(w,"ResultsBtn").IsEnabled,"Cleanup exposes detailed results");
            InspectModal(w,()=>C<Button>(w,"ResultsBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                var resultGrid=Child<DataGrid>(modal);
                Expect(resultGrid.Items.Cast<FileOutcome>().Single().Code==CleanCode.Deleted,"Results window binds the actual cleanup outcome");
                if(!String.IsNullOrWhiteSpace(preview))Image(modal,preview,"cleanup-results.png");
                var search=Descendants<TextBox>(modal).Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="搜索清理结果");
                search.Text="___no_result___";Expect(resultGrid.Items.Count==0,"Result search filters rows");
                if(!String.IsNullOrWhiteSpace(preview))Image(modal,preview,"cleanup-empty.png");search.Clear();
                Child<ComboBox>(modal).SelectedIndex=1;Expect(resultGrid.Items.Count==0,"Result category excludes successful deletes");
            });
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
            C<Button>(w,"HomeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            Expect(C<TabControl>(w,"Tabs").TabStripPlacement==Dock.Top && C<TextBlock>(w,"LocationText").Text==box,"Compact navigation preserves the current directory");
            VerifyManagementUi(fixture, preview);
            VerifySpaceUi(fixture, preview);
            VerifyKeepUi(fixture,uiStore,preview);
            w.Close();

            if (!String.IsNullOrWhiteSpace(preview)) SaveRealPreviews(preview);
            app.Shutdown();
        }

        private static void VerifyManagement()
        {
            byte[] approved=new byte[12];approved[0]=2;
            Expect(StartupInventory.ApprovalState(approved).Contains("已启用"),"Startup enabled record");
            approved[0]=3;
            Expect(StartupInventory.ApprovalState(approved).Contains("已禁用"),"Startup disabled record");
            approved[0]=9;
            Expect(StartupInventory.ApprovalState(approved).Contains("未知") && StartupInventory.ApprovalState(null).Contains("未确认"),"Unknown startup state never implies enabled");
            Expect(StartupInventory.IsAutomaticUserService(2,16) && !StartupInventory.IsAutomaticUserService(2,1) && !StartupInventory.IsAutomaticUserService(3,16),"Only automatic Win32 services are listed");
            const string xml="<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers><Actions><Exec><Command>sample.exe</Command><Arguments>--startup</Arguments></Exec></Actions></Task>";
            var task=StartupInventory.ParseTask(xml,"Example task",@"\Example",false,4);
            Expect(task.State.Contains("任务已禁用") && task.State.Contains("当前运行中"),"Disabled task can still be running");
            var disabled=StartupInventory.ParseTask(xml.Replace("true","false"),"Example",@"\Example",true,3);
            Expect(disabled.State.Contains("触发器已禁用"),"Disabled logon trigger is distinguished from enabled task");
            Expect(StartupInventory.ParseTask(xml.Replace("LogonTrigger","TimeTrigger"),"Scheduled",@"\Scheduled",true,3)==null,"Scheduled-only tasks excluded from boot inventory");
            string exe,args,reason;
            Expect(SoftwareInventory.TryParseUninstall("\"C:\\Program Files\\Example\\uninstall.exe\" /remove",out exe,out args,out reason) && exe==@"C:\Program Files\Example\uninstall.exe" && args=="/remove","Quoted uninstall path parsed without shell");
            Expect(!SoftwareInventory.TryParseUninstall(@"C:\Windows\System32\cmd.exe /c echo danger",out exe,out args,out reason),"Command shell uninstall not executed");
            Expect(!SoftwareInventory.TryParseUninstall(@"\\server\share\uninstall.exe",out exe,out args,out reason),"Network uninstall target rejected");
            Expect(!SoftwareInventory.TryParseUninstall("\"C:\\Example\\uninstall.exe\"suffix",out exe,out args,out reason),"Malformed quoted command rejected");
            Expect(SoftwareInventory.TryParseUninstall("MsiExec.exe /I{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE} /quiet",out exe,out args,out reason) && args=="/x {aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee} /norestart","MSI install form becomes interactive exact product uninstall without reboot");
            Expect(SoftwareInventory.ResidualPathProblem(@"C:\")!=null && SoftwareInventory.ResidualPathProblem(@"D:\Apps")!=null,"Shared roots cannot be residual targets");
            Expect(SoftwareInventory.ResidualPathProblem(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))!=null,"Personal documents are protected");
            var owner=new SoftwareEntry {Name="Example",Id="fixture|example",InstallPath=@"C:\Program Files\DiskGuideFixture",UninstallCommand="example.exe"};
            var inventory=new SoftwareInventoryResult(); inventory.Items.Add(owner);
            Expect(!SoftwareInventory.PreviewResidual(owner,inventory).CanRecycle,"Registered software cannot be reclaimed as residual");
            inventory.Items.Clear();inventory.Warnings.Add("Read denied");
            Expect(!SoftwareInventory.PreviewResidual(owner,inventory).CanRecycle,"Incomplete inventory blocks residual cleanup");
            inventory.Warnings.Clear();inventory.Items.Add(new SoftwareEntry{Id="other",InstallPath=owner.InstallPath+@"\Shared"});
            Expect(!SoftwareInventory.PreviewResidual(owner,inventory).CanRecycle,"Other software nested below installation blocks residual cleanup");
            var changed=new SoftwareEntry{Name="Example",Id=owner.Id,InstallPath=owner.InstallPath,UninstallCommand="different.exe"};
            Expect(!SoftwareInventory.SameRegistration(owner,changed),"Changed uninstall command requires revalidation");
            string identityFile=Put(@"identity\sample.dll","data",true);
            string identity=SoftwareInventory.ReadIdentity(identityFile,false);
            File.Move(identityFile,identityFile+".old");File.WriteAllText(identityFile,"data");File.SetLastWriteTimeUtc(identityFile,Old);
            Expect(SoftwareInventory.ReadIdentity(identityFile,false)!=identity,"Residual identity detects same-size same-time file replacement");
            var approvedPlan=new ResidualPlan{CanRecycle=true};approvedPlan.Manifest.Add(identity);
            var newPlan=new ResidualPlan{CanRecycle=true};newPlan.Manifest.Add(SoftwareInventory.ReadIdentity(identityFile,false));
            Expect(!SoftwareInventory.MatchesApproved(approvedPlan,newPlan),"Changed residual identity invalidates approval");
        }

        private static void VerifySpace()
        {
            for(int i=1;i<=110;i++)Put("rank\\file-"+i.ToString("000")+".bin",new string('x',i),false);
            Put(@"rank\child\small.bin","abc",false);
            Junction(Inside(@"rank\linked"),Inside("decoy"));
            var ranking=FolderUsage.ReadRanking(Inside("rank"),CancellationToken.None,null);
            Expect(ranking.Total.Bytes==6108 && ranking.Total.Files==111 && ranking.Total.Skipped==1,"Ranking covers root files and descendants but excludes links");
            Expect(ranking.LargestFiles.Count==100 && ranking.LargestFiles.First().Bytes==110 && ranking.LargestFiles.Last().Bytes==11,"Largest file list is bounded and correctly ordered");
            Expect(ranking.Children.Single(x=>x.PathText!=null).Name=="child","Folder ranking keeps exact first-level groups");
            bool cancelled=false;try{FolderUsage.ReadRanking(Inside("rank"),new CancellationToken(true),null);}catch(OperationCanceledException){cancelled=true;}
            Expect(cancelled,"Ranking honors cancellation");
            Put(@"app-space\program.dll","12345",false);Put(@"app-space\cache\cache.bin","123",false);
            Junction(Inside("app-alias"),Inside("app-space"));
            var locations=new[]{new AppLocation{Category="安装文件",SourcePath=Inside("app-space"),Confirmed=true},
                new AppLocation{Category="别名",SourcePath=Inside("app-alias"),Confirmed=false},
                new AppLocation{Category="缓存",SourcePath=Inside(@"app-space\cache"),Confirmed=false}};
            var report=AppSpace.Read(locations,CancellationToken.None,null);
            string drive=Path.GetPathRoot(box).TrimEnd('\\');
            Expect(report.Bytes(drive,true)==8 && report.Bytes(drive,false)==0,"Software alias and nested cache are not double counted");
            Expect(report.Locations[1].PathText==Inside("app-space") && report.Locations[2].Usage.Bytes==3 && !report.Locations[2].Counted,"Physical target and nested measured size remain visible");
            var repeat=AppSpace.Read(locations,CancellationToken.None,null);
            Expect(repeat.Bytes(drive,true)==8,"Repeat measurement resets accounting state");
            Expect(AppSpace.IsSharedRoot(Path.GetPathRoot(box)) && AppSpace.IsSharedRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),"Shared root cannot count as one software footprint");
            var inventory=new SoftwareInventoryResult();
            var app=new SoftwareEntry{Name="Example",Id="fixture",InstallPath=Inside("app-space")};inventory.Items.Add(app);
            inventory.Items.Add(new SoftwareEntry{Id="shared",InstallPath=Inside(@"app-space\cache")});
            Expect(!AppSpace.Locate(app,inventory,new List<Rule>()).Single().Confirmed,"Overlapping installation records are not exclusive attribution");
            var unknown=new SoftwareEntry{Name="Unknown Portable",Id="unknown"};
            Expect(AppSpace.Locate(unknown,inventory,new List<Rule>()).Count==0,"Unknown app names do not trigger guessed folder scans");
            var package=new SoftwareEntry{Name="Example Store",PackageFullName="example",PackageFamilyName="example_publisher"};
            var located=AppSpace.Locate(package,inventory,new List<Rule>());
            Expect(located.Count==1 && located[0].Confirmed && located[0].SourcePath.EndsWith(@"Packages\example_publisher"),"Store data uses exact package family identity");
            string deleted=Put(@"file-choice\remove.tmp","delete",true),kept=Put(@"file-choice\keep.tmp","keep",true);
            var rule=R("file-choice",FileKind.OldTemp,7);var row=Engine.Scan(new[]{rule},CancellationToken.None).Single();
            var choices=row.Files.Select(x=>new FileChoice(x)).ToList();choices.Single(x=>x.PathText==kept).Included=false;
            Expect(row.SelectedCount==2,"Preview draft cannot change cleanup before Save");
            Shell.ApplyChoices(choices);Expect(row.SelectedCount==1 && row.SelectedBytes==6,"Saved file exclusions change selected count and bytes");
            var result=Engine.Clean(new[]{row},CancellationToken.None)[rule.Path];
            Expect(result.Removed==1 && result.Skipped==0 && !File.Exists(deleted) && File.Exists(kept),"Cleaner preserves excluded file and does not report it as failed");
        }

        private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
        {
            if(root is T)yield return (T)root;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
        }
        private static void InspectModal(Window owner,Action launch,Action<Window> verify)
        {
            Exception failure=null;bool seen=false;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            timer.Tick+=delegate{
                var modal=Application.Current.Windows.Cast<Window>().FirstOrDefault(x=>x.Owner==owner && x.IsVisible);
                if(modal==null)return;timer.Stop();seen=true;
                try{verify(modal);}catch(Exception ex){failure=ex;}finally{if(modal.IsVisible)modal.Close();}
            };
            timer.Start();try{launch();}finally{timer.Stop();}
            if(failure!=null)throw failure;Expect(seen,"Requested detail window opens");
        }
        private static void VerifySpaceUi(Shell shell,string preview)
        {
            Window w=shell.View;w.Width=1180;w.Height=800;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"FoldersTab");
            C<Button>(w,"HomeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SelectPath(w,Inside("rank"));
            C<Button>(w,"FolderRankBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>C<TextBlock>(w,"RankNotice").Text.StartsWith("已读"),"Scoped ranking finishes");
            Expect(C<TextBlock>(w,"RankPath").Text==Inside("rank") && C<DataGrid>(w,"RankGrid").Items.Count==1,"Folder action opens scoped ranking");
            C<ComboBox>(w,"RankKind").SelectedIndex=1;
            Expect(C<DataGrid>(w,"RankGrid").Items.Count==100 && !C<Button>(w,"RankDiveBtn").IsEnabled,"File ranking does not offer folder drill-down");
            C<TextBox>(w,"RankSearch").Text="file-110";
            Expect(C<DataGrid>(w,"RankGrid").Items.Count==1,"Ranking search operates on measured results");
            C<TextBox>(w,"RankSearch").Clear();if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"ranking.png");
            w.Width=810;w.Height=580;w.UpdateLayout();
            Pump(()=>w.ActualWidth<=811,"Compact ranking size settles");
            Expect(C<DataGrid>(w,"RankGrid").ActualHeight>150 && C<Button>(w,"RankScanBtn").IsVisible,"Ranking remains usable in a small window");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"ranking-compact.png");
            C<Button>(w,"RankRootBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(C<DataGrid>(w,"RankGrid").Items.Count==0 && C<TextBlock>(w,"RankTotal").Text=="尚未统计","Changing ranking scope discards old totals");
            w.Width=1180;w.Height=800;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"CleanupTab");
            var grid=C<DataGrid>(w,"CleanGrid");grid.SelectedItem=grid.Items.Cast<ScanRow>().Single(x=>x.Rule.Path==Inside("button-copy"));
            InspectModal(w,()=>C<Button>(w,"PreviewFilesBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                var files=Child<DataGrid>(modal);Pump(()=>files.Items.Count==1,"File preview binds exact snapshot");
                Expect(files.Items.Cast<FileChoice>().Single().PathText==Inside(@"button-copy\keep.tmp"),"Preview contains the selected cache project only");
                if(!String.IsNullOrWhiteSpace(preview))Image(modal,preview,"file-preview.png");
                files.Items.Cast<FileChoice>().Single().Included=false;
                Descendants<Button>(modal).Single(b=>Object.Equals(b.Content,"保存文件选择")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Expect(C<TextBlock>(w,"ChosenSize").Text=="0 B" && !C<Button>(w,"CleanBtn").IsEnabled,"Excluding all visible selected files disables cleanup");
            C<Button>(w,"RefreshBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>C<Button>(w,"RefreshBtn").IsEnabled,"Refresh after file exclusions");
            Expect(!grid.Items.Cast<ScanRow>().Single(x=>x.Rule.Path==Inside("button-copy")).Files.Single().Included,"Unchanged excluded file stays excluded after rescan");
            grid.SelectedItem=grid.Items.Cast<ScanRow>().Single(x=>x.Rule.Path==Inside("button-copy"));
            InspectModal(w,()=>C<Button>(w,"PreviewFilesBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                Child<DataGrid>(modal).Items.Cast<FileChoice>().Single().Included=true;
                modal.Close();
            });
            Expect(!grid.Items.Cast<ScanRow>().Single(x=>x.Rule.Path==Inside("button-copy")).Files.Single().Included,"Closing preview without Save discards edited choices");
            var inventory=new SoftwareInventoryResult();inventory.Items.Add(new SoftwareEntry{Name="示例应用",Id="space-fixture",InstallPath=Inside("app-space"),Kind="桌面软件",Publisher="Example"});shell.SoftwareReader=()=>inventory;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"SoftwareTab");C<Button>(w,"RefreshBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>C<DataGrid>(w,"SoftwareGrid").Items.Cast<SoftwareEntry>().Any(x=>x.Id=="space-fixture"),"Software footprint fixture binds");
            InspectModal(w,()=>C<Button>(w,"SoftwareSpaceBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                var paths=Child<DataGrid>(modal);Pump(()=>paths.Items.Count==1,"App footprint calculation finishes");
                Expect(paths.Items.Cast<AppLocation>().Single().Usage.Bytes==8,"Software footprint measures bytes instead of installer estimate");
                if(!String.IsNullOrWhiteSpace(preview))Image(modal,preview,"app-space.png");
            });
        }

        private static void VerifyManagementUi(Shell shell,string preview)
        {
            var startup=new StartupInventoryResult();
            startup.Items.Add(new StartupEntry{Name="示例笔记",Source="登录启动",State="已禁用（系统记录）",Command=@"D:\Apps\ExampleNotes\notes.exe --startup",Location=@"HKCU\Software\Example"});
            startup.Items.Add(new StartupEntry{Name="示例更新任务",Source="计划任务 · 登录",State="任务已禁用 · 当前运行中",Command="update.exe",Location=@"\Example\Update"});
            startup.Items.Add(new StartupEntry{Name="示例后台服务",Source="自动服务",State="自动（延迟启动）· 未读取运行状态",Command="service.exe",Location=@"HKLM\System\Example"});
            shell.StartupReader=()=>startup;
            var software=new SoftwareInventoryResult();
            software.Items.Add(new SoftwareEntry{Name="示例笔记",Id="fixture-notes",Kind="桌面软件",Publisher="Example Studio",Version="2.4.0",InstallPath=@"D:\Apps\ExampleNotes",SizeText="85 MiB",UninstallCommand="fixture-only.exe"});
            software.Items.Add(new SoftwareEntry{Name="示例系统组件",Id="fixture-system",Kind="商店应用",Publisher="Example",Version="1.0",SizeText="未登记",ProtectionReason="系统、框架或资源包，不在本工具中卸载。"});
            shell.SoftwareReader=()=>software;
            var w=shell.View;
            w.Width=1180;w.Height=800;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"StartupTab");
            Pump(()=>C<DataGrid>(w,"StartupGrid").Items.Count==3,"Startup inventory binds fixture data");
            Expect(C<StackPanel>(w,"DriveSwitcher").Visibility==Visibility.Collapsed,"Machine startup scope not falsely filtered by selected disk");
            C<ComboBox>(w,"StartupKind").SelectedIndex=2;
            Expect(C<DataGrid>(w,"StartupGrid").Items.Count==1 && C<TextBlock>(w,"StartupTitle").Text.Contains("当前运行中"),"Startup source filter and running state detail");
            C<TextBox>(w,"StartupSearch").Text="missing-entry";
            Expect(C<DataGrid>(w,"StartupGrid").Items.Count==0 && C<TextBlock>(w,"StartupEmpty").Visibility==Visibility.Visible,"Startup search has empty state");
            C<TextBox>(w,"StartupSearch").Clear();C<ComboBox>(w,"StartupKind").SelectedIndex=0;
            if(!String.IsNullOrWhiteSpace(preview)){Directory.CreateDirectory(preview);Image(w,preview,"startup.png");}
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"SoftwareTab");
            Pump(()=>C<DataGrid>(w,"SoftwareGrid").Items.Count==2,"Software inventory binds fixtures without reading real installs");
            Expect(C<Button>(w,"UninstallBtn").IsEnabled,"Eligible software offers explicit uninstall action");
            C<ComboBox>(w,"SoftwareKind").SelectedIndex=2;
            Expect(C<DataGrid>(w,"SoftwareGrid").Items.Count==1 && !C<Button>(w,"UninstallBtn").IsEnabled,"Protected package cannot launch uninstall");
            C<ComboBox>(w,"SoftwareKind").SelectedIndex=0;C<TextBox>(w,"SoftwareSearch").Text="D:\\Apps";
            Expect(C<DataGrid>(w,"SoftwareGrid").Items.Count==1,"Software search matches installation location");
            C<TextBox>(w,"SoftwareSearch").Clear();
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"software.png");
            w.Width=810;w.Height=580;w.UpdateLayout();
            Pump(()=>C<DataGrid>(w,"SoftwareGrid").ActualHeight>100,"Small window retains useful software rows");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"software-compact.png");
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"FoldersTab");
            Expect(C<StackPanel>(w,"DriveSwitcher").Visibility==Visibility.Visible,"Returning to disk view restores separate drives");
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
                w.Width=1180;w.Height=800;
                C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"StartupTab");
                Pump(()=>C<TextBlock>(w,"StartupCount").Text.EndsWith("项"),"Read-only startup inventory preview");
                Expect(C<DataGrid>(w,"StartupGrid").Items.Count>0,"Real startup inventory binds without changes");
                Image(w,preview,"startup-real.png");
                C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"SoftwareTab");
                Pump(()=>C<TextBlock>(w,"SoftwareCount").Text.EndsWith("项"),"Read-only software inventory preview");
                Expect(C<DataGrid>(w,"SoftwareGrid").Items.Count>0,"Real software inventory binds without uninstall");
                Image(w,preview,"software-real.png");
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
                VerifyEngine(); VerifyPreserve(); VerifyScanWork(); VerifyUsage(); VerifyBuildVolume(); VerifyManagement(); VerifySpace(); VerifyUi(args.FirstOrDefault());Console.WriteLine("PASS: "+assertions+" checks");return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
            finally {if(box!=null && Directory.Exists(box))DeleteFixture(box);}
        }
    }
}
