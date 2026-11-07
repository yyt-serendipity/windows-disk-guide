using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DiskGuide
{
    internal sealed class SoftwareEntry : IconEntry
    {
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string InstallPath { get; set; }
        public string Kind { get; set; }
        public string Id { get; set; }
        public string UninstallCommand { get; set; }
        public string SizeText { get; set; }
        public string ProtectionReason { get; set; }
        public bool CanUninstall { get { return String.IsNullOrEmpty(ProtectionReason) && !String.IsNullOrEmpty(UninstallCommand); } }
        public string Status { get { return CanUninstall ? "可调用官方卸载" : "保留 / 系统管理"; } }
        internal RegistryHive Hive;
        internal RegistryView View;
        internal string KeyName, PackageFullName, PackageFamilyName;
        internal bool Msi;
    }

    internal sealed class SoftwareInventoryResult
    {
        public readonly List<SoftwareEntry> Items = new List<SoftwareEntry>();
        public readonly List<string> Warnings = new List<string>();
    }

    internal sealed class ResidualPlan
    {
        public string Path { get; internal set; }
        public string Reason { get; internal set; }
        public bool CanRecycle { get; internal set; }
        public int FileCount { get; internal set; }
        public long Bytes { get; internal set; }
        public List<string> Files { get; private set; }
        internal SoftwareEntry Owner;
        internal readonly List<string> Manifest = new List<string>();
        internal readonly Dictionary<string, string> Identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Directories = new List<string>();
        public ResidualPlan() { Files = new List<string>(); }
    }

    // Read only inventory. Uninstall and recycle methods are called only after a separate UI confirmation.
    internal static class SoftwareInventory
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private static readonly StringComparison CI = StringComparison.OrdinalIgnoreCase;
        private static readonly HashSet<string> ProgramExtensions = new HashSet<string>(new[] {
            ".exe", ".dll", ".pdb", ".mui", ".pak", ".asar", ".node", ".ocx", ".tlb", ".manifest", ".msi", ".cab"
        }, StringComparer.OrdinalIgnoreCase);

        public static SoftwareInventoryResult Read()
        {
            var result = new SoftwareInventoryResult();
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in Views())
                    ReadRegistry(hive, view, result);
            try { result.Items.AddRange(ReadPackages()); }
            catch (Exception ex) { result.Warnings.Add("商店应用未完整读取：" + ex.GetBaseException().Message); }
            result.Items.Sort((a, b) => {
                int available = b.CanUninstall.CompareTo(a.CanUninstall);
                return available != 0 ? available : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            });
            return result;
        }

        private static IEnumerable<RegistryView> Views()
        {
            if (Environment.Is64BitOperatingSystem) yield return RegistryView.Registry64;
            yield return RegistryView.Registry32;
        }

        private static void ReadRegistry(RegistryHive hive, RegistryView view, SoftwareInventoryResult result)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey parent = root.OpenSubKey(UninstallKey))
                {
                    if (parent == null) return;
                    foreach (string name in parent.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey key = parent.OpenSubKey(name))
                            {
                                SoftwareEntry entry = FromKey(key, hive, view, name);
                                if (entry != null && !(hive == RegistryHive.CurrentUser && result.Items.Any(e =>
                                    e.Hive == hive && e.KeyName == name && e.Name == entry.Name && e.Version == entry.Version &&
                                    e.InstallPath == entry.InstallPath && e.UninstallCommand == entry.UninstallCommand))) result.Items.Add(entry);
                            }
                        }
                        catch (Exception ex) { result.Warnings.Add(hive + " / " + view + " / " + name + "：" + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { result.Warnings.Add(hive + " / " + view + " 软件列表：" + ex.Message); }
        }

        private static SoftwareEntry FromKey(RegistryKey key, RegistryHive hive, RegistryView view, string name)
        {
            if (key == null) return null;
            string display = Text(key, "DisplayName");
            if (String.IsNullOrWhiteSpace(display)) return null;
            long kb;
            Int64.TryParse(Convert.ToString(key.GetValue("EstimatedSize", 0)), out kb);
            var item = new SoftwareEntry {
                Name = display, Publisher = Text(key, "Publisher"), Version = Text(key, "DisplayVersion"),
                InstallPath = NormalizePath(Text(key, "InstallLocation")), Kind = "桌面软件",
                Id = hive + "|" + view + "|" + name, Hive = hive, View = view, KeyName = name,
                Msi = Convert.ToString(key.GetValue("WindowsInstaller", 0)) == "1",
                UninstallCommand = Text(key, "UninstallString"), IconLocation = Text(key, "DisplayIcon"),
                SizeText = kb > 0 && kb < Int64.MaxValue / 1024 ? Engine.Size(kb * 1024) : "未登记"
            };
            Guid product;
            if (item.Msi && Guid.TryParse(name, out product)) item.UninstallCommand = "msiexec.exe /x " + product.ToString("B") + " /norestart";
            if (Convert.ToString(key.GetValue("SystemComponent", 0)) == "1" ||
                !String.IsNullOrEmpty(Text(key, "ParentKeyName")) ||
                Regex.IsMatch(Text(key, "ReleaseType"), "Update|Hotfix", RegexOptions.IgnoreCase))
                item.ProtectionReason = "系统或其他软件的组成部分，请通过 Windows 设置或所属软件管理。";
            else if (Inside(item.InstallPath, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
                item.ProtectionReason = "安装在 Windows 系统目录，请通过系统设置管理。";
            else if (String.IsNullOrWhiteSpace(item.UninstallCommand))
                item.ProtectionReason = "未登记官方卸载命令。";
            else
            {
                string executable, arguments, reason;
                if (!TryParseUninstall(item.UninstallCommand, out executable, out arguments, out reason))
                    item.ProtectionReason = reason + " 请通过 Windows 设置管理。";
            }
            return item;
        }

        private static string Text(RegistryKey key, string name)
        { return Convert.ToString(key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)).Trim(); }

        private const string PackageReadScript = @"
$ErrorActionPreference='Stop'
$enc=[Text.Encoding]::UTF8
$names=@{}
try {
 Get-StartApps | ForEach-Object {
  if($_.AppID.Contains('!')) {
   $family=$_.AppID.Split('!')[0]
   if(-not $names.ContainsKey($family)) {$names[$family]=$_.Name}
  }
 }
} catch { $names=@{} }
Get-AppxPackage | ForEach-Object {
 $p=$_
 $display=$names[$p.PackageFamilyName]
 if([string]::IsNullOrWhiteSpace($display)) {$display=$p.Name}
 $fields=@($display,$p.Publisher,$p.Version.ToString(),$p.InstallLocation,$p.PackageFullName,[string]$p.IsFramework,[string]$p.NonRemovable,[string]$p.IsResourcePackage,$p.PackageFamilyName)
 (($fields | ForEach-Object {[Convert]::ToBase64String($enc.GetBytes([string]$_))}) -join ""`t"")
}";

        private static List<SoftwareEntry> ReadPackages()
        {
            string output = RunPowerShell(PackageReadScript, null, 45000);
            var result = new List<SoftwareEntry>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] values = line.Split('\t');
                if (values.Length != 9) throw new InvalidDataException("商店应用输出格式不完整。");
                for (int i = 0; i < values.Length; i++) values[i] = Encoding.UTF8.GetString(Convert.FromBase64String(values[i]));
                if (String.IsNullOrEmpty(values[4])) throw new InvalidDataException("商店应用缺少包标识。");
                result.Add(new SoftwareEntry {
                    Name = values[0], Publisher = values[1], Version = values[2], InstallPath = NormalizePath(values[3]),
                    Id = "Appx|" + values[4], PackageFullName = values[4], PackageFamilyName = values[8], Kind = "商店应用", SizeText = "未登记",
                    UninstallCommand = "Remove-AppxPackage（当前用户）",
                    ProtectionReason = values.Skip(5).Take(3).Any(v => String.Equals(v, "True", CI)) ? "系统、框架或资源包，不在本工具中卸载。" : null
                });
            }
            return result;
        }

        internal static ProcessStartInfo PowerShellInfo(string script)
        {
            string executable = System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            script = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + script;
            return new ProcessStartInfo(executable, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
        }

        private static string RunPowerShell(string script, string package, int timeout)
        {
            ProcessStartInfo info = PowerShellInfo("[Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + script);
            if (package != null) info.EnvironmentVariables["DISKGUIDE_PACKAGE_ID"] = package;
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("读取商店应用超时，可稍后刷新。");
                }
                Task.WaitAll(output, error);
                if (process.ExitCode != 0 || !String.IsNullOrWhiteSpace(error.Result))
                    throw new InvalidOperationException(String.IsNullOrWhiteSpace(error.Result) ? "PowerShell 返回 " + process.ExitCode : error.Result.Trim());
                return output.Result;
            }
        }

        public static SoftwareEntry Revalidate(SoftwareEntry previous)
        {
            if (previous == null) throw new ArgumentNullException("previous");
            SoftwareEntry current;
            if (previous.PackageFullName != null)
                current = ReadPackages().FirstOrDefault(p => p.Id == previous.Id);
            else
                using (RegistryKey root = RegistryKey.OpenBaseKey(previous.Hive, previous.View))
                using (RegistryKey key = root.OpenSubKey(UninstallKey + "\\" + previous.KeyName))
                    current = FromKey(key, previous.Hive, previous.View, previous.KeyName);
            if (current == null || !SameRegistration(previous, current))
                throw new InvalidOperationException("软件登记已经变化或消失，请刷新软件列表后再操作。");
            if (!current.CanUninstall) throw new InvalidOperationException(current.ProtectionReason ?? "没有可用的卸载命令。");
            return current;
        }

        internal static bool SameRegistration(SoftwareEntry a, SoftwareEntry b)
        {
            return a.Id == b.Id && a.Name == b.Name && a.Version == b.Version && a.Publisher == b.Publisher &&
                String.Equals(a.InstallPath, b.InstallPath, CI) && a.UninstallCommand == b.UninstallCommand;
        }

        public static Process StartUninstall(SoftwareEntry entry)
        {
            SoftwareEntry item = Revalidate(entry);
            if (item.PackageFullName != null)
            {
                const string script = @"
$ErrorActionPreference='Stop'
$p=@(Get-AppxPackage | Where-Object {$_.PackageFullName -ceq $env:DISKGUIDE_PACKAGE_ID})
if($p.Count -ne 1 -or $p[0].IsFramework -or $p[0].NonRemovable -or $p[0].IsResourcePackage){throw 'Package changed or protected.'}
Remove-AppxPackage -Package $p[0].PackageFullName -ErrorAction Stop
";
                ProcessStartInfo info = PowerShellInfo(script);
                info.EnvironmentVariables["DISKGUIDE_PACKAGE_ID"] = item.PackageFullName;
                // The caller reads both streams and checks ExitCode before claiming completion.
                return Process.Start(info);
            }
            Guid product;
            if (item.Msi && Guid.TryParse(item.KeyName, out product))
                return Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/x " + product.ToString("B") + " /norestart") { UseShellExecute = true });
            string executable, arguments, reason;
            if (!TryParseUninstall(item.UninstallCommand, out executable, out arguments, out reason))
                throw new InvalidOperationException(reason + " 请在 Windows 设置中卸载该软件。");
            if (!File.Exists(executable)) throw new FileNotFoundException("官方卸载程序不存在，请使用软件安装器修复。", executable);
            return Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(executable) });
        }

        internal static bool TryParseUninstall(string command, out string executable, out string arguments, out string reason)
        {
            executable = arguments = "";
            reason = "卸载命令无法安全识别。";
            if (String.IsNullOrWhiteSpace(command) || command.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return false;
            string text = Environment.ExpandEnvironmentVariables(command.Trim());
            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                if (end <= 1 || (end + 1 < text.Length && !Char.IsWhiteSpace(text[end + 1]))) return false;
                executable = text.Substring(1, end - 1); arguments = text.Substring(end + 1).Trim();
            }
            else
            {
                Match match = Regex.Match(text, @"^(.+?\.exe)(?:\s+(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) return false;
                executable = match.Groups[1].Value; arguments = match.Groups[2].Value;
            }
            if (String.Equals(executable, "msiexec.exe", CI) || String.Equals(executable, "msiexec", CI))
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            if (!IsAbsoluteLocal(executable) || !String.Equals(System.IO.Path.GetExtension(executable), ".exe", CI)) return false;
            executable = NormalizePath(executable);
            string file = System.IO.Path.GetFileName(executable);
            if (new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "rundll32.exe", "regsvr32.exe", "mshta.exe" }.Contains(file, StringComparer.OrdinalIgnoreCase))
            { reason = "该软件使用脚本或通用系统宿主卸载，需要通过 Windows 设置处理。"; return false; }
            if (String.Equals(file, "msiexec.exe", CI))
            {
                Match m = Regex.Match(arguments, @"^/[ix]\s*(\{[0-9a-f-]{36}\})(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Guid id;
                if (!m.Success || !Guid.TryParse(m.Groups[1].Value, out id)) return false;
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                arguments = "/x " + id.ToString("B") + " /norestart";
            }
            reason = null; return true;
        }

        internal static bool IsAbsoluteLocal(string path)
        { return !String.IsNullOrWhiteSpace(path) && Regex.IsMatch(path, @"^[a-zA-Z]:[\\/]") && path.IndexOf(':', 2) < 0; }

        internal static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                return IsAbsoluteLocal(path) ? System.IO.Path.GetFullPath(path).TrimEnd('\\') : "";
            }
            catch (Exception ex) { if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return ""; throw; }
        }

        private static bool Inside(string path, string parent)
        { return !String.IsNullOrEmpty(path) && !String.IsNullOrEmpty(parent) && (String.Equals(path.TrimEnd('\\'), parent.TrimEnd('\\'), CI) || path.StartsWith(parent.TrimEnd('\\') + "\\", CI)); }

        internal static string ResidualPathProblem(string path)
        {
            if (!IsAbsoluteLocal(path)) return "没有登记可确认的本地安装目录。";
            string normal = NormalizePath(path);
            if (normal.Length < 4 || normal.Substring(3).Split('\\').Length < 2) return "磁盘根目录或根下一级目录可能包含共享内容，不自动清除。";
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localPrograms = System.IO.Path.Combine(local, "Programs");
            foreach (string protectedPath in new[] { windows, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) })
                if (Inside(normal, protectedPath) || Inside(protectedPath, normal)) return "该位置包含系统、共享或个人文件，保留供人工检查。";
            if (Inside(normal, profile) && (!Inside(normal, localPrograms) || String.Equals(normal, localPrograms, CI)))
                return "个人账户的数据目录不作为安装残留整目录清除。";
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), localPrograms })
                if (Inside(root, normal)) return "该目录是共享安装根目录，不能整目录清除。";
            string[] components = normal.Substring(3).Split('\\');
            if (components.Any(c => new[] { "WindowsApps", "Common Files", "Windows", "ProgramData", "Users", "Recovery", "$Recycle.Bin", "System Volume Information" }.Contains(c, StringComparer.OrdinalIgnoreCase)))
                return "系统或共享目录不自动清除。";
            try
            {
                for (DirectoryInfo dir = new DirectoryInfo(normal); dir != null; dir = dir.Parent)
                    if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return "路径包含链接或重定向，保留供人工检查。";
            }
            catch (Exception ex) { return "无法核对目录边界：" + ex.Message; }
            return null;
        }

        public static ResidualPlan PreviewResidual(SoftwareEntry owner, SoftwareInventoryResult current)
        {
            if (owner == null || current == null) throw new ArgumentNullException();
            var plan = new ResidualPlan { Owner = owner, Path = owner.InstallPath };
            if (current.Warnings.Count > 0) { plan.Reason = "软件列表未完整读取，暂不能确认目录是否独占。"; return plan; }
            if (current.Items.Any(s => s.Id == owner.Id)) { plan.Reason = "软件仍登记为已安装，请完成官方卸载并刷新后检查。"; return plan; }
            if (owner.PackageFullName != null) { plan.Reason = "商店应用的数据和共享包由 Windows 管理，不扫描 WindowsApps 残留。"; return plan; }
            plan.Reason = ResidualPathProblem(owner.InstallPath);
            if (plan.Reason != null) return plan;
            if (current.Items.Any(s => !String.IsNullOrEmpty(s.InstallPath) && (Inside(s.InstallPath, owner.InstallPath) || Inside(owner.InstallPath, s.InstallPath))))
            { plan.Reason = "与其他已安装软件的目录重叠，不能整目录清除。"; return plan; }
            if (!Directory.Exists(owner.InstallPath)) { plan.Reason = "登记的安装目录已不存在，没有这一位置的残留。"; return plan; }
            try
            {
                var pending = new Stack<string>(); pending.Push(owner.InstallPath);
                int visited = 0; bool unclassified = false;
                while (pending.Count > 0)
                {
                    string path = pending.Pop();
                    if (++visited > 100000) { plan.Reason = "文件数量较多，预览未完成；保留供人工检查。"; return plan; }
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0)
                    { plan.Reason = "存在链接或系统标记内容，保留供人工检查。"; return plan; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        string identity = ReadIdentity(path, true);
                        plan.Identities.Add(path, identity);
                        plan.Directories.Add(path);
                        plan.Manifest.Add(identity);
                        foreach (string child in Directory.GetFileSystemEntries(path)) pending.Push(child);
                    }
                    else
                    {
                        var file = new FileInfo(path);
                        plan.Files.Add(path); plan.FileCount++; plan.Bytes += file.Length;
                        string identity = ReadIdentity(path, false);
                        plan.Identities.Add(path, identity);
                        plan.Manifest.Add(identity);
                        if (!ProgramExtensions.Contains(file.Extension)) unclassified = true;
                    }
                }
                plan.Manifest.Sort(StringComparer.Ordinal);
                plan.CanRecycle = !unclassified;
                plan.Reason = unclassified ? "存在配置、文档或无法分类的文件；为保护个人数据，不整目录清除。可打开目录逐项检查。" :
                    "仅发现程序二进制文件或空目录；未发现其他已安装软件共享此路径。确认后交给 Windows 回收站处理。";
            }
            catch (Exception ex) { plan.CanRecycle = false; plan.Reason = "预览未完成，不执行清除：" + ex.Message; }
            return plan;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte value, uint length);

        private static string HandleIdentity(SafeFileHandle handle, string path, bool directory)
        {
            NativeInfo info;
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw new IOException("无法读取文件身份：" + path);
            FileAttributes attributes = (FileAttributes)info.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory ||
                (info.IndexHigh == 0 && info.IndexLow == 0) || (!directory && info.Links != 1))
                throw new IOException("文件身份、链接或系统属性不符合条件：" + path);
            string expected = @"\\?\" + System.IO.Path.GetFullPath(path);
            var final = new StringBuilder(Math.Max(260, expected.Length + 1));
            uint length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity || !String.Equals(final.ToString(), expected, CI))
                throw new IOException("目录实际位置发生变化：" + path);
            string id = (directory ? "D|" : "F|") + path + "|" + info.Volume + "|" + info.IndexHigh + "|" + info.IndexLow;
            return directory ? id : id + "|" + info.SizeHigh + "|" + info.SizeLow + "|" + (uint)info.Modified.dwHighDateTime + "|" + (uint)info.Modified.dwLowDateTime;
        }

        internal static string ReadIdentity(string path, bool directory)
        {
            if (Engine.CheckDirectory(directory ? path : System.IO.Path.GetDirectoryName(path)) != Boundary.Clear)
                throw new IOException("目录包含链接或无法访问：" + path);
            using (SafeFileHandle handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000 | (directory ? 0x02000000u : 0u), IntPtr.Zero))
                return HandleIdentity(handle, path, directory);
        }

        // The order is the native IFileOperation vtable, including methods unused here.
        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie);
            void SetOperationFlags(uint flags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr owner);
            void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems(IntPtr items);
            void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void MoveItems(IntPtr items, IShellItem destination);
            void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void CopyItems(IntPtr items, IShellItem destination);
            void DeleteItem(IShellItem item, IntPtr sink); void DeleteItems(IntPtr items);
            void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
            void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent); void GetDisplayName(uint kind, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem other, uint hint, out int order);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

        internal static bool MatchesApproved(ResidualPlan approved, ResidualPlan fresh)
        { return approved != null && fresh != null && approved.CanRecycle && fresh.CanRecycle && approved.Manifest.SequenceEqual(fresh.Manifest); }

        public static void RecycleResidual(ResidualPlan approvedPlan, IntPtr ownerWindow)
        {
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                throw new InvalidOperationException("回收操作必须在 STA 工作线程中进行。");
            if (approvedPlan == null || !approvedPlan.CanRecycle) throw new InvalidOperationException("该预览不允许清除。");
            ResidualPlan fresh = PreviewResidual(approvedPlan.Owner, Read());
            if (!MatchesApproved(approvedPlan, fresh)) throw new InvalidOperationException("软件状态或文件已经变化，请重新检查残留。");
            var guards = new List<SafeFileHandle>();
            IFileOperation operation = null;
            try
            {
                // Deny rename/delete of all directory ancestors while the exact file list is recycled.
                var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string directory in fresh.Directories)
                    for (DirectoryInfo d = new DirectoryInfo(directory); d != null; d = d.Parent) ancestors.Add(d.FullName);
                foreach (string directory in ancestors.OrderBy(p => p.Length))
                {
                    var guard = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    guards.Add(guard);
                    if (guard.IsInvalid) throw new IOException("无法固定安装目录边界，请退出相关软件后重试。");
                    // Filesystem roots can have System attributes; use the existing boundary checker
                    // for shared ancestors, and match full identities for reviewed directories.
                    if (Engine.CheckDirectory(directory) != Boundary.Clear) throw new IOException("目录边界已改变。");
                    string expected;
                    if (fresh.Identities.TryGetValue(directory, out expected) && HandleIdentity(guard, directory, true) != expected)
                        throw new IOException("安装目录已被替换，请重新检查。");
                }
                if (fresh.Files.Count > 0)
                {
                    operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
                    // Recycle only; any error aborts. No permanent-delete fallback or connected items.
                    operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x0400 | 0x2000 | 0x20000000);
                    operation.SetOwnerWindow(ownerWindow);
                    Guid shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    foreach (string file in fresh.Files)
                    {
                        if (ReadIdentity(file, false) != fresh.Identities[file]) throw new IOException("文件已发生变化，请重新检查：" + file);
                        IShellItem item;
                        SHCreateItemFromParsingName(file, IntPtr.Zero, ref shellItemId, out item);
                        try { operation.DeleteItem(item, IntPtr.Zero); }
                        finally { Marshal.FinalReleaseComObject(item); }
                    }
                    operation.PerformOperations();
                    bool aborted; operation.GetAnyOperationsAborted(out aborted);
                    if (aborted) throw new IOException("Windows 未完成回收，可能因取消、文件占用或权限不足。未执行永久删除后备操作。");
                }
            }
            finally
            {
                if (operation != null) Marshal.FinalReleaseComObject(operation);
                foreach (SafeFileHandle guard in guards) guard.Dispose();
            }
            // Remove only reviewed empty directories, using their verified native identity.
            // SetFileInformationByHandle refuses a nonempty directory; newly added files remain.
            foreach (string directory in fresh.Directories.OrderByDescending(p => p.Length))
            {
                try
                {
                    if (Engine.CheckDirectory(directory) != Boundary.Clear) continue;
                    using (SafeFileHandle handle = CreateFile(directory, 0x10080, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
                    {
                        if (handle.IsInvalid || HandleIdentity(handle, directory, true) != fresh.Identities[directory]) continue;
                        byte remove = 1; SetFileInformationByHandle(handle, 4, ref remove, 1);
                    }
                }
                catch (IOException) { } // A changed, occupied or nonempty directory is retained.
            }
        }
    }
}
