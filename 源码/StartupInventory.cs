using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.Win32;

namespace DiskGuide
{
    internal sealed class StartupEntry
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string State { get; set; }
        public string Command { get; set; }
        public string Location { get; set; }
        public string Publisher { get; set; }
    }

    internal sealed class StartupInventoryResult
    {
        public List<StartupEntry> Items = new List<StartupEntry>();
        public List<string> Warnings = new List<string>();
    }

    // Read-only inventory. Configured activation is distinct from current process state.
    internal static class StartupInventory
    {
        private const string Current = @"Software\Microsoft\Windows\CurrentVersion\";
        public static StartupInventoryResult Read()
        {
            var result = new StartupInventoryResult();
            ReadRun(result, RegistryHive.CurrentUser, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            foreach (RegistryView view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
                ReadRun(result, RegistryHive.LocalMachine, view);
            ReadFolder(result, Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, "用户启动文件夹");
            ReadFolder(result, Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, "公共启动文件夹");
            ReadServices(result);
            ReadTasks(result);
            result.Items = result.Items.GroupBy(x => x.Location + "\n" + x.Name + "\n" + x.Command, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).OrderBy(x => x.Source).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return result;
        }

        // StartupApproved is an undocumented Windows implementation detail. Unknown
        // encodings stay unknown; existence of Run alone is never presented as proof.
        internal static string ApprovalState(byte[] value)
        {
            if (value == null) return "已登记 · 启用状态未确认";
            if (value.Length < 12 || value[1] != 0 || value[2] != 0 || value[3] != 0) return "状态未知";
            if (value[0] == 2) return "已启用（系统记录）";
            if (value[0] == 3) return "已禁用（系统记录）";
            return "状态未知（系统记录 " + value[0] + "）";
        }

        private static string Approval(RegistryHive hive, string group, string name, StartupInventoryResult result)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32))
                using (var key = root.OpenSubKey(Current + @"Explorer\StartupApproved\" + group, false))
                    return ApprovalState(key == null ? null : key.GetValue(name) as byte[]);
            }
            catch (Exception ex) { Warn(result, "启动状态 " + name, ex); return "无法读取状态"; }
        }

        private static void ReadRun(StartupInventoryResult result, RegistryHive hive, RegistryView view)
        {
            string who = hive == RegistryHive.CurrentUser ? "当前用户" : "所有用户";
            foreach (string leaf in new[] { "Run", "RunOnce" })
            {
                string path = Current + leaf;
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = root.OpenSubKey(path, false))
                    {
                        if (key == null) continue;
                        foreach (string name in key.GetValueNames())
                        {
                            try
                            {
                                string command = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                                if (String.IsNullOrWhiteSpace(command)) continue;
                                result.Items.Add(new StartupEntry { Name = name, Command = command, Publisher = "",
                                    Source = leaf == "RunOnce" ? "一次性登录启动" : "登录启动",
                                    State = leaf == "RunOnce" ? "已登记 · 下次适用登录时执行" : Approval(hive, view == RegistryView.Registry32 && Environment.Is64BitOperatingSystem && hive == RegistryHive.LocalMachine ? "Run32" : "Run", name, result),
                                    Location = (hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM") + "\\" + path + " [" + who + " / " + view + "]" });
                            }
                            catch (Exception ex) { Warn(result, path + " / " + name, ex); }
                        }
                    }
                }
                catch (Exception ex) { Warn(result, who + " " + path, ex); }
            }
        }

        private static void ReadFolder(StartupInventoryResult result, Environment.SpecialFolder folder, RegistryHive hive, string source)
        {
            string path = Environment.GetFolderPath(folder);
            try
            {
                if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                foreach (string file in Directory.EnumerateFiles(path))
                {
                    if (String.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Items.Add(new StartupEntry { Name = Path.GetFileNameWithoutExtension(file), Source = source,
                        State = Approval(hive, "StartupFolder", Path.GetFileName(file), result),
                        Command = file, Location = path, Publisher = "" });
                }
            }
            catch (Exception ex) { Warn(result, source, ex); }
        }

        internal static bool IsAutomaticUserService(int start, int type)
        { return start == 2 && (type & 0x30) != 0; }

        private static void ReadServices(StartupInventoryResult result)
        {
            const string path = @"SYSTEM\CurrentControlSet\Services";
            try
            {
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var services = root.OpenSubKey(path, false))
                {
                    if (services == null) return;
                    foreach (string name in services.GetSubKeyNames())
                    {
                        try
                        {
                            using (var key = services.OpenSubKey(name, false))
                            {
                                if (key == null || !IsAutomaticUserService(Convert.ToInt32(key.GetValue("Start", -1)), Convert.ToInt32(key.GetValue("Type", 0)))) continue;
                                string display = key.GetValue("DisplayName") as string;
                                result.Items.Add(new StartupEntry { Name = String.IsNullOrEmpty(display) || display.StartsWith("@", StringComparison.Ordinal) ? name : display,
                                    Source = "自动服务", State = Convert.ToInt32(key.GetValue("DelayedAutoStart", 0)) == 1 ? "自动（延迟启动）· 未读取运行状态" : "自动启动 · 未读取运行状态",
                                    Command = Convert.ToString(key.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames)),
                                    Location = "HKLM\\" + path + "\\" + name, Publisher = "" });
                            }
                        }
                        catch (Exception ex) { Warn(result, "服务 " + name, ex); }
                    }
                }
            }
            catch (Exception ex) { Warn(result, "自动服务", ex); }
        }

        // COM automation through reflection avoids adding a dynamic-binder dependency.
        private static object Call(object target, string name, params object[] args)
        { return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args); }
        private static object Get(object target, string name, params object[] args)
        { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args); }
        private static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        private static void ReadTasks(StartupInventoryResult result)
        {
            object service = null, folder = null;
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) { result.Warnings.Add("计划任务接口不可用。"); return; }
                service = Activator.CreateInstance(type);
                Call(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                folder = Call(service, "GetFolder", "\\");
                ReadTaskFolder(result, folder, 0);
            }
            catch (Exception ex) { Warn(result, "计划任务", ex); }
            finally { Release(folder); Release(service); }
        }

        private static void ReadTaskFolder(StartupInventoryResult result, object folder, int depth)
        {
            if (depth > 64) { result.Warnings.Add("计划任务目录超过 64 层，部分内容未读取。"); return; }
            object tasks = null, children = null;
            string location = "计划任务";
            try
            {
                location = Convert.ToString(Get(folder, "Path"));
                tasks = Call(folder, "GetTasks", 1); // Include hidden tasks, read only.
                int count = Convert.ToInt32(Get(tasks, "Count"));
                for (int i = 1; i <= count; i++)
                {
                    object task = null;
                    try
                    {
                        task = Get(tasks, "Item", i);
                        StartupEntry item = ParseTask(Convert.ToString(Get(task, "Xml")), Convert.ToString(Get(task, "Name")),
                            Convert.ToString(Get(task, "Path")), Convert.ToBoolean(Get(task, "Enabled")), Convert.ToInt32(Get(task, "State")));
                        if (item != null) result.Items.Add(item);
                    }
                    catch (Exception ex) { Warn(result, location + " 任务 " + i, ex); }
                    finally { Release(task); }
                }
            }
            catch (Exception ex) { Warn(result, location, ex); }
            finally { Release(tasks); }
            try
            {
                children = Call(folder, "GetFolders", 0);
                int count = Convert.ToInt32(Get(children, "Count"));
                for (int i = 1; i <= count; i++)
                {
                    object child = null;
                    try { child = Get(children, "Item", i); ReadTaskFolder(result, child, depth + 1); }
                    catch (Exception ex) { Warn(result, location + " 子目录 " + i, ex); }
                    finally { Release(child); }
                }
            }
            catch (Exception ex) { Warn(result, location + " 子目录", ex); }
            finally { Release(children); }
        }

        internal static StartupEntry ParseTask(string xml, string name, string path, bool enabled, int state)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 })) doc.Load(reader);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            var triggers = doc.SelectNodes("/t:Task/t:Triggers/t:BootTrigger | /t:Task/t:Triggers/t:LogonTrigger", ns);
            if (triggers.Count == 0) return null;
            bool active = false;
            var types = new List<string>();
            foreach (XmlNode trigger in triggers)
            {
                types.Add(trigger.LocalName == "BootTrigger" ? "开机" : "登录");
                var flag = trigger.SelectSingleNode("t:Enabled", ns);
                if (flag == null || (flag.InnerText != "false" && flag.InnerText != "0")) active = true;
            }
            var commands = new List<string>();
            foreach (XmlNode action in doc.SelectNodes("/t:Task/t:Actions/*", ns))
            {
                if (action.LocalName == "Exec")
                {
                    XmlNode cmd = action.SelectSingleNode("t:Command", ns), args = action.SelectSingleNode("t:Arguments", ns);
                    commands.Add((cmd == null ? "" : cmd.InnerText) + (args == null ? "" : " " + args.InnerText));
                }
                else commands.Add("[" + action.LocalName + "] " + action.InnerText);
            }
            string config = !enabled ? "任务已禁用" : !active ? "启动触发器已禁用" : "已启用（可能受条件限制）";
            string running = state == 4 ? "当前运行中" : state == 2 ? "当前排队中" : state == 0 ? "当前运行状态未知" : "当前未运行";
            return new StartupEntry { Name = name, Source = "计划任务 · " + String.Join("/", types.Distinct()),
                State = config + " · " + running,
                Command = String.Join(" ; ", commands), Location = path, Publisher = "" };
        }

        private static void Warn(StartupInventoryResult result, string area, Exception ex)
        { result.Warnings.Add(area + "：" + ex.GetBaseException().Message); }
    }
}
