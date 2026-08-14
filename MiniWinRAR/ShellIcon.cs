using System.Drawing;
using System.Runtime.InteropServices;

namespace MiniWinRAR;

/// <summary>
/// 从 Windows Shell（shell32）获取文件/目录的系统图标，与资源管理器显示一致。
/// 用 SHGetFileInfo 直接返回 hIcon：16px 用 SHGFI_SMALLICON，更大用默认大图标（32px，由 ImageList 按需放大）。
/// GetIcon：SHGFI_USEFILEATTRIBUTES 模式，不访问真实文件，按路径/扩展名关联图标（用于目录、归档条目、类型图标）。
/// GetIconForFile/GetIconForDirectory：不带 USEFILEATTRIBUTES，读取真实对象自身图标（.exe 内嵌图标 / 特殊文件夹专属图标）。
/// </summary>
public static class ShellIcon
{
    private const uint ShgfiIcon = 0x100;              // SHGFI_ICON
    private const uint ShgfiSmallIcon = 0x1;           // SHGFI_SMALLICON
    private const uint ShgfiUseFileAttributes = 0x10;  // SHGFI_USEFILEATTRIBUTES
    private const uint FileAttributeDirectory = 0x10;  // FILE_ATTRIBUTE_DIRECTORY
    private const uint FileAttributeNormal = 0x80;     // FILE_ATTRIBUTE_NORMAL

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>按路径/扩展名获取系统图标（USEFILEATTRIBUTES，不访问真实文件）；失败返回 null。</summary>
    /// <param name="path">目录路径，或文件的扩展名关联路径（如 "*.txt"）。</param>
    /// <param name="isDirectory">是否按目录属性取图标。</param>
    /// <param name="size">目标尺寸：&lt;=16 用 16px 小图标，否则 32px 大图标（由 ImageList 放大）。</param>
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
        var info = new ShFileInfo();
        var flags = ShgfiIcon | (useFileAttributes ? ShgfiUseFileAttributes : 0);
        if (size <= 16) flags |= ShgfiSmallIcon; // 16px 小图标；否则默认返回 32px 大图标
        var attrs = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        var result = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            // FromHandle 持有的是共享句柄，Clone 一份可独立管理的副本，随后释放源句柄
            return (Icon)Icon.FromHandle(info.hIcon).Clone();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
