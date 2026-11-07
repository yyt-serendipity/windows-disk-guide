using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml;

namespace DiskGuide
{
    internal sealed class TransferFile
    {
        internal string Relative,Digest;
        internal Entry Original;
    }
    internal sealed class TransferJob
    {
        internal string Id,Source,Destination,Backup,Phase="已检查",Message="";
        internal List<TransferFile> Files=new List<TransferFile>();
        internal long Bytes {get{return Files.Sum(f=>f.Original.Bytes);}}
    }
    internal sealed class TransferService
    {
        internal readonly string RecordPath;
        private readonly string fixtureRoot;
        internal TransferService(string record,string fixture=null){RecordPath=record;fixtureRoot=fixture;}
        internal IDisposable Acquire()
        {
            string dir=Path.GetDirectoryName(RecordPath),ancestor=dir;while(!Directory.Exists(ancestor))ancestor=Path.GetDirectoryName(ancestor);
            if(Engine.CheckDirectory(ancestor)!=Boundary.Clear)throw new IOException("迁移记录目录不可用。");Directory.CreateDirectory(dir);
            string path=RecordPath+".lock";if(Engine.CheckDirectory(dir)!=Boundary.Clear || File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("迁移记录锁路径无效。");
            try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){throw new IOException("另一窗口正在处理迁移，请等它完成后再打开。");}
        }
        internal static string Hash(Stream stream){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
        private static string Digest(string file,CancellationToken token)
        {
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,1048576,FileOptions.SequentialScan))
            using(var sha=SHA256.Create()){var buffer=new byte[1048576];int n;while((n=stream.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,n,buffer,0);}sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-","");}
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StreamInfo {internal long Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=296)]internal string Name;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr FindFirstStreamW(string path,int level,out StreamInfo data,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool FindNextStreamW(IntPtr handle,out StreamInfo data);
        [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr handle);
        private static void CheckStreams(string path)
        {
            StreamInfo data;var handle=FindFirstStreamW(path,0,out data,0);
            if(handle==new IntPtr(-1)){if(Marshal.GetLastWin32Error()==38)return;throw new IOException("无法核对文件的附加数据："+path);}
            try{do{if(data.Name!="::$DATA")throw new IOException("文件含附加数据流，已保留原件。请使用 Windows 文件移动或软件自己的迁移功能："+path);}while(FindNextStreamW(handle,out data));if(Marshal.GetLastWin32Error()!=38)throw new IOException("附加数据流核对未完成。");}finally{FindClose(handle);}
        }
        private bool Allowed(string source)
        {
            if(fixtureRoot!=null)return Engine.Below(source,fixtureRoot);
            if(!source.StartsWith(@"C:\",Engine.CI))return false;
            string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if(String.Equals(source,user,Engine.CI))return false;
            var forbidden=new[]{Environment.SpecialFolder.Windows,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,Environment.SpecialFolder.CommonApplicationData,Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.ApplicationData}.Select(Environment.GetFolderPath).Concat(new[]{@"C:\Recovery",@"C:\System Volume Information",@"C:\$Recycle.Bin",Path.Combine(user,".codex"),Path.Combine(user,".cache")}).Where(p=>!String.IsNullOrEmpty(p));
            if(forbidden.Any(p=>String.Equals(source,p,Engine.CI)||Engine.Below(source,p)))return false;
            var personal=new[]{Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.MyPictures,Environment.SpecialFolder.MyMusic,Environment.SpecialFolder.MyVideos}.Select(Environment.GetFolderPath).Where(p=>!String.IsNullOrEmpty(p)).Concat(new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")});
            return personal.Any(p=>!String.Equals(p.TrimEnd('\\'),Path.GetPathRoot(p).TrimEnd('\\'),Engine.CI) && Engine.Below(source,p));
        }
        private void Validate(TransferJob job,bool switched=false,bool checkSource=true)
        {
            Guid id;if(!Guid.TryParseExact(job.Id,"N",out id) || !Allowed(job.Source) || job.Source!=Path.GetFullPath(job.Source).TrimEnd('\\'))throw new IOException("迁移源范围无效，只支持个人资料目录中的子文件夹。");
            string expected=fixtureRoot!=null?Path.Combine(fixtureRoot,"destination-"+job.Id):Path.Combine(@"D:\DiskGuideMoved",job.Id,Path.GetFileName(job.Source));
            if(!String.Equals(job.Destination,expected,Engine.CI) || job.Backup!=job.Source+".DiskGuide-"+job.Id)throw new IOException("迁移记录的目标或原件路径无效。");
            if(checkSource){if(switched){if(!Engine.DirectoryResolvesTo(job.Source,job.Destination))throw new IOException("原路径没有正确指向 D 盘，保留原件。");}
            else if(Engine.CheckDirectory(job.Source)!=Boundary.Clear)throw new IOException("源目录包含链接、已被移动或无法访问。");}
            if(job.Files.Count>100000)throw new IOException("文件数量过多，请分批迁移子文件夹。");
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var f in job.Files)if(Path.IsPathRooted(f.Relative) || f.Relative.Split('\\').Any(s=>s==".." || s=="." || s.Length==0) || f.Relative.IndexOf(':')>=0 || !seen.Add(f.Relative) || !Engine.Below(Path.Combine(job.Source,f.Relative),job.Source))throw new IOException("文件清单范围无效。");
        }
        private static List<Entry> Tree(string root,CancellationToken token,Action<string> report)
        {
            var files=new List<Entry>();var pending=new Stack<string>();pending.Push(root);
            while(pending.Count>0){token.ThrowIfCancellationRequested();string dir=pending.Pop();
                if(Engine.CheckDirectory(dir)!=Boundary.Clear || (File.GetAttributes(dir)&(FileAttributes.Encrypted|FileAttributes.Offline))!=0)throw new IOException("目录含链接、加密或云端占位内容，保留原件："+dir);
                foreach(var item in new DirectoryInfo(dir).EnumerateFileSystemInfos()){
                    token.ThrowIfCancellationRequested();
                    if((item.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Encrypted|FileAttributes.Offline))!=0)throw new IOException("发现链接、加密或未下载文件，保留原件："+item.FullName);
                    if((item.Attributes&FileAttributes.Directory)!=0){pending.Push(item.FullName);continue;}
                    CheckStreams(item.FullName);var file=Engine.Snapshot(item.FullName);if(file==null)throw new IOException("文件为只读、硬链接、系统文件或不可访问，保留原件："+item.FullName);
                    using(var probe=new FileStream(item.FullName,FileMode.Open,FileAccess.Read,FileShare.None)){}
                    files.Add(file);if(files.Count>100000)throw new IOException("超过 10 万个文件，请分批选择子目录。");if(files.Count%100==0)report("已检查 "+files.Count+" 个文件");
                }
            }
            return files;
        }
        internal TransferJob Inspect(string source,KeepSnapshot keep,CancellationToken token,Action<string> report)
        {
            var job=new TransferJob{Id=Guid.NewGuid().ToString("N"),Source=PreserveStore.Normalize(source)};
            job.Destination=fixtureRoot!=null?Path.Combine(fixtureRoot,"destination-"+job.Id):Path.Combine(@"D:\DiskGuideMoved",job.Id,Path.GetFileName(job.Source));job.Backup=job.Source+".DiskGuide-"+job.Id;Validate(job);
            if(keep.BlocksTree(job.Source))throw new IOException("此文件夹包含保留规则，未迁移。请先在保留设置中核对。");
            foreach(var f in Tree(job.Source,token,report))job.Files.Add(new TransferFile{Relative=f.Path.Substring(job.Source.Length+1),Original=f});
            if(fixtureRoot==null){var d=new DriveInfo(@"D:\");if(!d.IsReady || d.DriveType!=DriveType.Fixed || d.DriveFormat!="NTFS" || d.AvailableFreeSpace<job.Bytes+512L*1024*1024)throw new IOException("D 盘需要为本地 NTFS 磁盘，并有足够空间及 512 MB 余量。");}
            Save(job);return job;
        }
        internal void Copy(TransferJob job,KeepSnapshot keep,CancellationToken token,Action<string> report)
        {
            Validate(job);if(keep.BlocksTree(job.Source))throw new IOException("源目录已设置保留，未复制。");
            if(Directory.Exists(job.Destination))throw new IOException("目标副本已存在。为避免覆盖，请保留它并重新选择任务，或继续已经完成的校验步骤。");
            string ancestor=Path.GetDirectoryName(job.Destination);while(!Directory.Exists(ancestor))ancestor=Path.GetDirectoryName(ancestor);
            if(Engine.CheckDirectory(ancestor)!=Boundary.Clear)throw new IOException("目标上级目录含链接或无法访问。");
            Directory.CreateDirectory(job.Destination);
            if(Engine.CheckDirectory(job.Destination)!=Boundary.Clear)throw new IOException("目标目录边界变化。");
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{WindowsIdentity.GetCurrent().User,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(job.Destination,acl);job.Phase="复制中";Save(job);int n=0;
            foreach(var f in job.Files){token.ThrowIfCancellationRequested();string target=Path.Combine(job.Destination,f.Relative);Directory.CreateDirectory(Path.GetDirectoryName(target));
                if(Engine.CheckDirectory(Path.GetDirectoryName(target))!=Boundary.Clear || Engine.CheckDirectory(Path.GetDirectoryName(f.Original.Path))!=Boundary.Clear)throw new IOException("复制路径发生变化。");
                Engine.CopyTransferFile(f.Original,target,token);report("已复制 "+(++n)+" / "+job.Files.Count+" 个文件");
            }
            // Preserve empty folders as well; never traverse links.
            CopyEmptyDirectories(job.Source,job.Destination,token);
            job.Phase="校验中";Save(job);Verify(job,job.Source,false,token,report);
            job.Phase="已复制并校验";job.Message="尚未切换路径，C 盘原件仍在。";Save(job);
        }
        private static void CopyEmptyDirectories(string source,string dest,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(Engine.CheckDirectory(source)!=Boundary.Clear || Engine.CheckDirectory(dest)!=Boundary.Clear)throw new IOException("目录边界变化。");foreach(var d in new DirectoryInfo(source).EnumerateDirectories()){if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("目录出现链接。");string target=Path.Combine(dest,d.Name);Directory.CreateDirectory(target);CopyEmptyDirectories(d.FullName,target,token);}}
        internal void Verify(TransferJob job,string original,bool allowMissing,CancellationToken token,Action<string> report)
        {
            if(Engine.CheckDirectory(job.Destination)!=Boundary.Clear)throw new IOException("副本目录不可用或包含链接。");
            var actual=Tree(original,token,report).ToDictionary(f=>f.Path.Substring(original.Length+1),StringComparer.OrdinalIgnoreCase);
            var expected=new HashSet<string>(job.Files.Select(f=>f.Relative),StringComparer.OrdinalIgnoreCase);
            if(actual.Keys.Any(p=>!expected.Contains(p)) || !allowMissing && actual.Count!=job.Files.Count)throw new IOException("原目录新增或减少了文件，请保留原件并重新检查。");
            int n=0;
            foreach(var f in job.Files){token.ThrowIfCancellationRequested();string target=Path.Combine(job.Destination,f.Relative);if(Engine.CheckDirectory(Path.GetDirectoryName(target))!=Boundary.Clear || Engine.Snapshot(target)==null)throw new IOException("副本路径无效。");CheckStreams(target);string hash=Digest(target,token);
                Entry a;if(actual.TryGetValue(f.Relative,out a)){
                    if(a.Bytes!=f.Original.Bytes || a.Modified!=f.Original.Modified || a.Volume!=f.Original.Volume || a.IndexHigh!=f.Original.IndexHigh || a.IndexLow!=f.Original.IndexLow || Digest(a.Path,token)!=hash)throw new IOException("原件与副本不一致，保留原件："+f.Relative);
                }
                if(f.Digest!=null && f.Digest!=hash)throw new IOException("已校验副本后来发生变化，保留原件："+f.Relative);
                f.Digest=hash;report("已校验 "+(++n)+" / "+job.Files.Count+" 个文件");
            }
        }
        private static void Junction(string source,string destination)
        {
            string script="$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+source.Replace("'","''")+"' -Target '"+destination.Replace("'","''")+"' | Out-Null";
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script))){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            using(var process=Process.Start(start)){process.WaitForExit();if(process.ExitCode!=0)throw new IOException("无法建立原路径入口，原件保留。");}
        }
        internal void Switch(TransferJob job,KeepSnapshot keep,CancellationToken token,Action<string> report)
        {
            Validate(job);if(job.Phase!="已复制并校验" || keep.BlocksTree(job.Source))throw new IOException("复制校验尚未完成，或源目录受保护。");
            Verify(job,job.Source,false,token,report);token.ThrowIfCancellationRequested();
            if(Directory.Exists(job.Backup))throw new IOException("回退目录已存在，未覆盖。");
            job.Phase="切换中";Save(job);Directory.Move(job.Source,job.Backup);
            try{Junction(job.Source,job.Destination);if(!Engine.DirectoryResolvesTo(job.Source,job.Destination))throw new IOException("路径验证失败。");}
            catch{if(Directory.Exists(job.Source) && Engine.DirectoryResolvesTo(job.Source,job.Destination))Directory.Delete(job.Source,false);if(!Directory.Exists(job.Source))Directory.Move(job.Backup,job.Source);job.Phase="切换失败，原件保留";job.Message="请核对原件位置："+(Directory.Exists(job.Backup)?job.Backup:job.Source);Save(job);throw;}
            job.Phase="等待使用验证";job.Message="请从原路径打开文件或项目，确认能正常使用。原件暂存于 "+job.Backup;Save(job);
        }
        internal void RemoveOriginal(TransferJob job,KeepSnapshot keep,CancellationToken token,Action<string> report)
        {
            Validate(job,true);bool resume=job.Phase=="删除原件中" || job.Phase=="原件清理未完成";
            if(!resume && job.Phase!="等待使用验证")throw new IOException("尚未完成路径切换。");
            if(keep.BlocksTree(job.Source)||keep.BlocksTree(job.Backup)||keep.BlocksTree(job.Destination))throw new IOException("原件或副本在保护范围内，未删除。");
            Verify(job,job.Backup,resume,token,report);job.Phase="删除原件中";Save(job);int n=0;
            foreach(var f in job.Files){token.ThrowIfCancellationRequested();string path=Path.Combine(job.Backup,f.Relative);if(!File.Exists(path)){if(resume)continue;throw new IOException("原件发生变化，停止删除。");}
                var original=new Entry{Path=path,Bytes=f.Original.Bytes,Modified=f.Original.Modified,Volume=f.Original.Volume,IndexHigh=f.Original.IndexHigh,IndexLow=f.Original.IndexLow};
                var result=Engine.DeleteVerifiedTransfer(original,Path.Combine(job.Destination,f.Relative),f.Digest);if(result.Code!=CleanCode.Deleted)throw new IOException(result.Reason);
                report("已处理原件 "+(++n)+" / "+job.Files.Count);
            }
            RemoveEmpty(job.Backup);job.Phase="迁移完成";job.Message="原路径已指向 D 盘，C 盘已验证原件已删除。请保留原路径入口和 D 盘正式数据。";Save(job);
        }
        private static void RemoveEmpty(string dir){if(Engine.CheckDirectory(dir)!=Boundary.Clear)throw new IOException("清理原件目录时边界变化。");foreach(var sub in Directory.GetDirectories(dir))RemoveEmpty(sub);Directory.Delete(dir,false);}
        internal void Restore(TransferJob job,CancellationToken token,Action<string> report)
        {
            Validate(job,true);if(job.Phase!="等待使用验证")throw new IOException("此阶段不能自动恢复原路径，请保留两份文件。");
            Verify(job,job.Backup,false,token,report);token.ThrowIfCancellationRequested();
            if(!Engine.DirectoryResolvesTo(job.Source,job.Destination))throw new IOException("入口已经变化，未恢复。");
            Directory.Delete(job.Source,false);
            try{Directory.Move(job.Backup,job.Source);}catch{if(!Directory.Exists(job.Source))Junction(job.Source,job.Destination);throw;}
            job.Phase="已恢复原路径";job.Message="C 盘原件已恢复，D 盘副本保留。";Save(job);
        }
        internal void Save(TransferJob job)
        {
            var doc=new XmlDocument();var root=doc.CreateElement("transfer");doc.AppendChild(root);
            foreach(var pair in new[]{new[]{"id",job.Id},new[]{"source",job.Source},new[]{"destination",job.Destination},new[]{"backup",job.Backup},new[]{"phase",job.Phase},new[]{"message",job.Message}})root.SetAttribute(pair[0],pair[1]??"");
            root.SetAttribute("bytes",XmlConvert.ToString(job.Bytes));
            foreach(var f in job.Files){var e=doc.CreateElement("file");root.AppendChild(e);e.SetAttribute("path",f.Relative);e.SetAttribute("hash",f.Digest??"");e.SetAttribute("bytes",XmlConvert.ToString(f.Original.Bytes));e.SetAttribute("modified",XmlConvert.ToString(f.Original.Modified));e.SetAttribute("volume",XmlConvert.ToString(f.Original.Volume));e.SetAttribute("high",XmlConvert.ToString(f.Original.IndexHigh));e.SetAttribute("low",XmlConvert.ToString(f.Original.IndexLow));}
            LocalDocument.Write(RecordPath,doc);
            // Preserve a durable record for each job, including interrupted copies.
            LocalDocument.Write(Path.Combine(Path.GetDirectoryName(RecordPath),"Transfers",job.Id+".xml"),doc);
        }
        internal static string Peek(string path)
        {
            if(!File.Exists(path))return null;
            try {
                if(Engine.CheckDirectory(Path.GetDirectoryName(path))!=Boundary.Clear || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException();
                using(var reader=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=64*1024*1024})){
                    reader.MoveToContent();if(reader.Name!="transfer")throw new IOException();long bytes=XmlConvert.ToInt64(reader.GetAttribute("bytes")??"0");if(bytes<0)throw new IOException();string phase=reader.GetAttribute("phase");if(phase=="复制中" || phase=="校验中" || phase=="删除原件中")phase="迁移未结束，打开核对进度";return phase+" · "+Engine.Size(bytes)+" · 点击继续查看";
                }
            } catch {return "上次迁移记录需要检查，点击查看。";}
        }
        internal TransferJob Load()
        {
            if(!File.Exists(RecordPath))return null;var doc=LocalDocument.Read(RecordPath,64*1024*1024);var root=doc.DocumentElement;if(root.Name!="transfer")throw new IOException("迁移记录无法识别。");
            var job=new TransferJob{Id=root.GetAttribute("id"),Source=root.GetAttribute("source"),Destination=root.GetAttribute("destination"),Backup=root.GetAttribute("backup"),Phase=root.GetAttribute("phase"),Message=root.GetAttribute("message")};
            foreach(XmlElement e in root.ChildNodes){string relative=e.GetAttribute("path"),hash=e.GetAttribute("hash");job.Files.Add(new TransferFile{Relative=relative,Digest=hash.Length==0?null:hash,Original=new Entry{Path=Path.Combine(job.Source,relative),Bytes=XmlConvert.ToInt64(e.GetAttribute("bytes")),Modified=XmlConvert.ToInt64(e.GetAttribute("modified")),Volume=XmlConvert.ToUInt32(e.GetAttribute("volume")),IndexHigh=XmlConvert.ToUInt32(e.GetAttribute("high")),IndexLow=XmlConvert.ToUInt32(e.GetAttribute("low"))}});}
            Validate(job,false,false);
            // Resolve a switch interrupted between renaming and recording completion.
            if(job.Phase=="切换中" && !Directory.Exists(job.Source) && Engine.CheckDirectory(job.Backup)==Boundary.Clear){Directory.Move(job.Backup,job.Source);job.Phase="已复制并校验";job.Message="中断的切换已恢复 C 盘原路径，原件未删除。";Save(job);}
            if(Engine.DirectoryResolvesTo(job.Source,job.Destination) && Directory.Exists(job.Backup) && job.Phase=="切换中")job.Phase="等待使用验证";
            if(job.Phase=="复制中" || job.Phase=="校验中")job.Phase="复制未完成";
            if(job.Phase=="删除原件中")job.Phase="原件清理未完成";
            Validate(job,Engine.DirectoryResolvesTo(job.Source,job.Destination));return job;
        }
    }
    internal sealed partial class Shell
    {
        private Window transferWindow;
        private bool transferRunning;
        private TransferJob transferJob;
        private CancellationTokenSource transferCancel;
        internal Func<string,TransferService> TransferFactory=file=>new TransferService(file);
        private void ShowTransfer()
        {
            if(transferWindow!=null){transferWindow.Activate();return;}
            var service=TransferFactory(Path.ChangeExtension(preserveStore.FilePath,"transfer.xml"));
            var dialog=DetailWindow("安全迁移到 D 盘",940,690);transferWindow=dialog;
            var panel=new StackPanel{Margin=new Thickness(22)};
            panel.Children.Add(new TextBlock{Text="需要保留的资料，搬到 D 盘",FontSize=23,FontWeight=FontWeights.SemiBold});
            panel.Children.Add(new TextBlock{Text="支持 C 盘桌面、文档、下载、图片等个人目录中的子文件夹。系统、软件安装目录和应用配置请使用各自的迁移功能。含链接、加密、云占位或附加数据流时会保留原件并说明原因。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,9,0,16)});
            var select=new Button{Content="选择要迁移的文件夹",HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(select);
            var source=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12)};System.Windows.Automation.AutomationProperties.SetName(source,"迁移源文件夹");panel.Children.Add(source);
            var stages=new TextBlock{Text="检查占用 → 复制 → 完整校验 → 切换路径 → 验证可用 → 删除原件",TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,5,0,15)};panel.Children.Add(stages);
            var status=new TextBlock{Text="请选择文件夹。还没有移动或删除任何文件。",FontSize=18,TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);
            var info=new TextBox{IsReadOnly=true,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,TextWrapping=TextWrapping.Wrap,MinHeight=100,MaxHeight=180,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,10,0,12)};panel.Children.Add(info);
            var bar=new ProgressBar{Height=4,IsIndeterminate=true,Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,12)};panel.Children.Add(bar);
            var buttons=new WrapPanel();var inspect=new Button{Content="检查大小与占用",Margin=new Thickness(0,0,8,8)};var copy=new Button{Content="复制并校验",Margin=new Thickness(0,0,8,8)};var link=new Button{Content="切换原路径",Margin=new Thickness(0,0,8,8)};var confirm=new Button{Content="我已验证可用，删除原件…",Margin=new Thickness(0,0,8,8)};var cancel=new Button{Content="取消当前操作",Margin=new Thickness(0,0,8,8)};
            foreach(var button in new[]{inspect,copy,link,confirm,cancel})buttons.Children.Add(button);panel.Children.Add(buttons);
            var open=new Button{Content="打开原路径验证文件",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,8)};panel.Children.Add(open);
            var restore=new Button{Content="验证不通过，恢复 C 盘原路径",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};panel.Children.Add(restore);
            var record=new TextBlock{Text="取消或失败不会自动删除副本。关闭窗口后进度仍会保存；正在执行时关闭窗口会先取消操作。",TextWrapping=TextWrapping.Wrap};panel.Children.Add(record);
            Action bind=()=>{
                select.IsEnabled=!transferRunning;inspect.IsEnabled=!transferRunning && source.Text.Length>0 && (transferJob==null || transferJob.Phase=="已检查" || transferJob.Phase=="复制未完成");copy.IsEnabled=!transferRunning && transferJob!=null && transferJob.Phase=="已检查";link.IsEnabled=!transferRunning && transferJob!=null && transferJob.Phase=="已复制并校验";confirm.IsEnabled=!transferRunning && transferJob!=null && (transferJob.Phase=="等待使用验证" || transferJob.Phase=="原件清理未完成");cancel.IsEnabled=transferRunning;open.IsEnabled=!transferRunning && transferJob!=null;bar.Visibility=transferRunning?Visibility.Visible:Visibility.Collapsed;
                inspect.Content=transferJob!=null && transferJob.Phase=="复制未完成"?"重新检查，保留未完成副本":"检查大小与占用";
                if(transferJob!=null){source.Text=transferJob.Source;info.Text=transferJob.Files.Count+" 个文件 · "+Engine.Size(transferJob.Bytes)+"\n目标："+transferJob.Destination+"\n"+transferJob.Message+(Directory.Exists(transferJob.Backup)?"\nC 盘回退原件："+transferJob.Backup:"");Label("HomeTransferText",transferJob.Phase+" · "+Engine.Size(transferJob.Bytes)+" · 点击继续查看");}
                restore.IsEnabled=!transferRunning && transferJob!=null && transferJob.Phase=="等待使用验证";
            };
            Action<string> pause=message=>{if(transferJob==null)return;if(transferJob.Phase=="复制中" || transferJob.Phase=="校验中")transferJob.Phase="复制未完成";if(transferJob.Phase=="删除原件中")transferJob.Phase="原件清理未完成";transferJob.Message=message;try{using(service.Acquire())service.Save(transferJob);}catch(Exception ex){status.Text+="\n状态未能保存："+ex.GetBaseException().Message;}};
            Func<Action<CancellationToken,Action<string>>,Task> run=async work=>{
                transferRunning=true;transferCancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);bind();Activity("transfer","正在处理迁移…");
                var watch=Stopwatch.StartNew();long last=0;bool started=false;var progress=new Progress<string>(s=>{status.Text=s;Activity("transfer",s);});
                try{await ScanWork.Run(()=>{using(service.Acquire()){started=true;work(transferCancel.Token,s=>{if(watch.ElapsedMilliseconds-last<100)return;last=watch.ElapsedMilliseconds;((IProgress<string>)progress).Report(s);});}return true;},transferCancel.Token);status.Text=transferJob.Phase;EndActivity("transfer",transferJob.Phase,false);}
                catch(OperationCanceledException){status.Text="已取消当前操作，原件或回退副本仍保留";if(started)pause(status.Text);EndActivity("transfer",status.Text,true);}
                catch(Exception ex){status.Text="尚未完成："+ex.GetBaseException().Message;if(started)pause(status.Text);EndActivity("transfer","迁移尚未完成，打开迁移窗口查看原因",true);}
                finally{transferRunning=false;transferCancel.Dispose();transferCancel=null;bind();}
            };
            select.Click+=delegate{if(transferJob!=null && Directory.Exists(transferJob.Backup)){status.Text="先完成或恢复当前任务，原件仍在回退目录中。";return;}string path=ChooseFolder("选择 C 盘个人目录中的子文件夹");if(path!=null){transferJob=null;source.Text=path;status.Text="已选择，尚未复制。";info.Clear();bind();}};
            inspect.Click+=async delegate{string path=source.Text;await run((token,report)=>{transferJob=service.Inspect(path,preserveStore.Load(),token,report);});};
            copy.Click+=async delegate{await run((token,report)=>service.Copy(transferJob,preserveStore.Load(),token,report));};
            link.Click+=async delegate{if(MessageBox.Show(dialog,"切换后，原来的 C 盘路径将指向 D 盘。请先退出使用此文件夹的应用。原件仍会保留，等你验证后再删除。","切换路径",MessageBoxButton.OKCancel)==MessageBoxResult.OK)await run((token,report)=>service.Switch(transferJob,preserveStore.Load(),token,report));};
            confirm.Click+=async delegate{if(MessageBox.Show(dialog,"你已打开迁移后的文件或项目，并确认能正常使用吗？\n将再次校验副本，然后永久删除 C 盘回退原件。校验失败或文件被占用时会停止。","确认可用后删除原件",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)await run((token,report)=>{using(var lease=preserveStore.Acquire())service.RemoveOriginal(transferJob,lease.Snapshot,token,report);});};
            cancel.Click+=delegate{if(transferCancel!=null){transferCancel.Cancel();status.Text="正在停止，原件继续保留…";}};
            restore.Click+=async delegate{if(MessageBox.Show(dialog,"恢复 C 盘原路径并保留 D 盘副本？如果两份内容已不同，会停止恢复，避免覆盖。","恢复原路径",MessageBoxButton.YesNo)==MessageBoxResult.Yes)await run((token,report)=>service.Restore(transferJob,token,report));};
            open.Click+=delegate{if(transferJob!=null)try{Process.Start(new ProcessStartInfo("explorer.exe","\""+transferJob.Source+"\""){UseShellExecute=true});}catch(Exception ex){status.Text=ex.Message;}};
            dialog.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(transferRunning){e.Cancel=true;transferCancel.Cancel();status.Text="正在停止，请稍候再关闭。";}};dialog.Closed+=delegate{transferWindow=null;};
            bool loadFailed=false;transferJob=null;
            try{using(service.Acquire())transferJob=service.Load();if(transferJob!=null)status.Text=transferJob.Phase;}catch(Exception ex){status.Text="上次迁移记录需要检查："+ex.GetBaseException().Message;loadFailed=true;}
            dialog.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};bind();if(loadFailed){select.IsEnabled=false;inspect.IsEnabled=false;}dialog.Show();
        }
    }
}
