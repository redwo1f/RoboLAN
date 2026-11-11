using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RoboLAN;

public enum EntryKind { Host, Share, Drive, Folder, File }

public class Entry
{
    public EntryKind Kind;
    public string Name, Path, Type = "";
    public DateTime? Modified;
    public long Size = -1;
    public bool IsContainer => Kind != EntryKind.File;
    public int Icon => Kind switch
    {
        EntryKind.Host => 0,
        EntryKind.Share => 1,
        EntryKind.Drive => 2,
        EntryKind.Folder => 3,
        _ => 4
    };
}

public static class FileSource
{
    public static string MachineOf(string path)
    {
        if (!path.StartsWith(@"\\")) return "Local";
        int end = path.IndexOf('\\', 2);
        return end < 0 ? path.Substring(2) : path.Substring(2, end - 2);
    }

    public static bool IsHostPath(string path) =>
        path.StartsWith(@"\\") && path.IndexOf('\\', 2) < 0 && path.Length > 2;

    public static string Normalize(string path)
    {
        path = (path ?? "").Trim().Trim('"').Replace('/', '\\');
        if (path.Length == 0) return "";
        // A bare name like OFFICE-PC means a PC.
        if (path.IndexOfAny(new[] { '\\', ':' }) < 0) path = @"\\" + path;
        if (path.Length == 2 && path[1] == ':') return path + "\\";
        if (path.Length == 3 && path[1] == ':') return path;
        return path.TrimEnd('\\', '/');
    }

    public static string Parent(string path)
    {
        if (path == "" || IsHostPath(path) || (path.Length == 3 && path[1] == ':')) return "";
        if (path.StartsWith(@"\\"))
        {
            int cut = path.LastIndexOf('\\');
            return path.Substring(0, cut);
        }
        return Directory.GetParent(path)?.FullName ?? "";
    }

    public static List<Entry> ListDrives()
    {
        var list = new List<Entry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string label = "";
            try { if (d.IsReady) label = d.VolumeLabel; } catch { }
            string letter = d.Name.TrimEnd('\\');
            list.Add(new Entry
            {
                Kind = EntryKind.Drive,
                Path = d.Name,
                Name = label.Length > 0 ? $"{label} ({letter})" : letter,
                Type = d.DriveType + " drive"
            });
        }
        return list;
    }

    // The shell's Network folder is what Explorer shows. "net view" fails on modern Windows.
    public static List<string> DiscoverHosts()
    {
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic items = shell.NameSpace(18).Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    string path = items.Item(i).Path;
                    if (path != null && path.StartsWith(@"\\")) hosts.Add(path.Substring(2));
                }
            }
            catch { /* saved and manual PCs are still listed */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return hosts.ToList();
    }

    public static List<Entry> HostEntries(IEnumerable<string> names) => names
        .Select(n => n.Trim().TrimStart('\\'))
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(h => new Entry { Kind = EntryKind.Host, Name = h, Path = @"\\" + h, Type = "Computer" })
        .ToList();

    public static List<Entry> ListShares(string hostPath)
    {
        int resume = 0;
        int rc = NetShareEnum(hostPath, 1, out IntPtr buffer, -1, out int read, out _, ref resume);
        if (rc != 0) throw new Win32Exception(rc);
        var list = new List<Entry>();
        try
        {
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + i * size);
                if ((info.type & 0xFFFF) != 0 || (info.type & 0x80000000) != 0 || info.netname.EndsWith("$")) continue;
                list.Add(new Entry { Kind = EntryKind.Share, Name = info.netname, Path = hostPath + "\\" + info.netname, Type = "Share" });
            }
        }
        finally { NetApiBufferFree(buffer); }
        return list;
    }

    public static List<Entry> ListDirectory(string path)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        var list = new List<Entry>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if (item is DirectoryInfo)
            {
                list.Add(new Entry { Kind = EntryKind.Folder, Name = item.Name, Path = item.FullName, Type = "File folder", Modified = item.LastWriteTime });
            }
            else
            {
                string ext = item.Extension.TrimStart('.').ToUpperInvariant();
                list.Add(new Entry
                {
                    Kind = EntryKind.File, Name = item.Name, Path = item.FullName,
                    Type = ext.Length > 0 ? ext + " File" : "File",
                    Modified = item.LastWriteTime, Size = ((FileInfo)item).Length
                });
            }
        }
        return list;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = -1;
        do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
        return value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00") + " " + units[unit];
    }

    public static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    public static ImageList CreateIcons()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = SystemInformation.SmallIconSize };
        foreach (uint id in new uint[] { 15 /*server*/, 9 /*net drive*/, 8 /*fixed drive*/, 3 /*folder*/, 0 /*document*/ })
        {
            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
            if (SHGetStockIconInfo(id, 0x101 /*SHGSI_ICON|SHGSI_SMALLICON*/, ref info) == 0)
            {
                using var icon = Icon.FromHandle(info.hIcon);
                images.Images.Add(icon.ToBitmap());
                DestroyIcon(info.hIcon);
            }
            else images.Images.Add(new Bitmap(16, 16));
        }
        return images;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHARE_INFO_1
    {
        public string netname;
        public uint type;
        public string remark;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetShareEnum(string server, int level, out IntPtr buffer, int prefMaxLen, out int read, out int total, ref int resume);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("shell32.dll")]
    static extern int SHGetStockIconInfo(uint id, uint flags, ref SHSTOCKICONINFO info);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);
}
