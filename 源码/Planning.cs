using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml;

namespace DiskGuide
{
    internal static class LocalDocument
    {
        internal static XmlDocument Read(string path,long limit=8*1024*1024)
        {
            if(Engine.CheckDirectory(Path.GetDirectoryName(path))!=Boundary.Clear || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0 || new FileInfo(path).Length>limit)throw new IOException("记录位置不可用或内容过大。");
            var doc=new XmlDocument{XmlResolver=null};
            using(var r=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=limit}))doc.Load(r);
            return doc;
        }
        internal static void Write(string path,XmlDocument doc)
        {
            string parent=Path.GetDirectoryName(path),ancestor=parent;
            while(!Directory.Exists(ancestor))ancestor=Path.GetDirectoryName(ancestor);
            if(Engine.CheckDirectory(ancestor)!=Boundary.Clear)throw new IOException("记录目录包含链接或无法访问。");
            Directory.CreateDirectory(parent);
            if(Engine.CheckDirectory(parent)!=Boundary.Clear || File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("记录路径发生变化。");
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))doc.Save(f);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
    }
    internal sealed class GrowthRecord
    {
        public string Key;
        public string PathText { get; set; }
        public string Name { get; set; }
        public long Bytes,Delta;
        public DateTime Time,Previous;
        public string SizeText { get {return Engine.Size(Bytes);} }
        public string ChangeText { get {return Previous==default(DateTime)?"首次记录":Delta==0?"没有变化":(Delta>0?"增加 ":"减少 ")+Engine.Size(Math.Abs(Delta));} }
        public string TimeText { get {return Time.ToLocalTime().ToString("MM-dd HH:mm");} }
        public string ComparedText { get {return Previous==default(DateTime)?"下次扫描后比较":Previous.ToLocalTime().ToString("MM-dd HH:mm");} }
    }
    internal sealed class GrowthStore
    {
        private readonly string path;
        internal string Error;
        internal List<GrowthRecord> Records=new List<GrowthRecord>();
        internal GrowthStore(string file)
        {
            path=file;
            try {
                if(!File.Exists(path))return;
                var doc=LocalDocument.Read(path);if(doc.DocumentElement.Name!="growth")throw new IOException("空间记录格式无法识别。");
                foreach(XmlElement e in doc.DocumentElement.ChildNodes)Records.Add(new GrowthRecord{Key=e.GetAttribute("key"),PathText=e.GetAttribute("path"),Name=e.GetAttribute("name"),Bytes=XmlConvert.ToInt64(e.GetAttribute("bytes")),Delta=XmlConvert.ToInt64(e.GetAttribute("delta")),Time=XmlConvert.ToDateTime(e.GetAttribute("time"),XmlDateTimeSerializationMode.Utc),Previous=e.HasAttribute("previous")?XmlConvert.ToDateTime(e.GetAttribute("previous"),XmlDateTimeSerializationMode.Utc):default(DateTime)});
                if(Records.Count>2000 || Records.Any(r=>r.Bytes<0 || !Engine.OnDisk(r.PathText)))throw new IOException("空间记录范围无效。");
            } catch(Exception ex){Error="历史记录无法读取："+ex.GetBaseException().Message;Records.Clear();}
        }
        internal void Record(string key,string pathText,string name,long bytes,DateTime time)
        {
            if(Error!=null || bytes<0)return;
            var old=Records.FirstOrDefault(r=>String.Equals(r.Key,key,Engine.CI));
            if(old!=null && time<=old.Time)return;
            Records.Remove(old);Records.Add(new GrowthRecord{Key=key,PathText=pathText,Name=name,Bytes=bytes,Time=time,Previous=old==null?default(DateTime):old.Time,Delta=old==null?0:bytes-old.Bytes});
            Records=Records.OrderByDescending(r=>r.Time).Take(2000).ToList();
        }
        internal void Save()
        {
            if(Error!=null)return;
            try {
                var doc=new XmlDocument();var root=doc.CreateElement("growth");doc.AppendChild(root);
                foreach(var r in Records){var e=doc.CreateElement("entry");root.AppendChild(e);e.SetAttribute("key",r.Key);e.SetAttribute("path",r.PathText);e.SetAttribute("name",r.Name);e.SetAttribute("bytes",XmlConvert.ToString(r.Bytes));e.SetAttribute("delta",XmlConvert.ToString(r.Delta));e.SetAttribute("time",XmlConvert.ToString(r.Time,XmlDateTimeSerializationMode.Utc));if(r.Previous!=default(DateTime))e.SetAttribute("previous",XmlConvert.ToString(r.Previous,XmlDateTimeSerializationMode.Utc));}
                LocalDocument.Write(path,doc);
            } catch(Exception ex){Error="历史记录未能保存："+ex.GetBaseException().Message;}
        }
    }
    internal sealed class RecycleInfo {internal long Bytes,Files;internal string Error;}
    internal static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential,Pack=8)] private struct Info {internal uint Size;internal long Bytes,Files;}
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHQueryRecycleBin(string root,ref Info info);
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr owner,string root,uint flags);
        internal static RecycleInfo Read(string root)
        {
            try {if(!Engine.ReadyFixed(root))throw new IOException();var data=new Info{Size=(uint)Marshal.SizeOf(typeof(Info))};int hr=SHQueryRecycleBin(root,ref data);if(hr!=0)Marshal.ThrowExceptionForHR(hr);return new RecycleInfo{Bytes=data.Bytes,Files=data.Files};}
            catch(Exception){return new RecycleInfo{Error="暂时无法读取回收站"};}
        }
        internal static void Empty(string root,IntPtr owner){int hr=SHEmptyRecycleBin(owner,root,7);if(hr!=0)Marshal.ThrowExceptionForHR(hr);}
    }
    internal sealed partial class Shell
    {
        private GrowthStore growth;
        private bool planningReady,recycleBusy;
        private RecycleInfo recycle=new RecycleInfo();
        private string recycleDrive,lastSpaceChange="尚未记录磁盘空间变化";
        internal static long? ReadFree(string drive){try{return new DriveInfo(drive+@"\").AvailableFreeSpace;}catch{return null;}}
        internal static string SpaceChange(long? before,long? after)
        {return !before.HasValue || !after.HasValue?"磁盘可用空间变化暂时无法读取":"磁盘可用空间净"+(after>=before?"增加 ":"减少 ")+Engine.Size(Math.Abs(after.Value-before.Value))+"（含其他程序的同时活动）";}
        private string CleanupSummary()
        {return "删除文件合计 "+Engine.Size(lastCleanup.Where(x=>x.Code==CleanCode.Deleted).Sum(x=>x.Bytes))+" · "+lastCleanup.Count(x=>x.Code==CleanCode.Kept || x.Code==CleanCode.Excluded)+" 个已保留 · "+lastCleanup.Count(x=>x.Code!=CleanCode.Deleted && x.Code!=CleanCode.Kept && x.Code!=CleanCode.Excluded)+" 个未完成\n"+lastSpaceChange;}
        private void WirePlanning()
        {
            growth=new GrowthStore(Path.ChangeExtension(preserveStore.FilePath,"growth.xml"));planningReady=true;
            string transferState=TransferService.Peek(Path.ChangeExtension(preserveStore.FilePath,"transfer.xml"));if(transferState!=null)Label("HomeTransferText",transferState);
            Get<Button>("HomeProtectionBtn").Click+=delegate{ShowKeepRules();};
            Get<Button>("HomeProtectGamesBtn").Click+=delegate{SaveKeep(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"NVIDIA\DXCache"),true);};
            Get<Button>("HomeProtectFolderBtn").Click+=delegate{string chosen=ChooseFolder("选择要长期保护的文件夹，例如 EDA 工程或备份");if(chosen!=null)SaveKeep(chosen,true);};
            Get<Button>("HomeOptionalBtn").Click+=delegate{cleanScopePath=null;cleanFilter.SelectedIndex=0;BindClean();tabs.SelectedItem=cleanupTab;};
            Get<Button>("HomeRecycleBtn").Click+=async delegate {await ClearHomeRecycle();};
            Get<Button>("HomeGrowthBtn").Click+=delegate{ShowGrowth();};
            Get<Button>("HomeTransferBtn").Click+=delegate{ShowTransfer();};
            Get<Button>("HomeResultsBtn").Click+=delegate{ShowCleanupResults();};
            View.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(transferRunning){e.Cancel=true;MessageBox.Show(View,"文件迁移仍在进行。请先在迁移窗口取消，等待它停止后再关闭。","迁移尚未停止");}};
            BindPlanning();
        }
        private static string ChooseFolder(string description)
        {using(var picker=new System.Windows.Forms.FolderBrowserDialog{Description=description,ShowNewFolderButton=false})return picker.ShowDialog()==System.Windows.Forms.DialogResult.OK?picker.SelectedPath:null;}
        private async void RefreshRecycle(string drive)
        {
            if(recycleBusy)return;recycleBusy=true;recycleDrive=drive;
            try{recycle=await Task.Run(()=>RecycleBin.Read(drive+@"\"));}finally{recycleBusy=false;if(!lifetime.IsCancellationRequested)BindPlanning();}
        }
        private void BindPlanning()
        {
            if(recycleDrive!=scope && !recycleBusy)RefreshRecycle(scope);
            var suggestion=CleanupSuggestion.From(rows,scope);
            Label("HomeOptionalText",!scanCompleted?"扫描完成后显示需确认的项目":"需先确认："+Engine.Size(suggestion.OptionalBytes)+" · 正在使用："+Engine.Size(suggestion.BusyBytes));
            Label("HomeProtectionText",keep.Error??("保留设置已生效 · "+keep.Rules.Count+" 个保护位置 · 本次 "+rows.SelectMany(r=>r.Files).Count(f=>f.Preserved)+" 个文件已保护"));
            string shaders=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"NVIDIA\DXCache");
            Get<Button>("HomeProtectGamesBtn").Content=keep.Contains(shaders)?"游戏缓存已保护":"保护游戏缓存";
            Get<Button>("HomeProtectGamesBtn").IsEnabled=!busy && keep.Error==null && !keep.Contains(shaders);
            Get<Button>("HomeProtectFolderBtn").IsEnabled=!busy && keep.Error==null;Get<Button>("HomeProtectionBtn").IsEnabled=!busy;
            Get<Button>("HomeTransferBtn").ToolTip="迁移 C 盘个人目录中的文件夹；应用配置请用软件自身的迁移功能。";
            Label("HomeRecycleText",recycleBusy || recycleDrive!=scope?"正在读取…":recycle.Error??(Engine.Size(recycle.Bytes)+" · "+recycle.Files+" 项，清空后无法恢复"));
            Get<Button>("HomeRecycleBtn").IsEnabled=!busy && !recycleBusy && recycleDrive==scope && recycle.Error==null && recycle.Files>0 && !keep.BlocksTree(scope+@"\$Recycle.Bin");
            Label("HomeGrowthText",growth.Error??(growth.Records.Count==0?"尚无历史记录。扫描后建立基线，下次扫描再比较。":"已记录 "+growth.Records.Count+" 个位置，可查看最近两次完整扫描的变化。"));
            Get<TextBlock>("HomeResultText").Visibility=lastCleanup.Count>0?Visibility.Visible:Visibility.Collapsed;
            Label("HomeResultText",lastCleanup.Count>0?CleanupSummary():"");Get<Button>("HomeResultsBtn").Visibility=lastCleanup.Count>0?Visibility.Visible:Visibility.Collapsed;
        }
        private async Task ClearHomeRecycle()
        {
            string drive=scope;if(recycleBusy || busy)return;
            if(MessageBox.Show(View,"清空 "+drive+" 盘回收站中的文件？\n清空后无法从回收站恢复。请先确认没有需要找回的文件。","清空回收站",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            recycleBusy=true;BindPlanning();Activity("recycle","正在清空 "+drive+" 盘回收站…");
            var owner=new System.Windows.Interop.WindowInteropHelper(View).Handle;
            try {
                using(var lease=preserveStore.Acquire()){
                    if(lease.Snapshot.BlocksTree(drive+@"\$Recycle.Bin"))throw new IOException("回收站或上级目录属于保留范围，未清空。");
                    await Task.Run(()=>RecycleBin.Empty(drive+@"\",owner));
                }
                EndActivity("recycle","回收站已处理",false);
            } catch(Exception ex){EndActivity("recycle","回收站未完成："+ex.GetBaseException().Message,true);}
            finally{recycleBusy=false;recycleDrive=null;if(!lifetime.IsCancellationRequested)BindHome();}
        }
        private void RecordCacheGrowth()
        {
            if(growth==null)return;var now=DateTime.UtcNow;
            foreach(var r in rows.Where(r=>r.Errors==0))growth.Record("cache|"+r.Rule.Kind+"|"+r.Rule.Days+"|"+r.PathText,r.PathText,r.Name,r.Bytes,now);
            growth.Save();recycleDrive=null;
        }
        private void RecordFolderGrowth(string root,FolderUsage value)
        {
            if(growth==null || !value.Total.Readable)return;
            foreach(var r in value.Children.Where(r=>r.PathText!=null && r.Readable && r.Skipped==0))growth.Record("folder|"+root+"|"+r.PathText,r.PathText,r.Name,r.Bytes,DateTime.UtcNow);
            growth.Save();BindPlanning();
        }
        private void ShowGrowth()
        {
            var dialog=DetailWindow("最近哪些位置又变大了",1000,650);var panel=new DockPanel{Margin=new Thickness(20)};
            var intro=new TextBlock{Text="比较同一位置最近两次完整扫描。缓存记录和文件夹排行分别统计，不相加。\n仅在你扫描时记录；首次扫描没有增长结论。"+(growth.Error==null?"":"\n"+growth.Error),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)};DockPanel.SetDock(intro,Dock.Top);panel.Children.Add(intro);
            var advice=new TextBlock{Text="缓存持续增长：打开对应软件的设置，查找「缓存位置 / 下载位置 / 存储位置」并改到 D 盘。运行中的软件数据请优先使用它自己的迁移功能。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(advice,Dock.Bottom);panel.Children.Add(advice);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0)};var open=new Button{Content="打开选中位置",IsEnabled=false};actions.Children.Add(open);var clear=new Button{Content="清除历史记录",Margin=new Thickness(8,0,0,0)};actions.Children.Add(clear);DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);
            clear.IsEnabled=growth.Error==null;
            var grid=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false};grid.Columns.Add(TextColumn("位置","Name",-1));grid.Columns.Add(TextColumn("当前大小","SizeText",100));grid.Columns.Add(TextColumn("相比上次","ChangeText",140));grid.Columns.Add(TextColumn("本次扫描","TimeText",110));grid.Columns.Add(TextColumn("对比扫描","ComparedText",130));
            grid.Columns[0].MinWidth=150;grid.SizeChanged+=delegate{FitColumns(grid,0,0,90,110,105,120);};
            Action bind=()=>{grid.ItemsSource=growth.Records.Where(r=>r.PathText.StartsWith(scope,Engine.CI)).OrderByDescending(r=>r.Delta).ThenByDescending(r=>r.Time).ToList();};bind();panel.Children.Add(grid);
            grid.SelectionChanged+=delegate{open.IsEnabled=grid.SelectedItem!=null;};open.Click+=delegate{var r=grid.SelectedItem as GrowthRecord;if(r!=null)OpenLocation(r.PathText,false);};
            clear.Click+=delegate{if(MessageBox.Show(dialog,"清除扫描历史？只删除比较记录，不删除磁盘文件。","清除历史",MessageBoxButton.YesNo)==MessageBoxResult.Yes){growth.Records.Clear();growth.Save();bind();BindPlanning();}};
            dialog.Content=panel;dialog.Show();
        }
    }
}
