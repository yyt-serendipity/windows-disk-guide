using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace DiskGuide
{
    internal sealed class KeepRule
    {
        public string PathText { get; set; }
        public string PhysicalPath { get; set; }
        public bool Directory { get; set; }
        public string Kind { get { return Directory ? "目录及其内容" : "单个文件"; } }
        internal bool Matches(string path)
        {
            return Match(path,PathText) || !String.IsNullOrEmpty(PhysicalPath) && Match(path,PhysicalPath);
        }
        private bool Match(string path,string root)
        { return String.Equals(path,root,Engine.CI) || Directory && path.StartsWith(root.TrimEnd('\\')+"\\",Engine.CI); }
    }
    internal sealed class KeepSnapshot
    {
        public readonly List<KeepRule> Rules = new List<KeepRule>();
        public string Error;
        public bool Contains(string path) { return Error != null || Rules.Any(r=>r.Matches(PreserveStore.Normalize(path))); }
        public bool BlocksTree(string path)
        {
            if(Error!=null)return true;
            if(String.IsNullOrEmpty(path))return false;
            string root=PreserveStore.Normalize(path);
            return Contains(root) || Rules.Any(r=>Folder.ContainsRule(root,r.PathText) || !String.IsNullOrEmpty(r.PhysicalPath) && Folder.ContainsRule(root,r.PhysicalPath));
        }
    }
    internal sealed class KeepLease : IDisposable
    {
        internal FileStream Lock;
        public KeepSnapshot Snapshot;
        public void Dispose() { if(Lock!=null)Lock.Dispose(); }
    }
    internal sealed class PreserveStore
    {
        internal readonly string FilePath;
        public PreserveStore(string path) { FilePath=Path.GetFullPath(path); }
        public static PreserveStore ForUser()
        {
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new PreserveStore(Path.Combine(Engine.ResolveDirectory(local)??local,"DiskGuide","keep-rules.xml"));
        }
        internal static string Normalize(string path)
        {
            if(String.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[]{'*','?','\0','\r','\n','"'})>=0 || !SoftwareInventory.IsAbsoluteLocal(path))
                throw new ArgumentException("请输入 C 或 D 盘的完整路径，不支持通配符。");
            string full=Path.GetFullPath(path.Trim());
            if(!Engine.OnDisk(full))throw new ArgumentException("保留规则目前仅支持 C、D 盘。");
            return full.Length>3?full.TrimEnd('\\'):full;
        }
        public KeepSnapshot Load()
        {
            var snapshot=new KeepSnapshot();
            try
            {
                try { File.GetAttributes(Path.GetDirectoryName(FilePath)); }
                catch(FileNotFoundException){return snapshot;}
                catch(DirectoryNotFoundException){return snapshot;}
                VerifyDirectory();
                if(!File.Exists(FilePath))
                {
                    // File.Exists also returns false for denied access. Probe attributes before assuming absence.
                    try { File.GetAttributes(FilePath); }
                    catch(FileNotFoundException){return snapshot;}
                    catch(DirectoryNotFoundException){return snapshot;}
                }
                if((File.GetAttributes(FilePath)&FileAttributes.ReparsePoint)!=0)throw new IOException("规则文件不能是链接。");
                using(var stream=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                using(var reader=XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024 }))
                {
                    var doc=new XmlDocument{XmlResolver=null};doc.Load(reader);
                    if(doc.DocumentElement==null || doc.DocumentElement.Name!="keepRules" || doc.DocumentElement.GetAttribute("version")!="1")throw new InvalidDataException("不支持的保留规则格式。");
                    foreach(XmlNode node in doc.DocumentElement.ChildNodes)
                    {
                        if(node is XmlComment)continue;
                        var e=node as XmlElement;
                        if(e==null || e.Name!="keep" || (e.GetAttribute("kind")!="file" && e.GetAttribute("kind")!="directory"))throw new InvalidDataException("保留规则条目无法识别。");
                        if(snapshot.Rules.Count>=10000)throw new InvalidDataException("保留规则数量超出限制。");
                        string physical=e.GetAttribute("physical");
                        snapshot.Rules.Add(new KeepRule{PathText=Normalize(e.GetAttribute("path")),PhysicalPath=physical.Length==0?null:Normalize(physical),Directory=e.GetAttribute("kind")=="directory"});
                    }
                }
            }
            catch(Exception ex){snapshot.Error="保留规则读取失败："+ex.GetBaseException().Message;}
            return snapshot;
        }
        private void VerifyDirectory()
        {
            if(Engine.CheckDirectory(Path.GetDirectoryName(FilePath))!=Boundary.Clear)throw new IOException("规则存储目录不可访问或包含链接。");
        }
        public KeepLease Acquire()
        {
            string directory=Path.GetDirectoryName(FilePath);
            string ancestor=directory;
            while(!System.IO.Directory.Exists(ancestor))
            {
                ancestor=Path.GetDirectoryName(ancestor);
                if(String.IsNullOrEmpty(ancestor))throw new IOException("无法核对规则存储目录。");
            }
            if(Engine.CheckDirectory(ancestor)!=Boundary.Clear)throw new IOException("规则存储目录不可访问或包含链接。");
            System.IO.Directory.CreateDirectory(directory);VerifyDirectory();
            string lockPath=FilePath+".lock";
            if(File.Exists(lockPath) && (File.GetAttributes(lockPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("规则锁文件不能是链接。");
            var held=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            try{return new KeepLease{Lock=held,Snapshot=Load()};}catch{held.Dispose();throw;}
        }
        internal static KeepRule Create(string path,bool directory)
        {
            string full=Normalize(path),probe=directory?full:Path.GetDirectoryName(full);
            string physical=null;
            // Resolve the closest existing directory, preserving nonexistent descendants for future files.
            string ancestor=probe;
            while(!System.IO.Directory.Exists(ancestor))
            {
                var parent=Path.GetDirectoryName(ancestor.TrimEnd('\\'));
                if(String.IsNullOrEmpty(parent) || parent==ancestor)break;
                ancestor=parent;
            }
            if(System.IO.Directory.Exists(ancestor))
            {
                string target=Engine.ResolveDirectory(ancestor);
                if(target==null)throw new IOException("无法核对该位置的实际磁盘，请选择可访问的 C/D 盘路径。");
                physical=Normalize(target.TrimEnd('\\')+full.Substring(ancestor.TrimEnd('\\').Length));
            }
            return new KeepRule{PathText=full,PhysicalPath=physical,Directory=directory};
        }
        public void Add(string path,bool directory)
        {
            var rule=Create(path,directory);
            Change(list=>{if(!list.Any(r=>r.Directory==rule.Directory && String.Equals(r.PathText,rule.PathText,Engine.CI) && String.Equals(r.PhysicalPath,rule.PhysicalPath,Engine.CI)))list.Add(rule);});
        }
        public void Remove(KeepRule rule)
        { Change(list=>list.RemoveAll(r=>r.Directory==rule.Directory && String.Equals(r.PathText,rule.PathText,Engine.CI) && String.Equals(r.PhysicalPath,rule.PhysicalPath,Engine.CI))); }
        private void Change(Action<List<KeepRule>> change)
        {
            using(var lease=Acquire())
            {
                if(lease.Snapshot.Error!=null)throw new IOException(lease.Snapshot.Error);
                change(lease.Snapshot.Rules);
                if(lease.Snapshot.Rules.Count>10000)throw new IOException("保留规则数量超出限制。");
                string temp=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    using(var writer=XmlWriter.Create(output,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true}))
                    {
                        writer.WriteStartElement("keepRules");writer.WriteAttributeString("version","1");
                        foreach(var rule in lease.Snapshot.Rules)
                        {
                            writer.WriteStartElement("keep");writer.WriteAttributeString("kind",rule.Directory?"directory":"file");writer.WriteAttributeString("path",rule.PathText);
                            if(rule.PhysicalPath!=null)writer.WriteAttributeString("physical",rule.PhysicalPath);writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    if(new FileInfo(temp).Length>1024*1024)throw new IOException("保留规则文件超过 1 MiB，请减少规则数量。");
                    if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);
                }
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
        }
    }
}
