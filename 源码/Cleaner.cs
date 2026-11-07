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
        public string Identity { get { return Path + "|" + Volume + "|" + IndexHigh + "|" + IndexLow + "|" + Bytes + "|" + Modified; } }
    }

    internal sealed class ScanRow : INotifyPropertyChanged
    {
        public Rule Rule;
        public readonly List<Entry> Files = new List<Entry>();
        public int Errors;
        public bool BusyApp;
        public long Bytes { get; set; }
        public long SelectedBytes { get { return Files.Where(f => f.Included).Sum(f => f.Bytes); } }
        public int SelectedCount { get { return Files.Count(f => f.Included); } }
        public string Previous = "";
        public string Name { get { return Rule.Name; } }
        public string PathText { get { return Rule.Path; } }
        public string Drive { get { return PathText.Substring(0, 2).ToUpperInvariant(); } }
        public string SizeText { get { return Engine.Size(Bytes); } }
        public bool CanPick { get { return !BusyApp && Files.Count != 0; } }
        public string Status { get { return BusyApp ? "先退出应用" : Files.Count == 0 ? (Errors > 0 ? "访问受限" : "无符合文件") : Rule.Optional ? "手动勾选" : "可一键清理"; } }
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
        public int Removed, Skipped;
        public string Text { get { return "已删除 " + Removed.ToString("N0") + " 个文件，共 " + Engine.Size(Bytes) + "，跳过 " + Skipped.ToString("N0") + " 个。"; } }
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
        public static List<ScanRow> Scan(IEnumerable<Rule> rules, CancellationToken token)
        {
            var rows = new List<ScanRow>(); var active = Running(); DateTime now = DateTime.UtcNow;
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
                            token.ThrowIfCancellationRequested();
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
        private static bool DeleteScanned(Rule rule, Entry file)
        {
            string path = Path.GetFullPath(file.Path);
            if (!rule.Accepts(path, DateTime.FromFileTimeUtc(file.Modified), DateTime.UtcNow) ||
                CheckDirectory(Path.GetDirectoryName(path)) != Boundary.Clear) return false;
            using (SafeFileHandle h = CreateFile(path, 0x10000 | 0x80, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                HandleInfo info;
                if (h.IsInvalid || !GetFileInformationByHandle(h, out info)) return false;
                if (!Regular(info) || Length(info)!=file.Bytes || FileTime(info)!=file.Modified ||
                    info.Volume!=file.Volume || info.IndexHigh!=file.IndexHigh || info.IndexLow!=file.IndexLow) return false;
                if (!FinalPathMatches(h, path)) return false;
                byte disposition = 1;
                return SetFileInformationByHandle(h, 4, ref disposition, 1);
            }
        }
        public static Dictionary<string, Outcome> Clean(IEnumerable<ScanRow> rows, CancellationToken token)
        {
            var result = new Dictionary<string, Outcome>(StringComparer.OrdinalIgnoreCase);
            foreach (ScanRow row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (!row.Pick || !row.CanPick || row.SelectedCount == 0) continue;
                var done = new Outcome(); result[row.Rule.Path] = done;
                if (!row.Rule.HasValidOrigin() || CheckDirectory(row.Rule.Path) != Boundary.Clear ||
                    (row.Rule.Processes.Length != 0 && row.Rule.Processes.Any(Running().Contains)))
                { done.Skipped = row.SelectedCount; continue; }
                foreach (Entry file in row.Files.Where(f => f.Included))
                {
                    token.ThrowIfCancellationRequested();
                    try { if (DeleteScanned(row.Rule, file)) { done.Removed++; done.Bytes += file.Bytes; } else done.Skipped++; }
                    catch (Exception ex) { if (!FileProblem(ex)) throw; done.Skipped++; }
                }
            }
            return result;
        }
    }
}
