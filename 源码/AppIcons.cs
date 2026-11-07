using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

namespace DiskGuide
{
    internal abstract class IconEntry : INotifyPropertyChanged
    {
        private ImageSource icon;
        internal string IconLocation;
        public ImageSource Icon { get { return icon ?? AppIcons.Fallback; } }
        internal void SetIcon(ImageSource value)
        {
            icon = value;
            var changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs("Icon"));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    // Read local resources only: never launch a command or fetch artwork from the network.
    internal static class AppIcons
    {
        internal static readonly ImageSource Fallback = MakeFallback();
        private static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        internal static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        private static ImageSource MakeFallback()
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(231,239,251)), null, new Rect(0,0,32,32), 6,6);
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(73,111,167)), 1.5);
                dc.DrawRoundedRectangle(null, pen, new Rect(7,8,18,16), 2,2);
                dc.DrawLine(pen, new Point(7,13), new Point(25,13));
                dc.DrawLine(pen, new Point(11,17), new Point(17,17));
                dc.DrawLine(pen, new Point(11,20), new Point(21,20));
            }
            var image = new DrawingImage(drawing); image.Freeze(); return image;
        }

        internal static bool TrySource(string text, bool command, out string path, out int index)
        {
            path = null; index = 0;
            if (String.IsNullOrWhiteSpace(text)) return false;
            string value = Environment.ExpandEnvironmentVariables(text.Trim());
            if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), value.Substring(12));
            if (!command)
            {
                var suffix = Regex.Match(value, @",\s*(-?\d+)\s*$");
                if (suffix.Success) { if (!Int32.TryParse(suffix.Groups[1].Value, out index)) return false; value = value.Substring(0,suffix.Index); }
                value = value.Trim().Trim('"');
            }
            else if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = value.IndexOf('"',1); if (end < 0) return false;
                value = value.Substring(1,end-1);
            }
            else
            {
                var executable = Regex.Match(value, @"^(.+?\.(?:exe|com|lnk))(?=\s|$)", RegexOptions.IgnoreCase);
                if (!executable.Success) return false;
                value = executable.Groups[1].Value;
            }
            // Reject UNC/device/relative paths, alternate streams, and mapped network drives.
            if (!Regex.IsMatch(value, @"^[A-Za-z]:\\") || value.IndexOf(':',2)>=0) return false;
            try
            {
                path = Path.GetFullPath(value);
                if (new DriveInfo(Path.GetPathRoot(path)).DriveType != DriveType.Fixed) { path=null; return false; }
                return true;
            }
            catch { path=null; return false; }
        }

        // Reject redirected ancestors as well, so a local-looking path cannot fetch a network resource.
        private static bool LocalFile(string path)
        {
            try
            {
                string validated; int index;
                if(!TrySource("\""+path+"\"",false,out validated,out index))return false;
                var ancestors=new Stack<string>();
                for (string current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                    ancestors.Push(current);
                foreach(string current in ancestors)
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                return File.Exists(path);
            }
            catch { return false; }
        }

        private static ImageSource Read(string path, int index)
        {
            if (!LocalFile(path)) return Fallback;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
            {
                if (new FileInfo(path).Length > 4*1024*1024) return Fallback;
                using (var stream = File.OpenRead(path))
                {
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 32; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
                }
            }
            if (extension != ".exe" && extension != ".dll" && extension != ".ico") return Fallback;
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try
            {
                ExtractIconEx(path, index, out large, out small, 1);
                IntPtr handle = large != IntPtr.Zero ? large : small;
                if (handle == IntPtr.Zero) return Fallback;
                var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32,32));
                image.Freeze(); return image;
            }
            finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero && small != large) DestroyIcon(small); }
        }

        private static string PackageLogo(SoftwareEntry item)
        {
            if (String.IsNullOrWhiteSpace(item.InstallPath)) return null;
            string manifest = Path.Combine(item.InstallPath,"AppxManifest.xml");
            if (!LocalFile(manifest) || new FileInfo(manifest).Length > 1024*1024) return null;
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            var logo = document.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='Logo']");
            if (logo == null || Path.IsPathRooted(logo.InnerText)) return null;
            string path = Path.GetFullPath(Path.Combine(item.InstallPath,logo.InnerText));
            if (!path.StartsWith(item.InstallPath.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) return null;
            if (LocalFile(path)) return path;
            string folder = Path.GetDirectoryName(path);
            // Scale-qualified Windows package assets; no recursive inventory of the application.
            if (!Directory.Exists(folder) || Engine.CheckDirectory(folder) != Boundary.Clear) return null;
            return Directory.EnumerateFiles(folder,Path.GetFileNameWithoutExtension(path)+".scale-*"+Path.GetExtension(path)).Take(20).FirstOrDefault(LocalFile);
        }

        private static ImageSource Shortcut(string path)
        {
            if (!LocalFile(path)) return Fallback;
            object shell=null, link=null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return Fallback;
                shell=Activator.CreateInstance(type);
                link=type.InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{path});
                foreach (string field in new[]{"IconLocation","TargetPath"})
                {
                    string value=Convert.ToString(link.GetType().InvokeMember(field,System.Reflection.BindingFlags.GetProperty,null,link,null));
                    string source; int index;
                    if (TrySource(value,false,out source,out index))
                    { var image=Read(source,index); if (!Object.ReferenceEquals(image,Fallback)) return image; }
                }
                return Fallback;
            }
            finally { if(link!=null && Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link); if(shell!=null && Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell); }
        }

        // Caller holds Gate. Failed reads are cached too; memory remains bounded across refreshes.
        internal static ImageSource Resolve(IconEntry item)
        {
            var startup = item as StartupEntry; var software = item as SoftwareEntry;
            string text = item.IconLocation;
            string key = (text ?? "")+"|"+(startup == null ? (software == null ? "" : software.InstallPath) : startup.Command);
            ImageSource image;
            if (cache.TryGetValue(key,out image)) return image;
            image=Fallback;
            try
            {
                if (String.IsNullOrWhiteSpace(text) && software != null && software.Kind == "商店应用") text=PackageLogo(software);
                string path; int index;
                if (TrySource(text,false,out path,out index)) image=Read(path,index);
                if (Object.ReferenceEquals(image,Fallback) && startup != null && TrySource(startup.Command,true,out path,out index))
                    image=String.Equals(Path.GetExtension(path),".lnk",StringComparison.OrdinalIgnoreCase) ? Shortcut(path) : Read(path,index);
            }
            catch { image=Fallback; }
            if(cache.Count>=512)cache.Clear(); cache[key]=image; return image;
        }
    }

    internal sealed partial class Shell
    {
        private CancellationTokenSource startupIcons, softwareIcons;
        private int iconLoads;
        internal bool IconsIdle { get { return iconLoads==0; } }
        private void QueueIcons(IEnumerable<IconEntry> entries, bool startup)
        {
            var previous=startup ? startupIcons : softwareIcons;
            if(previous!=null){previous.Cancel();previous.Dispose();}
            var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if(startup)startupIcons=current;else softwareIcons=current;
            iconLoads++;
            LoadIcons(entries.ToArray(),current.Token);
        }
        private async void LoadIcons(IconEntry[] entries, CancellationToken token)
        {
            try
            {
                foreach(var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    await AppIcons.Gate.WaitAsync(token);
                    ImageSource image;
                    try { image=await Task.Run(()=>AppIcons.Resolve(entry),token); }
                    finally {AppIcons.Gate.Release();}
                    if(!token.IsCancellationRequested)entry.SetIcon(image);
                }
            }
            catch(OperationCanceledException) { }
            finally { iconLoads--; }
        }
    }
}
