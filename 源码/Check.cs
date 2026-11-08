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
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
        private static int HitTest(Window window,FrameworkElement element,double x,double y)
        {
            Point p=element.PointToScreen(new Point(x,y));int packed=((int)p.X&65535)|(((int)p.Y&65535)<<16);
            return SendMessage(new System.Windows.Interop.WindowInteropHelper(window).Handle,0x84,IntPtr.Zero,new IntPtr(packed)).ToInt32();
        }

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
            var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){
                var bounds=new Rect(0,0,content.ActualWidth,content.ActualHeight);
                drawing.DrawRectangle(w.Background??Brushes.White,null,bounds);
            }
            image.Render(visual);image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
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
        private static void PressEnter(UIElement grid)
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
            // Request a wide fixture. Windows may also constrain the native window when it is shown.
            Window w=fixture.View;w.Width=1320;w.Height=840;Hidden(w);var button=C<Button>(w,"CleanBtn");
            Expect(C<RadioButton>(w,"DriveC").IsChecked==true || !Engine.ReadyFixed(@"C:\"),"Landing page starts at C root when available");
            string fixtureDrive="Drive"+Path.GetPathRoot(box)[0];
            C<RadioButton>(w,fixtureDrive).IsChecked=true;
            Pump(()=>button.IsEnabled,"Fixture UI scan: "+C<TextBlock>(w,"StatusText").Text);
            w.UpdateLayout();
            Expect(HitTest(w,C<Grid>(w,"AppLayout"),12,24)==2,"Integrated header retains native title drag and double-click hit area");
            var captionClose=C<Button>(w,"CloseBtn");
            Expect(HitTest(w,captionClose,22,16)==1 && HitTest(w,C<RadioButton>(w,"DriveC"),40,24)==1,"Caption controls and drive switch remain clickable");
            C<Button>(w,"MaximizeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>w.WindowState==WindowState.Maximized,"Caption maximize command");
            Expect((string)C<Button>(w,"MaximizeBtn").ToolTip=="还原","Maximized caption exposes restore action");
            C<Button>(w,"MaximizeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>w.WindowState==WindowState.Normal,"Caption restore command");
            C<Button>(w,"MinimizeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>w.WindowState==WindowState.Minimized,"Caption minimize command");
            SystemCommands.RestoreWindow(w);Pump(()=>w.WindowState==WindowState.Normal,"Restore after minimize");w.Left=-20000;w.Top=-20000;
            bool closeRequested=false;System.ComponentModel.CancelEventHandler stopClose=(sender,args)=>{closeRequested=true;args.Cancel=true;};w.Closing+=stopClose;
            captionClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>closeRequested,"Caption close uses cancellable window closing");w.Closing-=stopClose;
            string other=Path.GetPathRoot(box).StartsWith("C",StringComparison.OrdinalIgnoreCase)?"DriveD":"DriveC";
            if(C<RadioButton>(w,other).IsEnabled) {
                C<RadioButton>(w,other).IsChecked=true;Expect(!button.IsEnabled,"Other disk scope excludes fixture");
                C<RadioButton>(w,fixtureDrive).IsChecked=true;
            }
            Expect(button.IsEnabled,"One-click enabled for selected files");
            Expect(C<TabControl>(w,"Tabs").SelectedItem==C<TabItem>(w,"OverviewTab"),"Ordinary users land on the guided home page");
            Expect(C<TextBlock>(w,"HomeCleanSize").Text=="12 B","Home suggestion excludes optional, running-app and unrelated files");
            Expect(C<TextBlock>(w,"HomeDiskAdvice").Foreground.ToString()=="#FF20324A","Home text does not inherit selected navigation colors");
            Expect(C<TextBlock>(w,"HomeCleanHint").Text.Contains("先退出软件"),"Home explains why some cache cannot be cleaned yet");
            var featureSearch=C<TextBox>(w,"GlobalSearch");featureSearch.Text="迁移";
            Expect(C<ListBox>(w,"GlobalSearchResults").Items.Count==1,"Global feature search finds migration without starting it");
            featureSearch.Text="no-matching-feature";Expect(C<ListBox>(w,"GlobalSearchResults").Items.Count==0 && C<TextBlock>(w,"GlobalSearchEmpty").Visibility==Visibility.Visible,"Feature search explains an empty result");
            featureSearch.Text="文件夹";PressEnter(featureSearch);Pump(()=>C<TabControl>(w,"Tabs").SelectedItem==C<TabItem>(w,"FoldersTab"),"Feature search keyboard activation navigates to folders");
            Expect(File.Exists(eligible) && featureSearch.Text.Length==0,"Feature navigation clears the query and never cleans files");
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"OverviewTab");
            Pump(()=>C<Button>(w,"HomeProtectionBtn").IsVisible,"Return home after feature search");
            Expect(C<Button>(w,"HomeProtectionBtn").IsVisible && C<Button>(w,"HomeRecycleBtn").IsVisible && C<Button>(w,"HomeTransferBtn").IsVisible && C<Button>(w,"HomeGrowthBtn").IsVisible,"Home exposes protection, recycle, migration and growth actions");
            C<Button>(w,"HomeGrowthBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var growthWindow=Application.Current.Windows.Cast<Window>().Single(x=>x!=w && x.Title=="最近哪些位置又变大了");
            growthWindow.UpdateLayout();Expect(Descendants<DataGrid>(growthWindow).Single().Columns[0].ActualWidth>=150,"Growth history keeps a readable location column");
            if(!String.IsNullOrWhiteSpace(preview))Image(growthWindow,preview,"growth.png");growthWindow.Close();
            fixture.TransferFactory=file=>new TransferService(file,box);
            Put(@"ui-transfer\sample.txt","ui migration fixture",false);var uiTransfer=new TransferService(Path.ChangeExtension(uiStore.FilePath,"transfer.xml"),box);
            var uiJob=uiTransfer.Inspect(Inside("ui-transfer"),new KeepSnapshot(),CancellationToken.None,s=>{});uiTransfer.Copy(uiJob,new KeepSnapshot(),CancellationToken.None,s=>{});uiTransfer.Switch(uiJob,new KeepSnapshot(),CancellationToken.None,s=>{});
            C<Button>(w,"HomeTransferBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var transferWindow=Application.Current.Windows.Cast<Window>().Single(x=>x!=w && x.Title=="安全迁移到 D 盘");
            Expect(Descendants<TextBlock>(transferWindow).Any(x=>x.Text=="等待使用验证") && Descendants<Button>(transferWindow).Single(b=>Object.Equals(b.Content,"我已验证可用，删除原件…")).IsEnabled,"Migration UI resumes waiting for user validation without auto-deleting the original");
            Expect(Directory.Exists(uiJob.Backup),"Opening the migration UI does not remove a pending original");
            if(!String.IsNullOrWhiteSpace(preview))Image(transferWindow,preview,"migration.png");
            transferWindow.Width=650;transferWindow.Height=480;transferWindow.UpdateLayout();
            if(!String.IsNullOrWhiteSpace(preview))Image(transferWindow,preview,"migration-compact.png");transferWindow.Close();
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"home-fixture.png");
            C<Button>(w,"HomeCleanBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(C<TabControl>(w,"Tabs").SelectedItem==C<TabItem>(w,"CleanupTab") && C<ListBox>(w,"CleanFilter").SelectedIndex==1 && File.Exists(eligible),"Home action opens recommended choices without deleting anything");
            Expect(!C<Expander>(w,"CleanDetails").IsExpanded,"Technical cleanup details start folded while impact remains visible");
            C<ListBox>(w,"CleanFilter").SelectedIndex=0;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"OverviewTab");
            C<Button>(w,"HomeFoldersBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>C<DataGrid>(w,"FolderGrid").Items.Count>0,"Home opens folder explanations");
            Expect(C<TabControl>(w,"Tabs").TabStripPlacement==(w.ActualWidth>=1100 && w.ActualHeight>=680?Dock.Left:Dock.Top),"Navigation matches allocated window dimensions: "+w.ActualWidth+" x "+w.ActualHeight);
            var selectedTab=C<TabItem>(w,"FoldersTab");var otherTab=C<TabItem>(w,"RankingTab");selectedTab.ApplyTemplate();otherTab.ApplyTemplate();
            Expect(((Border)selectedTab.Template.FindName("tabLine",selectedTab)).Background.ToString()!=(((Border)otherTab.Template.FindName("tabLine",otherTab)).Background??Brushes.Transparent).ToString(),"Navigation visibly identifies the selected module");
            var policyCombo=C<ComboBox>(w,"PolicyBox");policyCombo.ApplyTemplate();
            var comboPeer=new ComboBoxAutomationPeer(policyCombo);
            var comboAutomation=(IExpandCollapseProvider)comboPeer.GetPattern(PatternInterface.ExpandCollapse);
            comboAutomation.Expand();
            Pump(()=>policyCombo.IsDropDownOpen && policyCombo.ItemContainerGenerator.ContainerFromIndex(1)!=null,"Styled dropdown opens");
            var option=comboPeer.GetChildren().Select(p=>p.GetPattern(PatternInterface.SelectionItem)).OfType<ISelectionItemProvider>().ElementAt(1);
            option.Select();
            Expect(policyCombo.SelectedIndex==1,"Styled dropdown retains native accessible selection");
            comboAutomation.Collapse();Expect(!policyCombo.IsDropDownOpen,"Styled dropdown closes");policyCombo.SelectedIndex=0;
            Expect(C<TabControl>(w,"Tabs").SelectedItem==C<TabItem>(w,"FoldersTab"),"Folder browser remains available from home");
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
            VerifyBackgroundScans(fixture,preview);
            VerifyKeepUi(fixture,uiStore,preview);
            w.Close();

            VerifyHomeStates(uiStore,preview);
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
            Expect(Enumerable.Range(0,16).All(code=>{
                var value=new byte[12];value[0]=(byte)code;
                var entry=new StartupEntry{Source="登录启动",State=StartupInventory.ApprovalState(value)};
                return entry.Group==(code==2 ? StartupGroup.Enabled : code==3 ? StartupGroup.Disabled : StartupGroup.Unconfirmed);
            }),"Only supported explicit approval encodings enter enabled and disabled categories");
            Expect(new StartupEntry{Source="用户启动文件夹",State=StartupInventory.ApprovalState(null)}.Group==StartupGroup.Unconfirmed &&
                new StartupEntry{Source="一次性登录启动",State="已登记 · 下次适用登录时执行"}.Group==StartupGroup.Unconfirmed,"Missing approval and one-time registrations remain unconfirmed");
            Expect(new[]{"自动服务","计划任务 · 开机","计划任务 · 登录"}.All(source=>!Shell.MatchStartup(new StartupEntry{Source=source,State="已启用（系统记录）"},"",false,0)),"Services and boot tasks cannot enter common enabled counts even if marked enabled");
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
        private static void VerifyBackgroundScans(Shell shell,string preview)
        {
            var w=shell.View;var tabs=C<TabControl>(w,"Tabs");
            string folder=Inside("background-scan");Put(@"background-scan\sample.bin","12345678",false);
            using(var started=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
            {
                CancellationToken active=CancellationToken.None;int calls=0;
                shell.FolderReader=(path,token,progress)=>{
                    if(String.Equals(path,folder,Engine.CI)){Interlocked.Increment(ref calls);active=token;started.Set();release.Wait(token);}
                    return FolderUsage.Read(path,token,progress);
                };
                try
                {
                    tabs.SelectedItem=C<TabItem>(w,"FoldersTab");C<Button>(w,"HomeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));SelectPath(w,folder);
                    Pump(()=>started.IsSet,"Directory scan enters worker");
                    tabs.SelectedItem=C<TabItem>(w,"CleanupTab");
                    Expect(!active.IsCancellationRequested && C<Button>(w,"ScanTasksBtn").IsVisible,"Directory scan remains active on another page");
                    C<Button>(w,"ScanTasksBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Expect(tabs.SelectedItem==C<TabItem>(w,"FoldersTab") && !active.IsCancellationRequested && calls==1,"Returning to directory scan does not restart it");
                    tabs.SelectedItem=C<TabItem>(w,"CleanupTab");
                    C<Button>(w,"RefreshBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Expect(!active.IsCancellationRequested,"Starting a cache scan does not cancel background directory work");
                    if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"background-tasks.png");
                    release.Set();Pump(()=>C<Button>(w,"RefreshBtn").IsEnabled,"Queued cache scan finishes after folder scan");
                    tabs.SelectedItem=C<TabItem>(w,"FoldersTab");
                    Pump(()=>C<TextBlock>(w,"FolderSizeValue").Text=="8 B","Completed background directory result restored");
                    Expect(calls==1,"Completed directory result retained after another page scan");
                }
                finally{release.Set();shell.FolderReader=FolderUsage.Read;}
            }
            using(var started=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
            {
                CancellationToken active=CancellationToken.None;int calls=0;
                shell.RankingReader=(path,token,progress)=>{Interlocked.Increment(ref calls);active=token;started.Set();release.Wait(token);return FolderUsage.ReadRanking(path,token,progress);};
                try
                {
                    C<Button>(w,"FolderRankBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>started.IsSet,"Ranking scan enters worker");
                    tabs.SelectedItem=C<TabItem>(w,"SoftwareTab");
                    Expect(!active.IsCancellationRequested,"Switching pages does not cancel running ranking");
                    C<Button>(w,"ScanTasksBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Expect(tabs.SelectedItem==C<TabItem>(w,"RankingTab") && calls==1 && !active.IsCancellationRequested,"Task shortcut returns to original running ranking");
                    tabs.SelectedItem=C<TabItem>(w,"OverviewTab");C<Button>(w,"HomeLargeBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Expect(tabs.SelectedItem==C<TabItem>(w,"RankingTab") && calls==1 && !active.IsCancellationRequested,"Home shortcut also preserves the active disk scan");
                    tabs.SelectedItem=C<TabItem>(w,"StartupTab");release.Set();
                    Pump(()=>C<TextBlock>(w,"RankNotice").Text.StartsWith("已读"),"Ranking completes while hidden");
                    tabs.SelectedItem=C<TabItem>(w,"RankingTab");
                    Expect(C<TextBlock>(w,"RankTotal").Text=="8 B" && calls==1,"Returning shows completed result without restarting");
                    started.Reset();release.Reset();C<Button>(w,"RankScanBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>started.IsSet,"Ranking restart enters worker");
                    C<Button>(w,"RankScanBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Expect(active.IsCancellationRequested,"Explicit cancel still stops ranking");Pump(()=>C<TextBlock>(w,"RankTotal").Text=="已取消","Cancelled ranking settles");
                    started.Reset();C<Button>(w,"RankScanBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>started.IsSet,"Ranking starts before scope change");
                    C<Button>(w,"RankRootBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Expect(active.IsCancellationRequested && C<TextBlock>(w,"RankTotal").Text=="尚未统计","Explicit range change cancels old work and clears its result");
                }
                finally{release.Set();shell.RankingReader=FolderUsage.ReadRanking;}
            }
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
            {
                tabs.SelectedItem=C<TabItem>(w,"FoldersTab");
                var blocker=ScanWork.Run(()=>{entered.Set();release.Wait();return true;},CancellationToken.None);
                try
                {
                    Pump(()=>entered.IsSet,"Queue fixture takes scan slot");
                    C<Button>(w,"FolderRankBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    tabs.SelectedItem=C<TabItem>(w,"SoftwareTab");Pump(()=>true,"Process navigation events");
                    Expect(Object.Equals(C<Button>(w,"RankScanBtn").Content,"取消统计"),"Queued ranking survives page navigation");
                    release.Set();Pump(()=>C<TextBlock>(w,"RankNotice").Text.StartsWith("已读"),"Queued ranking finishes in background");
                    Expect(C<TextBlock>(w,"RankTotal").Text=="8 B","Queued scan retains the requested directory");
                }
                finally{release.Set();blocker.GetAwaiter().GetResult();}
            }
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

        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process,int flags);
        private static void VerifyIcons()
        {
            string executable=System.Reflection.Assembly.GetExecutingAssembly().Location,path;int index;
            Expect(AppIcons.TrySource("\""+executable+"\", -12",false,out path,out index) && path==executable && index==-12,"Icon locations retain signed resource index and quoted paths");
            Expect(AppIcons.TrySource("\""+executable+"\" --startup",true,out path,out index) && path==executable,"Startup icon parser separates quoted executable from arguments");
            Expect(!AppIcons.TrySource(@"\\server\share\app.exe",false,out path,out index) && !AppIcons.TrySource("https://example.com/icon.png",false,out path,out index) && !AppIcons.TrySource(@"relative\app.exe",false,out path,out index),"Remote and relative icon locations are rejected");
            Expect(!AppIcons.TrySource(executable+":stream",false,out path,out index),"Alternate streams cannot supply icons");
            var item=new StartupEntry{Command="\""+executable+"\" --not-executed"};
            var image=AppIcons.Resolve(item);
            Expect(image.IsFrozen && !Object.ReferenceEquals(image,AppIcons.Fallback),"Native executable icon is copied into a frozen WPF image");
            Expect(Object.ReferenceEquals(image,AppIcons.Resolve(item)),"Resolved icon is reused from bounded cache");
            object scripting=null,shortcut=null;
            try
            {
                var type=Type.GetTypeFromProgID("WScript.Shell");scripting=Activator.CreateInstance(type);
                string link=Inside(@"icons\sample.lnk");Directory.CreateDirectory(Path.GetDirectoryName(link));
                shortcut=type.InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,scripting,new object[]{link});
                shortcut.GetType().InvokeMember("TargetPath",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{executable});
                shortcut.GetType().InvokeMember("IconLocation",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{executable+",0"});
                shortcut.GetType().InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,shortcut,null);
                Expect(!Object.ReferenceEquals(AppIcons.Resolve(new StartupEntry{Command=link}),AppIcons.Fallback),"Startup shortcut icon is read without running the shortcut");
            }
            finally {if(shortcut!=null)Marshal.FinalReleaseComObject(shortcut);if(scripting!=null)Marshal.FinalReleaseComObject(scripting);}
            bool changed=false; item.PropertyChanged+=(s,e)=>{if(e.PropertyName=="Icon")changed=true;};item.SetIcon(image);
            Expect(changed && Object.ReferenceEquals(item.Icon,image),"Icon arrival notifies the row binding without rebuilding inventory");
            Expect(Object.ReferenceEquals(AppIcons.Resolve(new SoftwareEntry{IconLocation=Inside("missing-icon.exe")}),AppIcons.Fallback),"Missing icon leaves a usable fallback");
            string bad=Put(@"icons\broken.png","invalid image",false);
            Expect(Object.ReferenceEquals(AppIcons.Resolve(new SoftwareEntry{IconLocation=bad}),AppIcons.Fallback),"Malformed artwork cannot fail inventory");
            string logo=Put(@"icons\package\Assets\Logo.scale-100.png","",false);
            var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())drawing.DrawImage(AppIcons.Fallback,new Rect(0,0,32,32));
            var bitmap=new RenderTargetBitmap(32,32,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(logo))png.Save(file);
            Put(@"icons\package\AppxManifest.xml","<Package><Properties><Logo>Assets\\Logo.png</Logo></Properties></Package>",false);
            var package=new SoftwareEntry{Kind="商店应用",InstallPath=Inside(@"icons\package")};
            Expect(!Object.ReferenceEquals(AppIcons.Resolve(package),AppIcons.Fallback),"Package manifest resolves scale-qualified local artwork");
            using(var file=File.Open(logo,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Expect(file.Length>0,"Bitmap loading releases the source file");
            Put(@"icons\untrusted\AppxManifest.xml","<!DOCTYPE Package [<!ENTITY ext SYSTEM 'file:///C:/Windows/win.ini'>]><Package><Properties><Logo>&ext;</Logo></Properties></Package>",false);
            Expect(Object.ReferenceEquals(AppIcons.Resolve(new SoftwareEntry{Kind="商店应用",InstallPath=Inside(@"icons\untrusted")}),AppIcons.Fallback),"External entities in package metadata are rejected");
            using(var process=Process.GetCurrentProcess())
            {
                int before=GetGuiResources(process.Handle,1);
                for(int i=0;i<32;i++)AppIcons.Resolve(new SoftwareEntry{IconLocation=executable,InstallPath="handle-check-"+i});
                Expect(GetGuiResources(process.Handle,1)<=before+4,"Repeated icon extraction releases native icon handles");
            }
        }
        private static void VerifyActivity(Shell shell,string preview)
        {
            var w=shell.View;var glyph=C<System.Windows.Shapes.Path>(w,"ActivityGlyph");
            shell.MotionAllowed=()=>true;
            shell.Activity("fixture-clean","正在清理测试文件…");
            Pump(()=>glyph.RenderTransform.HasAnimatedProperties,"Active operation starts feedback motion");
            Expect(C<TextBlock>(w,"StatusText").Visibility!=Visibility.Visible,"Animated feedback replaces the status line without overlapping text");
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"StartupTab");
            Expect(C<Border>(w,"ActivityPanel").IsVisible && glyph.RenderTransform.HasAnimatedProperties,"Operation feedback survives page navigation");
            shell.Activity("fixture-remove","正在回收测试残留…");
            Expect(C<TextBlock>(w,"ActivityState").Text=="2 项处理中","Concurrent operations are represented separately");
            shell.EndActivity("fixture-clean","测试清理已完成",false);
            Expect(glyph.RenderTransform.HasAnimatedProperties && C<TextBlock>(w,"ActivityText").Text.Contains("回收"),"One completion does not stop another operation animation");
            shell.MotionAllowed=()=>false;shell.Activity("fixture-remove","正在回收测试残留…");
            Expect(!glyph.RenderTransform.HasAnimatedProperties && C<TextBlock>(w,"ActivityState").Text=="处理中","Reduced motion keeps readable state without a looping animation");
            Expect(!C<Border>(w,"ActivityPanel").HasAnimatedProperties,"Reduced motion also stops the entry transition");
            shell.MotionAllowed=()=>true;shell.Activity("fixture-remove","正在回收测试残留…");
            w.WindowState=WindowState.Minimized;
            Expect(!glyph.RenderTransform.HasAnimatedProperties,"Minimizing stops background animation clocks");
            w.WindowState=WindowState.Normal;
            Pump(()=>glyph.RenderTransform.HasAnimatedProperties,"Restoring resumes only an active operation animation");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"operation-running.png");
            shell.EndActivity("fixture-remove","测试文件被占用，请查看结果",true);
            Expect(!glyph.RenderTransform.HasAnimatedProperties && C<TextBlock>(w,"ActivityState").Text=="请查看结果","Partial or failed operation stops animation and does not claim success");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"operation-attention.png");
            C<Button>(w,"ActivityDismiss").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(C<Border>(w,"ActivityPanel").Visibility==Visibility.Collapsed,"Settled operation result can be dismissed");
            Expect(C<TextBlock>(w,"StatusText").Visibility==Visibility.Visible,"Dismissing operation feedback restores the page status");
            shell.Activity("fixture-repeat","正在执行下一项测试…");shell.EndActivity("fixture-repeat","测试操作完成",false);
            Expect(C<TextBlock>(w,"ActivityState").Text=="已完成" && !glyph.RenderTransform.HasAnimatedProperties,"Repeated operation ends in a settled completed state");
            shell.MotionAllowed=()=>SystemParameters.ClientAreaAnimation;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"SoftwareTab");
        }
        private static void VerifyHomeSuggestions()
        {
            var normal=new ScanRow{Rule=new Rule{Path=@"C:\cache",Kind=FileKind.Web}};normal.Files.Add(new Entry{Bytes=10});normal.Files.Add(new Entry{Bytes=90,Preserved=true});
            var optional=new ScanRow{Rule=new Rule{Path=@"C:\shader",Kind=FileKind.Shader}};optional.Files.Add(new Entry{Bytes=200});
            var busyRow=new ScanRow{Rule=new Rule{Path=@"C:\busy",Kind=FileKind.Web},BusyApp=true};busyRow.Files.Add(new Entry{Bytes=300});
            var other=new ScanRow{Rule=new Rule{Path=@"D:\cache",Kind=FileKind.Web}};other.Files.Add(new Entry{Bytes=40});
            var unreadable=new ScanRow{Rule=new Rule{Path=@"C:\unreadable",Kind=FileKind.Web},Errors=1};
            var all=new[]{normal,optional,busyRow,other,unreadable};var result=CleanupSuggestion.From(all,"C:");
            Expect(result.Bytes==10 && result.Items==1 && result.BusyItems==1 && result.UnreadableItems==1,"Guided estimate separates eligible files, keep rules, optional cache and incomplete reads");
            Expect(result.OptionalBytes==200 && result.BusyBytes==300,"Caches requiring confirmation or closing apps are shown separately");
            Expect(CleanupSuggestion.From(all,"D:").Bytes==40,"Guided estimate follows the selected disk");
            Expect(new Folder{Policy="系统保护"}.Advice=="不要删除" && new Folder{Policy="需要判断"}.Advice=="先确认用途" && new Folder{Policy="可清部分"}.Advice=="只清缓存","Plain folder advice never labels a whole mixed folder safe to delete");
            Expect(Engine.Size(1024)=="1 KB" && Engine.Size(1024L*1024*1024)=="1 GB","Everyday size labels retain 1024-based measurements");
        }
        private static void VerifyHomeStates(PreserveStore store,string preview)
        {
            var empty=new Shell(()=>new List<Rule>(),store);Hidden(empty.View);
            try {
                Pump(()=>C<Button>(empty.View,"HomeCleanBtn").IsEnabled,"Empty home scan finishes");
                Expect(C<TextBlock>(empty.View,"HomeCleanSize").Text=="暂未发现可清理缓存" && C<TextBlock>(empty.View,"HomeCleanHint").Text.Contains("不代表"),"No cache result does not claim the entire disk is clean");
                if(!String.IsNullOrWhiteSpace(preview))Image(empty.View,preview,"home-empty.png");
            }finally{empty.View.Close();}
            Put(@"home-optional\0123456789abcdef.nvph","optional cache",true);
            var optional=new Shell(()=>new List<Rule>{R("home-optional",FileKind.Shader,0)},store);Hidden(optional.View);
            try {
                C<RadioButton>(optional.View,"Drive"+Path.GetPathRoot(box)[0]).IsChecked=true;
                Pump(()=>C<Button>(optional.View,"HomeCleanBtn").IsEnabled,"Optional-cache home scan finishes");
                Expect(C<TextBlock>(optional.View,"HomeCleanSize").Text=="还有缓存需要你确认","Optional cache is discoverable without claiming it is recommended");
                C<Button>(optional.View,"HomeCleanBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Expect(C<TabControl>(optional.View,"Tabs").SelectedItem==C<TabItem>(optional.View,"CleanupTab") && !C<Button>(optional.View,"CleanBtn").IsEnabled,"Home opens optional cache without automatically selecting it");
            }finally{optional.View.Close();}
            var failed=new Shell(()=>{throw new IOException("fixture unavailable");},store);Hidden(failed.View);
            try {
                Pump(()=>C<TextBlock>(failed.View,"HomeCleanHint").Text.Contains("没有完成"),"Home scan failure is actionable");
                Expect(C<TextBlock>(failed.View,"HomeCleanSize").Text=="还没有检查结果" && C<Button>(failed.View,"HomeCleanBtn").IsEnabled,"Failed scan cannot advertise zero cleanable bytes");
            }finally{failed.View.Close();}
        }
        private static void VerifyManagementUi(Shell shell,string preview)
        {
            var startup=new StartupInventoryResult();
            startup.Items.Add(new StartupEntry{Name="示例笔记",Source="登录启动",State="已启用（系统记录）",Command=@"D:\Apps\ExampleNotes\notes.exe --startup",Location=@"HKCU\Software\Example"});
            startup.Items.Add(new StartupEntry{Name="已关闭的示例软件",Source="登录启动",State="已禁用（系统记录）",Command="closed.exe",Location=@"HKCU\Software\Closed"});
            startup.Items.Add(new StartupEntry{Name="待确认的示例软件",Source="用户启动文件夹",State="已登记 · 启用状态未确认",Command="unknown.lnk",Location=@"D:\Fixture\Startup"});
            startup.Items.Add(new StartupEntry{Name="示例更新任务",Source="计划任务 · 登录",State="任务已禁用 · 当前运行中",Command="update.exe",Location=@"\Example\Update"});
            startup.Items.Add(new StartupEntry{Name="示例后台服务",Source="自动服务",State="自动（延迟启动）· 未读取运行状态",Command="service.exe",Location=@"HKLM\System\Example"});
            startup.Items[0].IconLocation=System.Reflection.Assembly.GetExecutingAssembly().Location;
            shell.StartupReader=()=>startup;
            var software=new SoftwareInventoryResult();
            software.Items.Add(new SoftwareEntry{Name="示例笔记",Id="fixture-notes",Kind="桌面软件",Publisher="Example Studio",Version="2.4.0",InstallPath=@"D:\Apps\ExampleNotes",SizeText="85 MB",UninstallCommand="fixture-only.exe"});
            software.Items.Add(new SoftwareEntry{Name="示例系统组件",Id="fixture-system",Kind="商店应用",Publisher="Example",Version="1.0",SizeText="未登记",ProtectionReason="系统、框架或资源包，不在本工具中卸载。"});
            software.Items[0].IconLocation=System.Reflection.Assembly.GetExecutingAssembly().Location;
            shell.SoftwareReader=()=>software;
            var w=shell.View;
            w.Width=1180;w.Height=800;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"StartupTab");
            Pump(()=>C<DataGrid>(w,"StartupGrid").Items.Count==1,"Startup defaults to confirmed enabled common apps");
            Expect(!C<Expander>(w,"StartupDetails").IsExpanded && !C<Button>(w,"ServicesBtn").IsVisible,"Technical startup details and service controls stay out of the default view");
            Expect(C<DataGrid>(w,"StartupGrid").Items.Cast<StartupEntry>().All(x=>!x.IsAdvanced && x.Group==StartupGroup.Enabled),"Default startup view excludes disabled, unknown and advanced records");
            Expect(new[]{0,1,2}.All(i=>C<TextBlock>(w,"StartupStateCount"+i).Text=="1") && C<TextBlock>(w,"StartupCount").Text=="1 项","State counts include only common records and do not display the global inventory total");
            Expect(!C<Grid>(w,"StartupAdvancedOptions").IsVisible,"Advanced categories stay hidden by default");
            Pump(()=>!Object.ReferenceEquals(startup.Items[0].Icon,AppIcons.Fallback),"Startup icon loads asynchronously after list binding");
            Expect(Descendants<System.Windows.Controls.Image>(C<DataGrid>(w,"StartupGrid")).Any(i=>Object.ReferenceEquals(i.Source,startup.Items[0].Icon)),"Startup row displays the resolved icon");
            Expect(C<StackPanel>(w,"DriveSwitcher").Visibility==Visibility.Collapsed,"Machine startup scope not falsely filtered by selected disk");
            var stateList=C<ListBox>(w,"StartupState");
            stateList.SelectedIndex=1;
            Expect(C<DataGrid>(w,"StartupGrid").Items.Cast<StartupEntry>().Single().Group==StartupGroup.Disabled,"Closed category shows only confirmed disabled records");
            C<TextBox>(w,"StartupSearch").Text="已关闭";Expect(C<DataGrid>(w,"StartupGrid").Items.Count==1,"Search recognizes the displayed plain-language status");C<TextBox>(w,"StartupSearch").Clear();
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"startup-disabled.png");
            stateList.SelectedIndex=2;
            Expect(C<DataGrid>(w,"StartupGrid").Items.Cast<StartupEntry>().Single().Group==StartupGroup.Unconfirmed && C<TextBlock>(w,"StartupTitle").Text.Contains("待确认"),"Uncertain records remain distinct from enabled or disabled software");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"startup-unconfirmed.png");
            C<Button>(w,"StartupAdvancedBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(C<DataGrid>(w,"StartupGrid").Items.Count==1 && C<TextBlock>(w,"StartupTitle").Text.Contains("当前运行中"),"Startup source filter and running state detail");
            Expect(!stateList.IsVisible && C<Grid>(w,"StartupAdvancedOptions").IsVisible && !C<Button>(w,"StartupSettingsBtn").IsVisible,"Advanced mode keeps task management separate from common software");
            Pump(()=>C<DataGrid>(w,"StartupGrid").Columns.Where(c=>c.Visibility==Visibility.Visible).Sum(c=>c.ActualWidth)<=C<DataGrid>(w,"StartupGrid").ActualWidth,"Advanced startup columns fit after switching categories");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"startup-advanced.png");
            C<ComboBox>(w,"StartupKind").SelectedIndex=1;
            Expect(C<DataGrid>(w,"StartupGrid").Items.Cast<StartupEntry>().Single().IsService && C<Button>(w,"ServicesBtn").IsVisible && !C<Button>(w,"TaskSettingsBtn").IsVisible,"Advanced services have their own list and management entry");
            C<TextBox>(w,"StartupSearch").Text="missing-entry";
            Expect(C<DataGrid>(w,"StartupGrid").Items.Count==0 && C<TextBlock>(w,"StartupEmpty").Visibility==Visibility.Visible,"Startup search has empty state");
            C<Button>(w,"StartupAdvancedBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(stateList.SelectedIndex==2 && C<DataGrid>(w,"StartupGrid").Items.Cast<StartupEntry>().Single().Group==StartupGroup.Unconfirmed && !C<Button>(w,"ServicesBtn").IsVisible,"Returning from advanced restores the common category and clears its search");
            C<TextBox>(w,"StartupSearch").Text="not-found";
            Expect(C<TextBlock>(w,"StartupCount").Text=="0 项" && C<TextBlock>(w,"StartupStateCount2").Text=="1","Searching does not overwrite category totals with filtered counts");
            C<TextBox>(w,"StartupSearch").Clear();stateList.SelectedIndex=0;
            if(!String.IsNullOrWhiteSpace(preview)){Directory.CreateDirectory(preview);Image(w,preview,"startup.png");}
            w.Width=810;w.Height=580;w.UpdateLayout();Pump(()=>C<DataGrid>(w,"StartupGrid").ActualHeight>100,"Compact startup categories leave usable list space");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"startup-compact.png");w.Width=1180;w.Height=800;
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"SoftwareTab");
            Pump(()=>C<DataGrid>(w,"SoftwareGrid").Items.Count==2,"Software inventory binds fixtures without reading real installs");
            Pump(()=>!Object.ReferenceEquals(software.Items[0].Icon,AppIcons.Fallback),"Software icon loads asynchronously after list binding");
            Expect(Descendants<System.Windows.Controls.Image>(C<DataGrid>(w,"SoftwareGrid")).Any(i=>Object.ReferenceEquals(i.Source,software.Items[0].Icon)),"Software row displays the resolved icon");
            Expect(C<Button>(w,"UninstallBtn").IsEnabled,"Eligible software offers explicit uninstall action");
            Expect(!C<Expander>(w,"SoftwareDetails").IsExpanded && C<TextBlock>(w,"SoftwareAdvice").Text.Contains("重要文件"),"Software guidance is visible before opening technical details");
            C<ComboBox>(w,"SoftwareKind").SelectedIndex=2;
            Expect(C<DataGrid>(w,"SoftwareGrid").Items.Count==1 && !C<Button>(w,"UninstallBtn").IsEnabled,"Protected package cannot launch uninstall");
            C<ComboBox>(w,"SoftwareKind").SelectedIndex=0;C<TextBox>(w,"SoftwareSearch").Text="D:\\Apps";
            Expect(C<DataGrid>(w,"SoftwareGrid").Items.Count==1,"Software search matches installation location");
            C<TextBox>(w,"SoftwareSearch").Clear();
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"software.png");
            VerifyUninstallResult(shell,software,preview);
            VerifyActivity(shell,preview);
            w.Width=810;w.Height=580;w.UpdateLayout();
            Pump(()=>C<DataGrid>(w,"SoftwareGrid").ActualHeight>100,"Small window retains useful software rows");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"software-compact.png");
            C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"FoldersTab");
            Expect(C<StackPanel>(w,"DriveSwitcher").Visibility==Visibility.Visible,"Returning to disk view restores separate drives");
        }
        private static void VerifyUninstallResult(Shell shell,SoftwareInventoryResult inventory,string preview)
        {
            var w=shell.View;
            Expect(w.FindName("UninstallProgress")==null && w.FindName("CheckUninstallBtn")==null && w.FindName("CloseUninstallBtn")==null,"Legacy uninstall banner and manual check/close actions are removed");
            var owner=new SoftwareEntry{Name="示例软件 1.0",Id="fixture-removed",InstallPath=null};
            shell.ResidualReader=(entry,current)=>new ResidualPlan{Owner=entry,Reason="没有登记可确认的本地安装目录。"};
            var check=shell.CheckUninstall(owner);Pump(()=>check.IsCompleted,"Automatic uninstall check completes without a result dialog");check.GetAwaiter().GetResult();
            Expect(C<ScrollViewer>(w,"UninstallResult").IsVisible && C<TextBlock>(w,"UninstallResultTitle").Text.Contains(owner.Name) && C<TextBlock>(w,"UninstallStatus").Text.Contains("无法判断是否还有残留"),"Result appears inline and missing location is not presented as no leftovers");
            Expect(w.OwnedWindows.Count==0 && !C<Button>(w,"ReviewResidualBtn").IsVisible && C<Button>(w,"UninstallBtn").IsEnabled,"No-result-action flow releases the next uninstall without a close-record step");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"uninstall-result.png");
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
            {
                var reader=shell.SoftwareReader;
                shell.SoftwareReader=()=>{entered.Set();if(!release.Wait(10000))throw new TimeoutException();return inventory;};
                try {
                    var loading=shell.RefreshSoftware();Pump(()=>entered.IsSet,"Concurrent software refresh enters reader");
                    var automatic=shell.CheckUninstall(owner);
                    Expect(!automatic.IsCompleted,"Automatic uninstall result waits for an ongoing inventory refresh instead of being dropped");
                    release.Set();Pump(()=>loading.IsCompleted && automatic.IsCompleted,"Queued automatic result completes after inventory refresh");
                    loading.GetAwaiter().GetResult();automatic.GetAwaiter().GetResult();
                    Expect(C<TextBlock>(w,"UninstallStatus").Text.StartsWith("已从软件列表中移除"),"Concurrent refresh still produces the uninstall result");
                }finally{release.Set();shell.SoftwareReader=reader;}
            }
            var plan=new ResidualPlan{Owner=owner,Path=Inside("residual-preview-only"),CanRecycle=true,Bytes=1024,FileCount=1,Reason="测试安装残留"};plan.Files.Add(Path.Combine(plan.Path,"sample.dll"));
            shell.ResidualReader=(entry,current)=>plan;
            check=shell.CheckUninstall(owner);Pump(()=>check.IsCompleted,"Eligible leftovers are shown inline without automatic confirmation");check.GetAwaiter().GetResult();
            Expect(C<Button>(w,"ReviewResidualBtn").IsVisible && w.OwnedWindows.Count==0,"Residual preview requires its own explicit action");
            InspectModal(w,()=>C<Button>(w,"ReviewResidualBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),modal=>{
                Expect(modal.Title=="卸载结果与残留预览","Explicit residual action opens the existing review before any removal");
            });
            Pump(()=>C<Button>(w,"UninstallBtn").IsEnabled,"Cancelling residual review restores software actions");
            Expect(C<Button>(w,"ReviewResidualBtn").IsVisible,"Cancelling preview leaves the result available without deleting anything");
            inventory.Items.Add(owner);
            check=shell.CheckUninstall(owner);Pump(()=>check.IsCompleted,"Still-installed check completes");check.GetAwaiter().GetResult();
            Expect(C<TextBlock>(w,"UninstallStatus").Text.Contains("暂未确认卸载完成") && !C<Button>(w,"ReviewResidualBtn").IsVisible,"Still-installed software is not reported as successfully uninstalled");
            inventory.Items.Remove(owner);
            shell.ResidualReader=(entry,current)=>new ResidualPlan{Owner=entry,Reason="没有登记可确认的本地安装目录。"};
            check=shell.RefreshSoftware();Pump(()=>check.IsCompleted,"Normal refresh updates an unresolved uninstall");check.GetAwaiter().GetResult();
            Expect(C<TextBlock>(w,"UninstallStatus").Text.StartsWith("已从软件列表中移除"),"Normal refresh updates the inline result without restoring a check-result button");
            inventory.Warnings.Add("fixture read failed");
            shell.ResidualReader=(entry,current)=>{throw new InvalidOperationException("Must not inspect residuals with incomplete inventory");};
            check=shell.CheckUninstall(owner);Pump(()=>check.IsCompleted,"Incomplete inventory returns inline warning");check.GetAwaiter().GetResult();
            Expect(C<TextBlock>(w,"UninstallStatus").Text.Contains("暂时无法确认") && !C<Button>(w,"ReviewResidualBtn").IsVisible,"Incomplete reads cannot offer leftover cleanup or claim success");
            inventory.Warnings.Clear();
            shell.ResidualReader=(entry,current)=>{throw new IOException("fixture preview unavailable");};
            check=shell.CheckUninstall(owner);Pump(()=>check.IsCompleted,"Residual inspection error settles");check.GetAwaiter().GetResult();
            Expect(C<TextBlock>(w,"UninstallStatus").Text.Contains("检查没有完成") && C<Button>(w,"UninstallBtn").IsEnabled,"Check failure remains visible and does not block unrelated uninstall actions");
            shell.ResidualReader=SoftwareInventory.PreviewResidual;
            check=shell.CheckUninstall(owner);Pump(()=>check.IsCompleted,"Restore missing-location result");check.GetAwaiter().GetResult();
            w.Width=810;w.Height=580;w.UpdateLayout();
            Expect(C<DataGrid>(w,"SoftwareGrid").ActualHeight>100,"Inline result leaves usable software rows in a compact window");
            Pump(()=>C<DataGrid>(w,"SoftwareGrid").Columns.Where(c=>c.Visibility==Visibility.Visible).Sum(c=>c.ActualWidth)<=C<DataGrid>(w,"SoftwareGrid").ActualWidth,"Compact result layout settles without horizontal overflow");
            if(!String.IsNullOrWhiteSpace(preview))Image(w,preview,"uninstall-result-compact.png");
            w.Width=1180;w.Height=800;
        }
        private static void SaveRealPreviews(string preview)
        {
            // Explicit local opt-in only. Screenshots may contain local account or folder names.
            // Never click the delete action in a window constructed with production rules.
            Directory.CreateDirectory(preview);
            var shell=new Shell(Catalog.Rules,new PreserveStore(Inside(@"real-preview-settings\keep-rules.xml")));Window w = shell.View;
            Hidden(w);
            try
            {
                Pump(()=>C<TextBlock>(w,"StatusText").Text.StartsWith("扫描完成"),"Local preview scan");
                var live=C<DataGrid>(w,"CleanGrid").Items.Cast<ScanRow>().ToList();
                Expect(live.All(x=>!x.Rule.Optional || !x.Pick),"Optional production rules default off");
                Image(w,preview,"home.png");
                w.Width=810;w.Height=580;w.UpdateLayout();Image(w,preview,"home-compact.png");
                w.Width=1180;w.Height=800;
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
                Expect(C<DataGrid>(w,"StartupGrid").Items.Cast<StartupEntry>().All(i=>!i.IsAdvanced && i.Group==StartupGroup.Enabled),"Real default startup view contains only confirmed enabled common records");
                Pump(()=>shell.IconsIdle,"Startup icon queue settles before local preview");
                Image(w,preview,"startup-real.png");
                C<TabControl>(w,"Tabs").SelectedItem=C<TabItem>(w,"SoftwareTab");
                Pump(()=>C<TextBlock>(w,"SoftwareCount").Text.EndsWith("项"),"Read-only software inventory preview");
                Expect(C<DataGrid>(w,"SoftwareGrid").Items.Count>0,"Real software inventory binds without uninstall");
                Pump(()=>C<DataGrid>(w,"SoftwareGrid").Items.Cast<SoftwareEntry>().Any(i=>!Object.ReferenceEquals(i.Icon,AppIcons.Fallback)),"Real software icons load");
                Pump(()=>shell.IconsIdle,"Software icon queue settles before local preview");
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
        private static void Refuses(Action action,string label)
        {bool refused=false;try{action();}catch(IOException){refused=true;}catch(UnauthorizedAccessException){refused=true;}Expect(refused,label);}
        private static void VerifyPlanningAndTransfer()
        {
            var nvidia=R("package-rule",FileKind.DriverDownload,7);
            Expect(nvidia.Accepts(Path.Combine(nvidia.Path,@"grd\abc\610.62-notebook-win10-win11.exe"),Old,DateTime.UtcNow),"Known old driver downloads are recognized");
            Expect(!nvidia.Accepts(Path.Combine(nvidia.Path,"personal.exe"),Old,DateTime.UtcNow) && !nvidia.Accepts(Path.Combine(nvidia.Path,"610.62-driver.exe"),DateTime.UtcNow,DateTime.UtcNow) && nvidia.Optional,"Unknown executables and recent updates stay out of cleanup; update downloads require opt-in");
            var npm=R("npm-rule",FileKind.NpmContent,7);string cachePath=Path.Combine(npm.Path,@"ab\cd\"+new string('a',124));
            Expect(npm.Accepts(cachePath,Old,DateTime.UtcNow) && !npm.Accepts(Path.Combine(npm.Path,".npmrc"),Old,DateTime.UtcNow),"npm cache hashes cannot include credentials or arbitrary files");
            var yarn=R("yarn-rule",FileKind.YarnArchive,7);Expect(yarn.Accepts(Path.Combine(yarn.Path,"example.zip"),Old,DateTime.UtcNow) && !yarn.Accepts(Path.Combine(yarn.Path,"data.sqlite"),Old,DateTime.UtcNow),"Yarn rule preserves databases and sources");
            Expect(new FileOutcome{Code=CleanCode.Busy}.NextStep.Contains("退出") && new FileOutcome{Code=CleanCode.Kept}.NextStep.Contains("保护"),"Cleanup failures explain recovery without treating protection as failure");
            Expect(Shell.SpaceChange(100,90).Contains("减少") && Shell.SpaceChange(null,90).Contains("无法"),"Net disk changes never become fabricated cleanup savings");
            var historyPath=Inside(@"planning\growth.xml");var history=new GrowthStore(historyPath);var now=DateTime.UtcNow;
            history.Record("cache-one",Inside("cache-one"),"测试缓存",100,now);Expect(history.Records[0].ChangeText=="首次记录","First growth record is a baseline");history.Save();history=new GrowthStore(historyPath);
            history.Record("cache-one",Inside("cache-one"),"测试缓存",250,now.AddMinutes(5));history.Save();history=new GrowthStore(historyPath);Expect(history.Records[0].Delta==150 && history.Records[0].Previous==now,"Growth survives restart and compares equal scopes");
            history.Record("cache-one",Inside("cache-one"),"测试缓存",900,now);Expect(history.Records[0].Bytes==250,"Older observations cannot overwrite a newer growth baseline");
            Put(@"transfer-source\hello.txt","important original",false);Directory.CreateDirectory(Inside(@"transfer-source\empty"));
            var service=new TransferService(Inside(@"planning\transfer.xml"),box);var keep=new KeepSnapshot();Action<string> report=s=>{};var token=CancellationToken.None;
            Refuses(()=>service.Inspect(Path.GetPathRoot(box),keep,token,report),"Drive roots cannot be migrated");
            var protectedKeep=new KeepSnapshot();protectedKeep.Rules.Add(PreserveStore.Create(Inside("transfer-source"),true));Refuses(()=>service.Inspect(Inside("transfer-source"),protectedKeep,token,report),"Protected data cannot be moved by the wizard");
            var job=service.Inspect(Inside("transfer-source"),keep,token,report);service.Copy(job,keep,token,report);
            using(service.Acquire())Refuses(()=>service.Acquire(),"Only one migration operation can hold the record lock");
            Expect(job.Phase=="已复制并校验" && File.Exists(Inside(@"transfer-source\hello.txt")) && Directory.Exists(Path.Combine(job.Destination,"empty")),"Copy verifies content, preserves empty folders and retains all original files");
            string target=Path.Combine(job.Destination,"hello.txt");File.WriteAllText(target,"changed copy");Refuses(()=>service.Switch(job,keep,token,report),"A modified destination blocks path switching");Expect(Engine.CheckDirectory(job.Source)==Boundary.Clear,"Failed verification leaves original path intact");
            File.WriteAllText(target,"important original");service.Switch(job,keep,token,report);job=service.Load();
            Expect(job.Phase=="等待使用验证" && Engine.DirectoryResolvesTo(job.Source,job.Destination) && File.Exists(Path.Combine(job.Backup,"hello.txt")),"Restart restores pending user verification, not a false completed state");
            using(var held=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Refuses(()=>service.RemoveOriginal(job,keep,token,report),"An in-use copy blocks original removal");
            File.WriteAllText(Path.Combine(job.Backup,"late.txt"),"new data");Refuses(()=>service.RemoveOriginal(job,keep,token,report),"New data in the original is never silently dropped");File.Delete(Path.Combine(job.Backup,"late.txt"));
            service.RemoveOriginal(job,keep,token,report);Expect(job.Phase=="迁移完成" && !Directory.Exists(job.Backup) && File.ReadAllText(Path.Combine(job.Source,"hello.txt"))=="important original","Only verified originals are removed; original path still opens the D-style destination");
            Put(@"transfer-restore\hello.txt","restore me",false);var restore=service.Inspect(Inside("transfer-restore"),keep,token,report);service.Copy(restore,keep,token,report);service.Switch(restore,keep,token,report);service.Restore(restore,token,report);
            Expect(Engine.CheckDirectory(restore.Source)==Boundary.Clear && File.Exists(Path.Combine(restore.Source,"hello.txt")) && File.Exists(Path.Combine(restore.Destination,"hello.txt")),"Rollback restores the source while retaining the copy");
            Put(@"transfer-interrupt\hello.txt","interrupted",false);var interrupted=service.Inspect(Inside("transfer-interrupt"),keep,token,report);service.Copy(interrupted,keep,token,report);interrupted.Phase="切换中";service.Save(interrupted);Directory.Move(interrupted.Source,interrupted.Backup);
            interrupted=service.Load();Expect(interrupted.Phase=="已复制并校验" && File.Exists(Path.Combine(interrupted.Source,"hello.txt")),"An interrupted rename recovers the original path before continuing");
            Directory.CreateDirectory(Inside("transfer-link"));Junction(Inside(@"transfer-link\outside"),Inside("decoy"));Refuses(()=>service.Inspect(Inside("transfer-link"),keep,token,report),"Migration never traverses nested junctions");
            Put(@"transfer-cancel\hello.txt","retained",false);var cancelled=service.Inspect(Inside("transfer-cancel"),keep,token,report);var cts=new CancellationTokenSource();cts.Cancel();bool stopped=false;try{service.Copy(cancelled,keep,cts.Token,report);}catch(OperationCanceledException){stopped=true;}
            Expect(stopped && File.Exists(Inside(@"transfer-cancel\hello.txt")),"Cancelled copying leaves original content intact");cts.Dispose();
            Expect(service.Load().Phase=="复制未完成","Restart cannot present an interrupted copy as running or completed");
            Put(@"transfer-changed\hello.txt","before",false);var changed=service.Inspect(Inside("transfer-changed"),keep,token,report);File.WriteAllText(Path.Combine(changed.Source,"hello.txt"),"modified after inspection");Refuses(()=>service.Copy(changed,keep,token,report),"Source identity and modification time are checked on the copy handle");
            Directory.CreateDirectory(Inside("transfer-hardlink"));Expect(CreateHardLink(Inside(@"transfer-hardlink\shared.txt"),Inside(@"transfer-changed\hello.txt"),IntPtr.Zero),"Create a hard-linked fixture");Refuses(()=>service.Inspect(Inside("transfer-hardlink"),keep,token,report),"Hard links cannot silently become destructive migration originals");
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
                VerifyEngine(); VerifyPreserve(); VerifyScanWork(); VerifyUsage(); VerifyBuildVolume(); VerifyManagement(); VerifySpace(); VerifyIcons(); VerifyHomeSuggestions(); VerifyPlanningAndTransfer(); VerifyUi(args.FirstOrDefault());Console.WriteLine("PASS: "+assertions+" checks");return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
            finally {if(box!=null && Directory.Exists(box))DeleteFixture(box);}
        }
    }
}
