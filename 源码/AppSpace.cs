using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DiskGuide
{
    internal sealed class AppLocation
    {
        public string Category { get; set; }
        public string SourcePath { get; set; }
        public string PathText { get; set; }
        public string Evidence { get; set; }
        public string Note { get; set; }
        public bool Confirmed, Counted;
        public UsagePart Usage;
        public string Drive { get { return String.IsNullOrEmpty(PathText) ? "—" : Path.GetPathRoot(PathText).TrimEnd('\\'); } }
        public string SizeText { get { return Usage == null ? (Note ?? "待统计") : Usage.SizeText; } }
        public long SizeBytes { get { return Usage == null ? -1 : Usage.SizeBytes; } }
        public string Attribution { get { return Confirmed ? "登记关联" : "待核对"; } }
    }

    internal sealed class AppSpaceReport
    {
        public readonly List<AppLocation> Locations = new List<AppLocation>();
        public long Bytes(string drive, bool confirmed)
        { return Locations.Where(x => x.Counted && x.Confirmed == confirmed && x.Drive == drive && x.Usage != null).Sum(x => x.Usage.Bytes); }
        public bool Partial { get { return Locations.Any(x => x.Usage == null || !x.Usage.Readable || x.Usage.Skipped > 0); } }
    }

    internal static class AppSpace
    {
        private static void Add(List<AppLocation> list, string path, string category, string evidence, bool confirmed)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return;
            list.Add(new AppLocation { SourcePath = path, Category = category, Evidence = evidence, Confirmed = confirmed });
        }
        internal static List<AppLocation> Locate(SoftwareEntry app, SoftwareInventoryResult inventory, IList<Rule> rules)
        {
            var result = new List<AppLocation>();
            if (!String.IsNullOrEmpty(app.InstallPath))
            {
                bool shared = inventory.Warnings.Count != 0 || inventory.Items.Any(x => x.Id != app.Id && !String.IsNullOrEmpty(x.InstallPath) &&
                    (Folder.ContainsRule(app.InstallPath,x.InstallPath) || Folder.ContainsRule(x.InstallPath,app.InstallPath)));
                Add(result, app.InstallPath, "安装文件", shared ? "软件登记；路径与其他软件重叠或登记读取不完整，不能独占归属" : "软件卸载登记 / Windows 包安装位置", !shared);
            }
            else if (app.PackageFullName == null)
            {
                string executable, arguments, reason;
                if (SoftwareInventory.TryParseUninstall(app.UninstallCommand, out executable, out arguments, out reason) &&
                    !String.Equals(Path.GetFileName(executable),"msiexec.exe",Engine.CI) && !Folder.ContainsRule(Environment.GetFolderPath(Environment.SpecialFolder.Windows),executable))
                    Add(result,Path.GetDirectoryName(executable),"卸载器所在目录","来自软件登记的卸载命令；仅为候选位置，不保证等于完整安装目录",false);
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!String.IsNullOrEmpty(app.PackageFamilyName) && app.PackageFamilyName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !app.PackageFamilyName.Contains(".."))
                Add(result, Path.Combine(local,"Packages",app.PackageFamilyName), "配置 / 用户数据", "Windows PackageFamilyName 精确关联；包含缓存、设置与存储数据", true);
            string name = app.Name ?? "";
            string profile = null;
            if (name.Equals("Google Chrome",Engine.CI)) profile = Path.Combine(local,@"Google\Chrome\User Data");
            else if (name.Equals("Microsoft Edge",Engine.CI)) profile = Path.Combine(local,@"Microsoft\Edge\User Data");
            else if (name.Equals("豆包",Engine.CI) || name.Equals("Doubao",Engine.CI)) profile = Path.Combine(local,@"Doubao\User Data");
            else if (name.Equals("抖音",Engine.CI) || name.Equals("Douyin",Engine.CI)) profile = Path.Combine(roaming,"douyin");
            else if (name.Equals("百度网盘",Engine.CI) || name.Equals("BaiduNetdisk",Engine.CI)) profile = Path.Combine(roaming,"baidunetdisk");
            else if (name.Equals("Clash Verge",Engine.CI) || name.Equals("Clash Verge Rev",Engine.CI)) profile = Path.Combine(roaming,"io.github.clash-verge-rev.clash-verge-rev");
            else if (name.Equals("Docker Desktop",Engine.CI)) profile = Path.Combine(local,"Docker");
            if (profile != null)
            {
                Add(result, profile, "配置 / 用户数据（混合）", "产品名称匹配已知目录模板，需要核对；不作为删除依据", false);
                string resolved = null;
                try { resolved = Engine.ResolveDirectory(profile); } catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; }
                foreach (Rule rule in rules.Where(r => Folder.ContainsRule(profile,r.Path) || resolved != null && Folder.ContainsRule(resolved,r.Path)))
                    Add(result, rule.Path, "已识别缓存 / 日志", "现有缓存规则：" + rule.Name + "；目录大小不等于可清大小", false);
            }
            return result;
        }

        internal static bool IsSharedRoot(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return true;
            var protectedPaths = new[] { Path.GetPathRoot(path), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
            return protectedPaths.Any(p => !String.IsNullOrEmpty(p) && String.Equals(path.TrimEnd('\\'),p.TrimEnd('\\'),Engine.CI));
        }

        public static AppSpaceReport Read(IEnumerable<AppLocation> locations, CancellationToken token, Action<string> progress)
        {
            var report = new AppSpaceReport();
            foreach (var item in locations)
            {
                token.ThrowIfCancellationRequested();
                item.PathText = null; item.Counted = false; item.Usage = null; item.Note = null;
                report.Locations.Add(item);
                try
                {
                    item.PathText = Engine.ResolveDirectory(item.SourcePath);
                    if (item.PathText == null) { item.Note = "不存在、访问受限或非 C/D 盘"; continue; }
                    if (IsSharedRoot(item.PathText)) { item.Note = "共享根目录，不计入软件占用"; item.PathText = null; continue; }
                    if (!String.Equals(item.SourcePath.TrimEnd('\\'),item.PathText.TrimEnd('\\'),Engine.CI)) item.Evidence += "；原路径解析到实际位置（按实际磁盘统计）";
                }
                catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; item.Note = "读取受限"; }
            }
            var measured = new List<AppLocation>();
            foreach (var item in report.Locations.Where(x => x.PathText != null).OrderBy(x => x.PathText.Length).ThenByDescending(x => x.Confirmed))
            {
                token.ThrowIfCancellationRequested();
                var same = measured.FirstOrDefault(x => String.Equals(x.PathText,item.PathText,Engine.CI));
                if (same != null) { item.Usage = same.Usage; item.Note = "与其他条目是同一实际目录，不重复合计"; continue; }
                item.Counted = !measured.Any(x => Folder.ContainsRule(x.PathText,item.PathText));
                item.Note = item.Counted ? "计入已发现位置合计" : "已包含在上级目录，不重复合计";
                if (progress != null) progress("正在统计 " + item.PathText);
                item.Usage = FolderUsage.Read(item.PathText,token, (bytes, files) => {
                    if (progress != null) progress("正在统计 " + item.Category + " · 已读 " + Engine.Size(bytes) + " / " + files.ToString("N0") + " 个文件");
                }).Total;
                measured.Add(item);
            }
            return report;
        }
    }
}
