using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DiskGuide
{
    internal sealed class CleanupSuggestion
    {
        internal long Bytes,OptionalBytes,BusyBytes;
        internal int Items,OptionalItems,BusyItems,UnreadableItems;
        internal bool HasChoices { get { return Items+OptionalItems+BusyItems>0; } }
        internal static CleanupSuggestion From(IEnumerable<ScanRow> rows,string drive)
        {
            var area=rows.Where(r=>String.Equals(r.Drive,drive,Engine.CI)).ToList();
            var candidates=area.Where(r=>r.CanPick && !r.Rule.Optional).ToList();
            return new CleanupSuggestion {
                Bytes=candidates.Sum(r=>r.EligibleBytes),Items=candidates.Count,
                OptionalItems=area.Count(r=>r.CanPick && r.Rule.Optional),OptionalBytes=area.Where(r=>r.CanPick && r.Rule.Optional).Sum(r=>r.EligibleBytes),
                BusyItems=area.Count(r=>r.BusyApp),BusyBytes=area.Where(r=>r.BusyApp).Sum(r=>r.EligibleBytes),UnreadableItems=area.Count(r=>r.Errors>0)
            };
        }
    }

    internal sealed partial class Shell
    {
        private bool scanCompleted;
        private string scanError;
        private void WireHome()
        {
            Get<Button>("HomeCleanBtn").Click+=async delegate {
                if(busy)return;
                var suggestion=CleanupSuggestion.From(rows,scope);
                if(!scanCompleted || !suggestion.HasChoices){await Refresh(false);return;}
                cleanScopePath=null;cleanFilter.SelectedIndex=suggestion.Items>0 ? 1 : 0;BindClean();tabs.SelectedItem=cleanupTab;
            };
            Get<Button>("HomeLargeBtn").Click+=async delegate {
                tabs.SelectedItem=Get<TabItem>("RankingTab");
                // A second visit must not cancel an existing scan or lose its results.
                if(ranking==null && ranked==null){ResetRanking();await ScanRanking();}
            };
            Get<Button>("HomeFoldersBtn").Click+=delegate{tabs.SelectedItem=folderTab;};
            Get<Button>("HomeSoftwareBtn").Click+=delegate{tabs.SelectedItem=Get<TabItem>("SoftwareTab");};
            BindHome();
        }
        private void BindHome()
        {
            Label("HomeTitle",scope.Substring(0,1)+" 盘空间，先看这里");
            try
            {
                var disk=new DriveInfo(scope+@"\");
                if(!disk.IsReady || disk.DriveType!=DriveType.Fixed)throw new IOException();
                long free=disk.AvailableFreeSpace,total=disk.TotalSize;
                double used=total==0?0:100.0*(total-free)/total;
                Label("HomeFree",Engine.Size(free));
                Label("HomeCapacity","剩余空间 · 总容量 "+Engine.Size(total));
                Get<ProgressBar>("HomeDiskBar").Value=used;
                Label("HomeDiskAdvice",used>=90 ? "空间比较紧张，建议先检查可清理缓存。" : "先清缓存；如果还不够，再看看大文件和不用的软件。");
            }
            catch(Exception ex)
            {
                if(!(ex is IOException) && !Engine.FileProblem(ex))throw;
                Label("HomeFree","暂时无法读取");Label("HomeCapacity","请检查磁盘是否可用");
                Label("HomeDiskAdvice","可尝试切换上方磁盘，再重新扫描。");Get<ProgressBar>("HomeDiskBar").Value=0;
            }
            var suggestion=CleanupSuggestion.From(rows,scope);
            Label("HomeCleanSize",busy ? "正在检查…" : !scanCompleted ? "还没有检查结果" : suggestion.Items>0 ? Engine.Size(suggestion.Bytes) : suggestion.HasChoices ? "还有缓存需要你确认" : "暂未发现可清理缓存");
            string note=busy ? "正在检查常见的缓存位置，不会自动删除文件。" :
                scanError!=null ? "这次检查没有完成，请重新扫描。" : !scanCompleted ? "检查完成后，会告诉你哪些缓存可以清理。" :
                suggestion.Items==0 ? (suggestion.HasChoices ? "暂时没有建议直接清理的缓存。" : "这不代表磁盘里没有可整理的文件。可以继续查找大文件。") : "发现 "+suggestion.Items+" 项可清理缓存。先看清单，再决定清理。";
            if(!busy && scanCompleted && suggestion.OptionalItems>0)note+=" 另有 "+Engine.Size(suggestion.OptionalBytes)+" 需先了解清理影响。";
            if(!busy && scanCompleted && suggestion.BusyItems>0)note+=" "+suggestion.BusyItems+" 项需先退出软件"+(suggestion.BusyBytes>0 ? "（"+Engine.Size(suggestion.BusyBytes)+"）" : "")+"。";
            if(!busy && scanCompleted && suggestion.UnreadableItems>0)note+=" 部分位置没能读取，结果可能不完整。";
            if(keep.Error!=null)note="暂时不能清理：你的保留设置没有读取成功。请先查看「不清理的文件」。";
            Label("HomeCleanHint",note);
            var cleanButton=Get<Button>("HomeCleanBtn");
            cleanButton.Content=busy ? "正在检查" : !scanCompleted || !suggestion.HasChoices ? "重新扫描" : suggestion.Items>0 ? "查看可清理项目" : "查看其他清理项";
            cleanButton.IsEnabled=!busy && keep.Error==null;
        }
    }
}
