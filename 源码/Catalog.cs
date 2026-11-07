using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace DiskGuide
{
    internal sealed class Folder : INotifyPropertyChanged
    {
        public string PathText { get; set; }
        public string Name { get; set; }
        public string Drive { get { return PathText.Substring(0, 2).ToUpperInvariant(); } }
        public string Policy { get; set; }
        public string Advice { get { switch(Policy) {
            case "可清部分": return "只清缓存";
            case "可一键清理": return "可清缓存";
            case "手动勾选": return "先看影响";
            case "需要判断": return "先确认用途";
            case "系统保护": return "不要删除";
            case "保留": return "建议保留";
            default:return Policy;
        } } }
        public string Purpose { get; set; }
        private UsagePart measured;
        public UsagePart Measured
        {
            get { return measured; }
            set
            {
                measured = value;
                var notify = PropertyChanged;
                if (notify == null) return;
                notify(this, new PropertyChangedEventArgs("SizeText"));
                notify(this, new PropertyChangedEventArgs("SizeBytes"));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public long SizeBytes { get { return measured == null || !measured.Readable ? -1 : measured.Bytes; } }
        public string SizeText { get { return IsLink ? "已跳过" : measured == null ? "待统计" : measured.SizeText; } }
        public List<ScanRow> Related = new List<ScanRow>();
        public long ReadyBytes { get { return Related.Where(r => r.CanPick && !r.Rule.Optional).Sum(r => r.EligibleBytes); } }
        public string ReadyText { get { return ReadyBytes > 0 ? Engine.Size(ReadyBytes) : "—"; } }
        public static bool ContainsRule(string folder, string rulePath)
        { return String.Equals(folder, rulePath, Engine.CI) || Engine.Below(rulePath, folder); }
        public string Tint { get { return Policy == "可清部分" || Policy == "可一键清理" ? "#DBF3ED" :
                    Policy == "手动勾选" || Policy == "需要判断" ? "#FFF0D8" : "#E9EFF6"; } }
        public bool IsLink { get; set; }
        public string Ink { get { return Policy == "可清部分" || Policy == "可一键清理" ? "#006C61" :
                    Policy == "需要判断" || Policy == "手动勾选" ? "#92520D" : "#485B76"; } }
    }

    internal static class Catalog
    {
        public static List<Rule> Rules()
        {
            var rules = new List<Rule>();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Add(rules, Path.Combine(local, "Temp"), "用户临时文件", FileKind.OldTemp, 7,
                "仅清此目录第一层满 7 天的 .tmp 和 .temp 文件。安装包、虚拟磁盘和子目录会保留。");
            Add(rules, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), "Windows 临时文件", FileKind.OldTemp, 7,
                "仅清第一层满 7 天的临时文件，使用当前 Windows 账户权限。");
            Add(rules, Path.Combine(local, "CrashDumps"), "软件闪退记录", FileKind.Dump, 7,
                "满 7 天的 .dmp 故障记录。若仍在排查软件闪退，请保留。");
            Add(rules, Path.Combine(local, @"NVIDIA\DXCache"), "NVIDIA 游戏缓存", FileKind.Shader, 0,
                "清理后游戏会重新编译着色器，首次进入时可能短暂卡顿。请退出游戏后再勾选。");
            Browser(rules, Path.Combine(local, @"Google\Chrome\User Data"), "Chrome", "chrome");
            Browser(rules, Path.Combine(local, @"Microsoft\Edge\User Data"), "Edge", "msedge");
            Browser(rules, Path.Combine(local, @"Doubao\User Data"), "豆包", "Doubao");
            var roamingRoots = new List<string>();
            if (Engine.ReadyFixed(roaming))
            {
                roamingRoots.Add(roaming);
                string profileName = Path.GetFileName(profile.TrimEnd('\\'));
                if (!String.IsNullOrWhiteSpace(profileName) && Engine.ReadyFixed(@"D:\"))
                    roamingRoots.Add(Path.Combine(@"D:\AppData", profileName, "Roaming"));
            }
            foreach (string root in roamingRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int first = rules.Count;
                Web(rules, Path.Combine(root, @"douyin\Cache\Cache_Data"), "抖音页面缓存", "douyin");
                Web(rules, Path.Combine(root, @"douyin\TTNetCache\disk_cache"), "抖音播放缓存", "douyin");
                Web(rules, Path.Combine(root, @"baidunetdisk\Cache\Cache_Data"), "百度网盘页面缓存", "BaiduNetdisk");
                Add(rules, Path.Combine(root, @"io.github.clash-verge-rev.clash-verge-rev\logs"), "Clash Verge 历史日志", FileKind.OldLog, 7,
                    "仅清日志目录及 service 子目录中满 7 天的 .log。订阅、节点、配置和近期日志均保留。");
                if (!String.Equals(root, roaming, Engine.CI))
                {
                    foreach (Rule r in rules.Skip(first))
                    {
                        string app = r.Path.Substring(root.Length + 1).Split('\\')[0];
                        r.OriginalAppPath = Path.Combine(roaming, app);
                        r.PhysicalAppPath = Path.Combine(root, app);
                    }
                }
            }
            Add(rules,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"NVIDIA Corporation\NVIDIA App\UpdateFramework\ota-artifacts"),"NVIDIA 旧更新下载包",FileKind.DriverDownload,7,"只识别更新下载目录中满 7 天、名称符合 NVIDIA 安装包格式的文件。驱动本体及游戏缓存不在此规则内。","NVIDIA App","setup");
            Add(rules,Path.Combine(local,@"Ollama\updates_v2"),"Ollama 旧更新安装包",FileKind.OllamaDownload,7,"只识别哈希目录中满 7 天的 OllamaSetup.exe，保留模型与配置。请先退出 Ollama。","ollama","OllamaSetup");
            Add(rules,Path.Combine(local,@"Yarn\Berry\cache"),"Yarn 依赖缓存",FileKind.YarnArchive,7,"满 7 天的缓存 ZIP，重新构建可能需要联网下载。","node","yarn");
            Add(rules,Path.Combine(local,@"npm-cache\_cacache\content-v2\sha512"),"npm 下载内容缓存",FileKind.NpmContent,7,"只识别 SHA-512 内容缓存块，不包含项目的 node_modules、源码或凭据。","node","npm");
            Add(rules,Path.Combine(local,"electron"),"Electron 下载缓存",FileKind.ElectronArchive,7,"满 7 天、名称符合 Windows Electron 发行包格式的 ZIP。","node","electron");
            Add(rules,Path.Combine(local,"go-build"),"Go 编译缓存",FileKind.GoBuild,7,"只清满 7 天、名称符合 Go 构建缓存格式的文件，保留 Go 源码。","go","gopls");
            return rules.Where(r => Engine.ReadyFixed(r.Path) && r.HasValidOrigin())
                .Where(r => { var state = Engine.CheckDirectory(r.Path); return state == Boundary.Clear || state == Boundary.Inaccessible; })
                .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }
        private static void Add(List<Rule> rules, string path, string name, FileKind kind, int days, string why, params string[] processes)
        {
            if (!Path.IsPathRooted(path) || !Engine.OnDisk(path)) return;
            rules.Add(new Rule { Path = Path.GetFullPath(path), Name = name, Kind = kind, Days = days, Why = why, Processes = processes });
        }
        private static void Web(List<Rule> rules, string path, string name, string process)
        {
            Add(rules, path, name, FileKind.Web, 0, "识别应用生成的缓存块和索引。退出应用后可清，之后访问内容时会重新下载。登录、聊天、下载任务均保留。", process);
        }
        private static void Browser(List<Rule> rules, string root, string product, string process)
        {
            if (Engine.CheckDirectory(root) != Boundary.Clear) return;
            try
            {
                foreach (var profile in new DirectoryInfo(root).EnumerateDirectories())
                {
                    if ((profile.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        !(profile.Name == "Default" || Regex.IsMatch(profile.Name, @"^Profile [0-9]+$"))) continue;
                    string label = product + (profile.Name == "Default" ? "" : " · " + profile.Name);
                    Web(rules, Path.Combine(profile.FullName, @"Cache\Cache_Data"), label + " 网页缓存", process);
                    Web(rules, Path.Combine(profile.FullName, @"Code Cache\js"), label + " 脚本缓存", process);
                }
            }
            catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; }
        }

        private static readonly Dictionary<string, string> PersonalFolders = KnownPersonalFolders();

        private static Dictionary<string, string> KnownPersonalFolders()
        {
            var folders = new Dictionary<Environment.SpecialFolder, string>
            {
                { Environment.SpecialFolder.DesktopDirectory, "Windows 当前账户的桌面，包含个人文件、项目和快捷方式。" },
                { Environment.SpecialFolder.MyDocuments, "Windows 当前账户的文档目录，可能包含项目原稿和应用保存的数据。" },
                { Environment.SpecialFolder.MyPictures, "Windows 当前账户的图片目录，可能含照片和作品原图。" },
                { Environment.SpecialFolder.MyMusic, "Windows 当前账户的音乐目录。" },
                { Environment.SpecialFolder.MyVideos, "Windows 当前账户的视频目录。" }
            };
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in folders)
            {
                string path = Environment.GetFolderPath(item.Key);
                if (Engine.OnDisk(path)) paths[path.TrimEnd('\\')] = item.Value;
            }
            return paths;
        }
        private static Folder Set(Folder f, string policy, string purpose)
        { f.Policy = policy; f.Purpose = purpose; return f; }

        private static readonly Lazy<Dictionary<string, string>> Installed = new Lazy<Dictionary<string, string>>(ReadInstalled);
        private static Dictionary<string, string> ReadInstalled()
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] keys = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (string key in keys)
                {
                    try
                    {
                        using (var list = hive.OpenSubKey(key))
                        {
                            if (list == null) continue;
                            foreach (string id in list.GetSubKeyNames())
                            {
                                try
                                {
                                    using (var entry = list.OpenSubKey(id))
                                    {
                                        if (entry == null) continue;
                                        string name = entry.GetValue("DisplayName") as string;
                                        string path = entry.GetValue("InstallLocation") as string;
                                        if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(path)) continue;
                                        path = path.Trim().Trim('"').Replace('/','\\').TrimEnd('\\');
                                        if (!Regex.IsMatch(path,@"^[CD]:\\",RegexOptions.IgnoreCase)) continue;
                                        path = Path.GetFullPath(path);
                                        found[path] = name;
                                    }
                                }
                                catch(Exception ex) { if (!(ex is ArgumentException) && !Engine.FileProblem(ex)) throw; }
                            }
                        }
                    }
                    catch(Exception ex) { if (!Engine.FileProblem(ex)) throw; }
                }
            return found;
        }
        private static Folder Unknown(Folder f)
        {
            try
            {
                var entries=new DirectoryInfo(f.PathText).EnumerateFileSystemInfos().Take(40).ToList();
                if (entries.Count==0) return Set(f,"需要判断","该目录当前为空。确认应用无需保留它后可手动移除，能释放的空间很少。");
                var ordinary = entries.Where(e => (e.Attributes & FileAttributes.ReparsePoint) == 0).ToList();
                var files = ordinary.OfType<FileInfo>().ToList();
                var children = ordinary.OfType<DirectoryInfo>().Take(8).ToList();
                var ext = files.Select(e => e.Extension.ToLowerInvariant()).ToList();
                if (files.Any(e => String.Equals(e.Name,"my.ini",Engine.CI) || String.Equals(e.Name,"mysql.ini",Engine.CI)) &&
                    children.Any(child => File.Exists(Path.Combine(child.FullName, "ibdata1"))))
                    return Set(f,"保留","发现 MySQL 配置和 InnoDB 数据文件。此目录可能保存本地数据库及其运行配置。");
                if ((f.Name.IndexOf("backup",Engine.CI)>=0 || f.Name.Contains("备份")) &&
                    children.Any(child => File.Exists(Path.Combine(child.FullName, "manifest.json"))))
                    return Set(f,"保留","备份目录中发现快照清单。请先核对恢复用途和其他副本。");
                if (ext.Any(e=>new[]{".sqlite",".sqlite3",".db",".sql",".bak",".ibd",".vhdx",".vmdk"}.Contains(e)))
                    return Set(f,"保留","此层发现数据库、备份或虚拟磁盘文件。须先核对所属软件和备份。");
                if (ext.Any(e=>new[]{".docx",".xlsx",".pptx",".pdf",".kicad_pcb",".mp4",".mkv",".jpg",".png"}.Contains(e)))
                    return Set(f,"需要判断","此层发现文档、工程或媒体文件。具体来源尚未确认，按个人资料保留。");
                if (ext.Contains(".exe") || ext.Contains(".dll")) return Set(f,"需要判断","此层发现可执行程序或组件，可能是软件安装或分发目录。请先核对来源。");
                if (String.Equals(Path.GetDirectoryName(f.PathText), Path.GetPathRoot(f.PathText), Engine.CI))
                {
                    foreach (var child in children)
                    {
                        string product;
                        if (Installed.Value.TryGetValue(child.FullName, out product))
                            return Set(f,"保留","下一级包含 Windows 已登记的安装目录，关联程序为 " + product + "。");
                        try
                        {
                            var binary = child.EnumerateFiles("*.exe").Take(12).FirstOrDefault(file =>
                                (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                                !Regex.IsMatch(file.Name, @"^(?:unins|update|crash|setup)", RegexOptions.IgnoreCase));
                            if (binary != null)
                                return Set(f,"需要判断","在 " + child.Name + " 中发现 " + binary.Name + "，可能包含已安装的软件或分发包。");
                        }
                        catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; }
                    }
                }
            }
            catch(Exception ex) { if(!Engine.FileProblem(ex))throw; return Set(f,"访问受限","当前账户无法完整查看这个目录。自动清理会跳过它。"); }
            return Set(f,"需要判断","用途尚未确认。可查看子目录、统计大小或在资源管理器中核对。自动清理会跳过这里。");
        }
        public static Folder Describe(string path, bool link, IList<Rule> rules)
        {
            path = Path.GetFullPath(path);
            string drive = Path.GetPathRoot(path);
            string rel = path.Substring(drive.Length).TrimEnd('\\');
            string[] parts = rel.Split('\\'); string first = parts[0].ToLowerInvariant();
            string name = Path.GetFileName(path.TrimEnd('\\'));
            var f = new Folder { PathText = path, Name = name, IsLink = link };
            if (link) return Set(f, "目录链接", "此处连接到其他位置或使用特殊文件系统功能。扫描和清理都会跳过此入口。");
            Rule rule = rules.FirstOrDefault(r => String.Equals(r.Path, path, Engine.CI));
            if (rule != null) return Set(f, rule.Optional ? "手动勾选" : "可一键清理", rule.Why + " 仅处理符合规则的文件，保留文件夹。");
            string personal;
            if (PersonalFolders.TryGetValue(path,out personal)) return Set(f,"保留",personal);
            if (parts.Length == 1)
            {
                switch (first)
                {
                    case "windows": return Set(f,"系统保护","Windows 系统、驱动和更新组件。请使用 Windows 设置中的存储清理建议。");
                    case "program files": case "program files (x86)": case "windowsapps": return Set(f,"保留","软件安装文件和共享组件。需要移除软件时，请从 Windows 的已安装应用卸载。");
                    case "programdata": return Set(f,"保留","共享程序数据。这里可能有数据库、安装缓存和系统配置，请按所属软件管理。");
                    case "users": return Set(f,rules.Any(r=>Engine.Below(r.Path,path)) ? "可清部分" : "保留","账户、文档、项目和应用设置。清理清单只包含其中已识别的缓存。");
                    case "recovery": return Set(f,"系统保护","Windows 或厂商的修复、重置和恢复资料。");
                    case "perflogs": return Set(f,"保留","Windows 性能诊断日志，可能需要额外权限查看。");
                    case "temp": return Set(f,"需要判断","应用或工具使用的临时工作目录，也可能包含项目和安装材料。先核对实际内容。");
                    case "asusmcenterdownload": return Set(f,"需要判断","华硕软件下载的暂存位置。确认没有更新任务后才考虑清理。");
                    case "appdata": return Set(f,rules.Any(r=>Engine.Below(r.Path,path)) ? "可清部分" : "需要判断","常用于应用数据或其备份。配置、订阅及下载记录需要保留。");
                    case "apps": return Set(f,"保留","通常用于安装软件，程序和快捷方式可能从这里启动。");
                    case "app": if (File.Exists(Path.Combine(path,@"storage\chroma\chroma.sqlite3"))) return Set(f,"保留","本地 AI 知识库，含 Chroma 数据库和模型。请按所属项目管理。"); break;
                    case "appstore": return Set(f,"需要判断","应用或模拟器的存储目录。进入查看后再决定是否保留。");
                    case ".appdata": return Set(f,"需要判断","点号开头的应用私有目录，创建程序尚未确认。");
                    case "uv-cache": case ".pnpm-store": return Set(f,"需要判断","开发工具的软件包存储，可能含链接或离线依赖。通过相应包管理器清理闲置包。");
                    case "caches": return Set(f,"需要判断","可能存放多种应用的缓存，名称无法证明每个文件都可删除。请按子目录判断。");
                    case "system volume information": case "$recycle.bin": case "wpsystem": case "deliveryoptimization": case "wudownloadcache":
                        return Set(f,"系统保护","由 Windows 管理的存储、回收或更新目录。请使用对应的系统功能处理。");
                }
                if (Regex.IsMatch(name,@"^[a-fA-F0-9]{32}$") && File.Exists(Path.Combine(path,"目录说明.txt")) &&
                    Directory.Exists(Path.Combine(path,"下载剧集快捷方式"))) return Set(f,"需要判断","具有腾讯视频离线缓存的目录结构。删除可能使离线剧集无法播放。");
            }
            if (first == "windows" && rel.Equals(@"Windows\WinSxS",Engine.CI)) return Set(f,"系统保护","Windows 组件存储。更新、修复和启用系统功能时会读取这里，部分文件与其他系统目录共享。");
            if (first == "windows" && rel.Equals(@"Windows\System32\DriverStore",Engine.CI)) return Set(f,"系统保护","Windows 已登记的驱动包，安装及维护设备时使用。");
            if (first == "windows" && rel.Equals(@"Windows\Installer",Engine.CI)) return Set(f,"系统保护","已安装软件修复、升级和卸载时使用的 Windows Installer 文件。");
            if (name.Equals("codex-runtimes",Engine.CI) && path.IndexOf(@"\.cache\",Engine.CI)>=0) return Set(f,"保留","Codex 使用的本机工具运行环境，里面可能有正在使用的程序和依赖。");
            if (name.Equals(".git",Engine.CI)) return Set(f,"保留","Git 历史、分支与仓库信息。");
            if(path.EndsWith(@"\OpenAI\Codex-old",Engine.CI))return Set(f,"需要判断","位于 Codex 安装区域的旧版目录候选。名称不代表已经停用；请先核对当前运行路径和软件登记，本工具不会自动删除程序本体。");
            if(name.Equals(".codex",Engine.CI) || name.Equals("archived_sessions",Engine.CI) ||
                path.IndexOf(@"\.codex\sessions",Engine.CI)>=0) return Set(f,"保留","Codex 可能在此存放会话、历史记录、设置或生成文件。");
            if (name.IndexOf("LCEDA",Engine.CI)>=0 || name.IndexOf("嘉立创",Engine.CI)>=0 || name.Equals("EasyEDA",Engine.CI)) return Set(f,"保留","EDA 工程、资源或历史版本，请在设计软件中管理。");
            if (name.Equals("ota-artifacts",Engine.CI) && path.IndexOf(@"\NVIDIA App\UpdateFramework\",Engine.CI)>=0)
                return Set(f,"需要判断","NVIDIA 已下载的更新包，可能正等待安装。准备升级时请保留。");
            if (path.EndsWith(@"\Yarn\Berry\cache",Engine.CI)) return Set(f,"需要判断","Yarn 依赖包。离线开发或直接从缓存加载依赖的项目可能需要这些文件。");
            if (name.Equals("node_modules",Engine.CI) || name.Equals(".venv",Engine.CI)) return Set(f,"保留","项目的开发依赖或运行环境。移除后可能需要重新安装依赖。");
            if (parts.Length>1 && (first=="windows" || first=="recovery" || first=="system volume information" || first=="windowsapps"))
                return Set(f,"系统保护","系统或恢复组件的子目录。请交给 Windows 的存储及维护功能处理。");
            if (parts.Length>1 && (first=="program files" || first=="program files (x86)" || first=="apps"))
                return Set(f,"保留","已安装软件的程序或运行资源，手动删除可能影响启动或卸载。");
            if (name.Equals("Desktop",Engine.CI) || name.Equals("Documents",Engine.CI) || name.Equals("Downloads",Engine.CI))
                return Set(f,"保留","用户存放文档、下载或桌面项目的位置。个人文件由你决定是否保留。");
            if (Directory.Exists(Path.Combine(path,".git")) || File.Exists(Path.Combine(path,"pyproject.toml")) || File.Exists(Path.Combine(path,"package.json")))
                return Set(f,"保留","检测到项目或依赖清单。可能含源码、配置及尚未备份的修改。");
            if (rules.Any(r=>Engine.Below(r.Path,path))) return Set(f,"可清部分","下级目录中有已识别的清理规则。只会处理列入清理清单的文件。");
            if (parts.Length>1 && (first=="appdata" || first=="programdata")) return Set(f,"保留","应用数据目录，可能包含设置、凭据、数据库或下载记录。");
            string installed;
            if (Installed.Value.TryGetValue(path,out installed)) return Set(f,"保留","Windows 的安装记录显示此位置与 "+installed+" 有关。请通过应用的卸载或设置入口管理。");
            return Unknown(f);
        }

        public static List<Folder> Children(string parent, IEnumerable<string> volumes, IList<Rule> rules, out int errors, CancellationToken token)
        {
            var result = new List<Folder>(); errors = 0;
            foreach (string root in parent == null ? volumes : new[] { parent })
            {
                try
                {
                    if (Engine.CheckDirectory(root) != Boundary.Clear) { errors++; continue; }
                    foreach (DirectoryInfo child in new DirectoryInfo(root).EnumerateDirectories())
                    {
                        token.ThrowIfCancellationRequested();
                        try { result.Add(Describe(child.FullName,(child.Attributes & FileAttributes.ReparsePoint)!=0,rules)); }
                        catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; errors++; }
                    }
                }
                catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; errors++; }
            }
            return result.OrderBy(f=>f.Drive).ThenBy(f=>f.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
