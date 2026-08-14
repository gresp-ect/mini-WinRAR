using System.Drawing;
using System.Runtime.InteropServices;

namespace MiniWinRAR;

/// <summary>
/// 从 Windows Shell（shell32）获取文件/目录的系统图标，与资源管理器显示一致。
/// 图标按目标尺寸从系统图标列表（SHGetImageList，SHIL_SMALL/LARGE/EXTRALARGE）取出，各档位清晰而非拉伸。
/// GetIcon：SHGFI_USEFILEATTRIBUTES 模式，不访问真实文件，按路径/扩展名关联图标（用于目录、归档条目、类型图标）。
/// GetIconForFile/GetIconForDirectory：不带 USEFILEATTRIBUTES，读取真实对象自身图标（.exe 内嵌图标 / 特殊文件夹专属图标）。
/// </summary>
public static class ShellIcon
{
    private const uint ShgfiUseFileAttributes = 0x10;   // SHGFI_USEFILEATTRIBUTES
    private const uint ShgfiSysIconIndex = 0x4000;       // SHGFI_SYSICONINDEX
    private const uint FileAttributeDirectory = 0x10;    // FILE_ATTRIBUTE_DIRECTORY
    private const uint FileAttributeNormal = 0x80;       // FILE_ATTRIBUTE_NORMAL
    private const int ShilLarge = 0;                      // 32px
    private const int ShilSmall = 1;                      // 16px
    private const int ShilExtraLarge = 2;                 // 48px
    private const int IldTransparent = 0x0001;
    private static readonly Guid IidImageList = new("46EB5926-5820-4464-9378-90A647F474E8");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>按路径/扩展名获取指定尺寸的系统小图标（USEFILEATTRIBUTES，不访问真实文件）；失败返回 null。</summary>
    /// <param name="path">目录路径，或文件的扩展名关联路径（如 "*.txt"）。</param>
    /// <param name="isDirectory">是否按目录属性取图标。</param>
    /// <param name="size">目标尺寸（16 / 32 / 48）。</param>
    public static Icon? GetIcon(string path, bool isDirectory, int size = 16)
        => GetIconCore(path, isDirectory, useFileAttributes: true, size);

    /// <summary>读取真实文件的系统图标（含文件自身的内嵌图标，如 .exe/.ico/.lnk）；失败返回 null。</summary>
    public static Icon? GetIconForFile(string fullPath, int size = 16)
        => GetIconCore(fullPath, isDirectory: false, useFileAttributes: false, size);

    /// <summary>读取真实目录的系统图标：特殊/已知文件夹（桌面、音乐、下载等）返回专属图标，普通目录返回通用文件夹图标；失败返回 null。</summary>
    public static Icon? GetIconForDirectory(string fullPath, int size = 16)
        => GetIconCore(fullPath, isDirectory: true, useFileAttributes: false, size);

    private static Icon? GetIconCore(string path, bool isDirectory, bool useFileAttributes, int size)
    {
        // 1) 取系统图标索引（USEFILEATTRIBUTES 时无需真实文件）
        var info = new ShFileInfo();
        var flags = ShgfiSysIconIndex | (useFileAttributes ? ShgfiUseFileAttributes : 0);
        var attrs = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        if (SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero)
            return null;

        // 2) 按目标尺寸从系统图标列表取图标
        var shil = size <= 16 ? ShilSmall : size <= 32 ? ShilLarge : ShilExtraLarge;
        var iid = IidImageList; // 局部副本：static readonly 字段不能作 ref 参数
        if (SHGetImageList(shil, ref iid, out var himl) != 0 || himl == IntPtr.Zero)
            return null;
        var hIcon = ImageList_GetIcon(himl, info.iIcon, IldTransparent);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            // FromHandle 持有的是共享句柄，Clone 一份可独立管理的副本，随后释放源句柄
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
