using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskGuide
{
    internal enum FileKind { Web, OldTemp, OldLog, Shader, Dump }
    internal enum Boundary { Clear, Missing, Link, Inaccessible }

    internal sealed class Rule
    {
        public string Path, Name, Why;
        public FileKind Kind;
        public int Days;
        public string[] Processes;
        public string OriginalAppPath, PhysicalAppPath;
        public bool HasValidOrigin()
        {
            if (OriginalAppPath == null && PhysicalAppPath == null) return true;
            return OriginalAppPath != null && PhysicalAppPath != null &&
                   Engine.Below(Path, PhysicalAppPath) && Engine.DirectoryResolvesTo(OriginalAppPath, PhysicalAppPath);
        }
        public bool Optional { get { return Kind == FileKind.Shader || Kind == FileKind.Dump; } }
        private static readonly Regex WebFile = new Regex(@"^(?:index|data_[0-3]|f_[0-9a-f]{6,12}|[0-9a-f]{16}_(?:[0-9]+|s))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ShaderFile = new Regex(@"^[0-9a-f]{8,64}\.nvph$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public bool Accepts(string full, DateTime modified, DateTime now)
        {
            if (!Engine.Below(full, Path) || modified > now.AddDays(-Days)) return false;
            string relative = full.Substring(Path.TrimEnd('\\').Length + 1);
            string name = System.IO.Path.GetFileName(relative);
            string sub = System.IO.Path.GetDirectoryName(relative) ?? "";
            if (Kind == FileKind.Web)
                return sub.Length == 0 ? WebFile.IsMatch(name) :
                    sub.Equals("index-dir", StringComparison.OrdinalIgnoreCase) && name.Equals("the-real-index", StringComparison.OrdinalIgnoreCase);
            if (Kind == FileKind.OldLog)
                return (sub.Length == 0 || sub.Equals("service", StringComparison.OrdinalIgnoreCase)) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
            if (sub.Length != 0) return false;
            if (Kind == FileKind.Shader) return ShaderFile.IsMatch(name);
            if (Kind == FileKind.Dump) return name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
            return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase);
        }

        public bool AllowsSubdirectory(string full)
        {
            if (!Engine.Below(full, Path)) return false;
            string rel = full.Substring(Path.TrimEnd('\\').Length + 1);
            return (Kind == FileKind.Web && rel.Equals("index-dir", StringComparison.OrdinalIgnoreCase)) ||
                   (Kind == FileKind.OldLog && rel.Equals("service", StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class Entry
    {
        public string Path;
        public long Bytes, Modified;
        public uint Volume, IndexHigh, IndexLow;
        public bool Included = true;
        public bool Preserved;
        public string Identity { get { return Path + "|" + Volume + "|" + IndexHigh + "|" + IndexLow + "|" + Bytes + "|" + Modified; } }
    }

    internal sealed class ScanRow : INotifyPropertyChanged
    {
        public Rule Rule;
        public readonly List<Entry> Files = new List<Entry>();
        public int Errors;
        public bool BusyApp;
        public long Bytes { get; set; }
        public long EligibleBytes { get { return Files.Where(f=>!f.Preserved).Sum(f=>f.Bytes); } }
        public long SelectedBytes { get { return Files.Where(f => f.Included && !f.Preserved).Sum(f => f.Bytes); } }
        public int SelectedCount { get { return Files.Count(f => f.Included && !f.Preserved); } }
        public string Previous = "";
        public string Name { get { return Rule.Name; } }
        public string PathText { get { return Rule.Path; } }
        public string Drive { get { return PathText.Substring(0, 2).ToUpperInvariant(); } }
        public string SizeText { get { return Engine.Size(Bytes); } }
        public bool CanPick { get { return !BusyApp && Files.Any(f=>!f.Preserved); } }
        public string Status { get { return BusyApp ? "先退出应用" : Files.Count == 0 ? (Errors > 0 ? "访问受限" : "无符合文件") : Files.All(f=>f.Preserved) ? "保留规则保护" : Rule.Optional ? "手动勾选" : "可一键清理"; } }
        public string Ink { get { return BusyApp || Rule.Optional ? "#92520D" : CanPick ? "#006C61" : "#637389"; } }
        public string Detail { get { return Rule.Why + "  符合规则 " + Files.Count.ToString("N0") + " 个文件。" +
                    (BusyApp ? " 检测到相关程序正在运行，退出后请重新扫描。" : "") +
                    (Errors > 0 ? " 有 " + Errors + " 个位置无法读取或已跳过。" : "") + Previous; } }
        private bool pick;
        public bool Pick { get { return pick; } set { bool v = value && CanPick; if (pick == v) return; pick = v; var e = PropertyChanged; if (e != null) e(this, new PropertyChangedEventArgs("Pick")); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class Outcome
    {
        public long Bytes;
        public int Removed, Skipped, Preserved;
        public readonly List<FileOutcome> Details=new List<FileOutcome>();
        public string Text { get { return "已删除 " + Removed.ToString("N0") + " 个文件，共 " + Engine.Size(Bytes) + "，跳过 " + Skipped.ToString("N0") + " 个"+(Preserved>0?"，保留 "+Preserved+" 个":"")+"。"; } }
        public void Add(FileOutcome item)
        {
            Details.Add(item);
            if(item.Code==CleanCode.Deleted){Removed++;Bytes+=item.Bytes;}
            else if(item.Code==CleanCode.Kept || item.Code==CleanCode.Excluded)Preserved++;
            else Skipped++;
        }
    }
    internal enum CleanCode { Deleted, Excluded, Kept, Busy, Permission, Missing, Changed, Unsafe, RuleChanged, ConfigError, Cancelled, Failed }
    internal sealed class FileOutcome
    {
        public string PathText { get; set; }
        public string Project { get; set; }
        public CleanCode Code { get; set; }
        public string Reason { get; set; }
        public long Bytes { get; set; }
        public string SizeText { get { return Engine.Size(Bytes); } }
        public string Status { get { switch(Code){case CleanCode.Deleted:return "已删除";case CleanCode.Excluded:return "手动排除";case CleanCode.Kept:return "永久保留";case CleanCode.Busy:return "被占用";case CleanCode.Permission:return "权限不足";case CleanCode.Missing:return "已不存在";case CleanCode.Changed:return "文件已变化";case CleanCode.Unsafe:return "安全检查未通过";case CleanCode.RuleChanged:return "规则已变化";case CleanCode.ConfigError:return "保留规则不可读";case CleanCode.Cancelled:return "已取消";default:return "未完成";} } }
    }

    internal static class Engine
    {
        internal static readonly StringComparison CI = StringComparison.OrdinalIgnoreCase;
        public static string Size(long v)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" }; double n = v; int i = 0;
            while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
            return n.ToString(i == 0 ? "0" : "0.##") + " " + units[i];
        }
        public static bool Below(string path, string parent)
        {
            return System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(parent).TrimEnd('\\') + "\\", CI);
        }
        public static bool OnDisk(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string root = System.IO.Path.GetPathRoot(path);
            return String.Equals(root, @"C:\", CI) || String.Equals(root, @"D:\", CI);
        }
        public static bool ReadyFixed(string path)
        {
            if (!OnDisk(path)) return false;
            try
            {
                var disk = new DriveInfo(System.IO.Path.GetPathRoot(path));
                return disk.DriveType == DriveType.Fixed && disk.IsReady;
            }
            catch (Exception ex) { if (!FileProblem(ex)) throw; return false; }
        }
        public static bool FileProblem(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException;
        }
        public static Boundary CheckDirectory(string path)
        {
            if (!OnDisk(path)) return Boundary.Inaccessible;
            string full = System.IO.Path.GetFullPath(path);
            string current = System.IO.Path.GetPathRoot(full);
            try
            {
                if (new DriveInfo(current).DriveType != DriveType.Fixed) return Boundary.Inaccessible;
                // Check from the drive down so a migrated parent junction is never traversed.
                foreach (string segment in full.Substring(current.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = System.IO.Path.Combine(current, segment);
                    FileAttributes attr = File.GetAttributes(current);
                    if ((attr & FileAttributes.ReparsePoint) != 0) return Boundary.Link;
                    if ((attr & FileAttributes.Directory) == 0) return Boundary.Inaccessible;
                }
                return Boundary.Clear;
            }
            catch (FileNotFoundException) { return Boundary.Missing; }
            catch (DirectoryNotFoundException) { return Boundary.Missing; }
            catch (Exception ex) { if (!FileProblem(ex)) throw; return Boundary.Inaccessible; }
        }
        public static HashSet<string> Running()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcesses())
                using (p) { try { result.Add(p.ProcessName); } catch (InvalidOperationException) {} catch (Win32Exception) {} }
            return result;
        }
        public static List<ScanRow> Scan(IEnumerable<Rule> rules, CancellationToken token, KeepSnapshot keep = null)
        {
            var rows = new List<ScanRow>(); var active = Running(); DateTime now = DateTime.UtcNow;
            var pacer=new ScanPacer();
            foreach (Rule rule in rules)
            {
                token.ThrowIfCancellationRequested();
                if (!ReadyFixed(rule.Path) || !rule.HasValidOrigin()) continue;
                Boundary status = CheckDirectory(rule.Path);
                if (status == Boundary.Missing || status == Boundary.Link) continue;
                var row = new ScanRow { Rule = rule, BusyApp = rule.Processes.Any(active.Contains) };
                rows.Add(row);
                if (status != Boundary.Clear) { row.Errors++; continue; }
                var pending = new Stack<string>(); pending.Push(rule.Path);
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    try
                    {
                        if (CheckDirectory(current) != Boundary.Clear) { row.Errors++; continue; }
                        foreach (FileSystemInfo item in new DirectoryInfo(current).EnumerateFileSystemInfos())
                        {
                            pacer.Check(token);
                            try
                            {
                                FileAttributes a = item.Attributes;
                                if ((a & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.ReadOnly)) != 0) continue;
                                if ((a & FileAttributes.Directory) != 0)
                                {
                                    if (rule.AllowsSubdirectory(item.FullName)) pending.Push(item.FullName);
                                    continue;
                                }
                                var file = (FileInfo)item;
                                DateTime modified = file.LastWriteTimeUtc;
                                if (!rule.Accepts(file.FullName, modified, now)) continue;
                                Entry entry = Snapshot(file.FullName);
                                if (entry == null) { row.Errors++; continue; }
                                if (!rule.Accepts(entry.Path, DateTime.FromFileTimeUtc(entry.Modified), now)) continue;
                                entry.Preserved=keep!=null && keep.Contains(entry.Path);
                                row.Files.Add(entry); row.Bytes += entry.Bytes;
                            }
                            catch (Exception ex) { if (!FileProblem(ex)) throw; row.Errors++; }
                        }
                    }
                    catch (Exception ex) { if (!FileProblem(ex)) throw; row.Errors++; }
                }
                row.Pick = !rule.Optional;
            }
            return rows.OrderByDescending(x => x.CanPick && !x.Rule.Optional).ThenByDescending(x => x.CanPick).ThenByDescending(x => x.Bytes).ToList();
        }

        // A file is inspected and deleted using the same exclusive handle. A path-based
        // File.Delete after checking a path would allow a replaced parent junction to win a race.
        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte deleteFile, uint length);

        private static bool FinalPathMatches(SafeFileHandle handle, string path)
        {
            string expected = @"\\?\" + System.IO.Path.GetFullPath(path);
            var final = new StringBuilder(Math.Max(260, expected.Length + 1));
            uint length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
            return length > 0 && length < final.Capacity && String.Equals(final.ToString(), expected, CI);
        }
        public static bool DirectoryResolvesTo(string source, string target)
        {
            // Resolve a single known application directory read-only. File traversal and
            // deletion still start at the physical, non-reparse directory checked below.
            if (!ReadyFixed(source) || CheckDirectory(target) != Boundary.Clear) return false;
            using (var handle = CreateFile(source, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                HandleInfo info;
                return !handle.IsInvalid && GetFileInformationByHandle(handle, out info) &&
                       ((FileAttributes)info.Attributes & FileAttributes.Directory) != 0 && FinalPathMatches(handle, target);
            }
        }
        public static string ResolveDirectory(string source)
        {
            if (!ReadyFixed(source)) return null;
            using (var handle = CreateFile(source, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                HandleInfo info;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info) || ((FileAttributes)info.Attributes & FileAttributes.Directory) == 0) return null;
                var buffer = new StringBuilder(32768);
                uint n = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (n == 0 || n >= buffer.Capacity || !buffer.ToString().StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
                string physical = buffer.ToString().Substring(4);
                return ReadyFixed(physical) && CheckDirectory(physical) == Boundary.Clear ? physical : null;
            }
        }
        private static long FileTime(HandleInfo info)
        { return ((long)(uint)info.Modified.dwHighDateTime << 32) | (uint)info.Modified.dwLowDateTime; }
        private static long Length(HandleInfo info) { return ((long)info.SizeHigh << 32) | info.SizeLow; }
        private static bool Regular(HandleInfo info)
        {
            const FileAttributes reject = FileAttributes.Directory | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.ReadOnly;
            return info.Links == 1 && (info.IndexHigh != 0 || info.IndexLow != 0) && ((FileAttributes)info.Attributes & reject) == 0;
        }
        private static Entry Snapshot(string path)
        {
            using (var h=CreateFile(path,0x80,7,IntPtr.Zero,3,0x00200000,IntPtr.Zero))
            {
                HandleInfo info;
                if (h.IsInvalid || !GetFileInformationByHandle(h,out info) || !Regular(info) || !FinalPathMatches(h, path)) return null;
                return new Entry {Path=path,Bytes=Length(info),Modified=FileTime(info),Volume=info.Volume,
                    IndexHigh=info.IndexHigh,IndexLow=info.IndexLow};
            }
        }
        private static FileOutcome Result(Rule rule,Entry file,CleanCode code,string reason)
        {return new FileOutcome{PathText=file.Path,Project=rule.Name,Bytes=file.Bytes,Code=code,Reason=reason};}
        internal static CleanCode ErrorCode(int code)
        {return code==32 || code==33?CleanCode.Busy:code==5?CleanCode.Permission:code==2 || code==3?CleanCode.Missing:CleanCode.Failed;}
        private static FileOutcome NativeError(Rule rule,Entry file,int code)
        {return Result(rule,file,ErrorCode(code),"Windows 返回 "+code+"："+new Win32Exception(code).Message);}
        private static FileOutcome DeleteScanned(Rule rule, Entry file)
        {
            string path = Path.GetFullPath(file.Path);
            if (!rule.Accepts(path, DateTime.FromFileTimeUtc(file.Modified), DateTime.UtcNow))return Result(rule,file,CleanCode.RuleChanged,"已不符合本次清理规则。");
            var boundary=CheckDirectory(Path.GetDirectoryName(path));
            if(boundary!=Boundary.Clear)return Result(rule,file,boundary==Boundary.Missing?CleanCode.Missing:CleanCode.Unsafe,"父目录不存在、不可访问或已成为链接，未删除。");
            using (SafeFileHandle h = CreateFile(path, 0x10000 | 0x80, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                HandleInfo info;
                if (h.IsInvalid || !GetFileInformationByHandle(h, out info)) return NativeError(rule,file,Marshal.GetLastWin32Error());
                if(!Regular(info))return Result(rule,file,CleanCode.Unsafe,"只读、系统、目录、链接或多硬链接内容不允许清除。");
                if (Length(info)!=file.Bytes || FileTime(info)!=file.Modified || info.Volume!=file.Volume || info.IndexHigh!=file.IndexHigh || info.IndexLow!=file.IndexLow)
                    return Result(rule,file,CleanCode.Changed,"文件身份、大小或修改时间与扫描快照不一致，请重新扫描。");
                if (!FinalPathMatches(h, path)) return Result(rule,file,CleanCode.Unsafe,"实际文件路径与扫描位置不一致。");
                byte disposition = 1;
                return SetFileInformationByHandle(h, 4, ref disposition, 1)?Result(rule,file,CleanCode.Deleted,"文件身份复核通过，已提交删除。"):NativeError(rule,file,Marshal.GetLastWin32Error());
            }
        }
        public static Dictionary<string, Outcome> Clean(IEnumerable<ScanRow> rows, CancellationToken token, KeepSnapshot keep = null)
        {
            var result = new Dictionary<string, Outcome>(StringComparer.OrdinalIgnoreCase);
            foreach (ScanRow row in rows)
            {
                if (!row.Pick || !row.CanPick || row.SelectedCount == 0) continue;
                var done = new Outcome(); result[row.Rule.Path] = done;
                bool safe=row.Rule.HasValidOrigin() && CheckDirectory(row.Rule.Path)==Boundary.Clear;
                bool running=row.Rule.Processes.Length!=0 && row.Rule.Processes.Any(Running().Contains);
                foreach (Entry file in row.Files)
                {
                    try
                    {
                        if(!file.Included)done.Add(Result(row.Rule,file,CleanCode.Excluded,"在本次预览中取消了勾选。"));
                        else if(keep!=null && keep.Error!=null)done.Add(Result(row.Rule,file,CleanCode.ConfigError,keep.Error));
                        else if(file.Preserved || keep!=null && keep.Contains(file.Path))done.Add(Result(row.Rule,file,CleanCode.Kept,"匹配文件或上级目录的永久保留规则。"));
                        else if(token.IsCancellationRequested)done.Add(Result(row.Rule,file,CleanCode.Cancelled,"操作已取消，未尝试删除该文件。"));
                        else if(!safe)done.Add(Result(row.Rule,file,CleanCode.Unsafe,"清理目录或原始映射已变化，未删除。"));
                        else if(running)done.Add(Result(row.Rule,file,CleanCode.Busy,"检测到对应应用正在运行，请正常退出后再扫描。"));
                        else done.Add(DeleteScanned(row.Rule,file));
                    }
                    catch (Exception ex) { if (!FileProblem(ex)) throw;done.Add(Result(row.Rule,file,ex is UnauthorizedAccessException?CleanCode.Permission:CleanCode.Failed,ex.GetBaseException().Message)); }
                }
            }
            return result;
        }
    }
}
