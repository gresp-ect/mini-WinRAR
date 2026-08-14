using System.Drawing;
using System.Runtime.InteropServices;

namespace MiniWinRAR;

/// <summary>
/// 从 Windows Shell（shell32）获取文件/目录的系统图标，与资源管理器显示一致。
/// 使用 SHGFI_USEFILEATTRIBUTES：不访问真实文件，仅按路径/扩展名关联图标，快速且不依赖文件存在。
/// </summary>
public static class ShellIcon
{
    private const uint ShgfiIcon = 0x100;            // SHGFI_ICON
    private const uint ShgfiSmallIcon = 0x1;         // SHGFI_SMALLICON
    private const uint ShgfiUseFileAttributes = 0x10; // SHGFI_USEFILEATTRIBUTES
    private const uint FileAttributeDirectory = 0x10; // FILE_ATTRIBUTE_DIRECTORY
    private const uint FileAttributeNormal = 0x80;    // FILE_ATTRIBUTE_NORMAL

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

    /// <summary>按路径/扩展名获取 16x16 系统小图标；失败返回 null。</summary>
    /// <param name="path">目录路径，或文件的扩展名关联路径（如 "*.txt"）。</param>
    /// <param name="isDirectory">是否按目录属性取图标。</param>
    public static Icon? GetIcon(string path, bool isDirectory)
    {
        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes;
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
