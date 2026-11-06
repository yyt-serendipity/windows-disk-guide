using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DiskGuide
{
    internal sealed class UsagePart
    {
        public string Name { get; set; }
        public string PathText { get; set; }
        public bool Readable;
        public long Bytes, Files;
        public int Skipped;
        public double Percent { get; set; }
        public string SizeText { get { return Readable ? (Skipped > 0 ? "≥ " : "") + Engine.Size(Bytes) : "访问受限"; } }
    }

    internal sealed class FolderUsage
    {
        public readonly UsagePart Total = new UsagePart();
        public readonly List<UsagePart> Children = new List<UsagePart>();
        private List<UsagePart> breakdown;
        public List<UsagePart> Largest
        {
            get { return (breakdown ?? Children).Where(p => p.Readable && p.Bytes > 0).OrderByDescending(p => p.Bytes).ThenBy(p => p.Name).Take(3).ToList(); }
        }

        public static FolderUsage Read(string path, CancellationToken token, Action<long, long> report)
        {
            token.ThrowIfCancellationRequested();
            DirectoryInfo wrapper = null;
            try
            {
                if (Engine.CheckDirectory(path) == Boundary.Clear)
                {
                    var firstTwo = new DirectoryInfo(path).EnumerateFileSystemInfos().Take(2).ToList();
                    if (firstTwo.Count == 1 && (firstTwo[0].Attributes & FileAttributes.ReparsePoint) == 0)
                        wrapper = firstTwo[0] as DirectoryInfo;
                }
            }
            catch (Exception ex) { if (!Engine.FileProblem(ex)) throw; }
            if (wrapper == null) return ReadLevel(path, token, report);
            // A lone account folder would otherwise yield one 100% bar.
            // Show its immediate contents without a second recursive scan.
            var inside = ReadLevel(wrapper.FullName, token, report);
            var outer = new FolderUsage();
            outer.Total.Readable = true;
            outer.Total.Bytes = inside.Total.Bytes;
            outer.Total.Files = inside.Total.Files;
            outer.Total.Skipped = inside.Total.Skipped;
            inside.Total.Name = wrapper.Name;
            inside.Total.PathText = wrapper.FullName;
            inside.Total.Percent = inside.Total.Bytes == 0 ? 0 : 100;
            outer.Children.Add(inside.Total);
            if (inside.Children.Any(c => c.PathText != null && c.Bytes > 0))
            {
                outer.breakdown = inside.Largest;
                foreach (var part in outer.breakdown) part.Name = wrapper.Name + "\\" + part.Name;
            }
            return outer;
        }

        // Attribute each file to its first-level child in the same walk used for the total.
        private static FolderUsage ReadLevel(string path, CancellationToken token, Action<long, long> report)
        {
            var result = new FolderUsage();
            var total = result.Total;
            var direct = new UsagePart { Name = "本层文件" };
            var pending = new Stack<Tuple<string, UsagePart>>();
            pending.Push(Tuple.Create(path, (UsagePart)null));
            var clock = Stopwatch.StartNew();
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var next = pending.Pop();
                string directory = next.Item1;
                var group = next.Item2;
                try
                {
                    if (Engine.CheckDirectory(directory) != Boundary.Clear)
                    {
                        total.Skipped++;
                        if (group != null) group.Skipped++;
                        continue;
                    }
                    using (var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().GetEnumerator())
                    {
                        bool more = entries.MoveNext(); // An unreadable root must not appear as an empty folder.
                        if (group == null) total.Readable = true;
                        else if (String.Equals(directory, group.PathText, Engine.CI)) group.Readable = true;
                        while (more)
                        {
                            token.ThrowIfCancellationRequested();
                            FileSystemInfo item = entries.Current;
                            try
                            {
                                FileAttributes flags = item.Attributes;
                                if ((flags & FileAttributes.ReparsePoint) != 0)
                                {
                                    total.Skipped++;
                                    if (group != null) group.Skipped++;
                                }
                                else if ((flags & FileAttributes.Directory) != 0)
                                {
                                    var child = group;
                                    if (child == null)
                                    {
                                        child = new UsagePart { Name = item.Name, PathText = item.FullName };
                                        result.Children.Add(child);
                                    }
                                    pending.Push(Tuple.Create(item.FullName, child));
                                }
                                else
                                {
                                    long bytes = ((FileInfo)item).Length;
                                    var part = group ?? direct;
                                    part.Readable = true;
                                    part.Files++;
                                    part.Bytes += bytes;
                                    total.Bytes += bytes;
                                    total.Files++;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!Engine.FileProblem(ex)) throw;
                                total.Skipped++;
                                if (group != null) group.Skipped++;
                            }
                            if (report != null && clock.ElapsedMilliseconds >= 500)
                            {
                                report(total.Bytes, total.Files);
                                clock.Restart();
                            }
                            more = entries.MoveNext();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!Engine.FileProblem(ex)) throw;
                    total.Skipped++;
                    if (group != null) group.Skipped++;
                }
            }
            if (direct.Files > 0) result.Children.Add(direct);
            foreach (var child in result.Children)
                child.Percent = total.Bytes == 0 ? 0 : 100.0 * child.Bytes / total.Bytes;
            return result;
        }
    }
}
