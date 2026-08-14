using ICSharpCode.SharpZipLib.Zip;
using MiniWinRAR.Core.Mwr;

namespace MiniWinRAR.Core.Archive;

/// <summary>
/// 归档加密探测：不解密即可判断一个归档文件是否需要密码。
/// .zip 的 central directory 明文可读；.mwr 固定头的 flags 位（偏移 5）明文可读。
/// 无法读取/未知时保守返回 true（允许用户输入密码）。
/// </summary>
public static class ArchiveProbe
{
    public static bool IsEncrypted(string path)
    {
        try
        {
            return Path.GetExtension(path).Equals(".mwr", StringComparison.OrdinalIgnoreCase)
                ? ProbeMwr(path)
                : ProbeZip(path);
        }
        catch
        {
            return true; // 无法探测 → 保守显示密码框
        }
    }

    /// <summary>.mwr 固定头：Magic(4) + Version(1) + Flags(1) + Salt(16)，Flags bit0 = 加密。</summary>
    private static bool ProbeMwr(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[6];
        if (fs.Read(buf, 0, 6) < 6) return false;
        return (buf[5] & MwrFormat.FlagEncrypted) != 0;
    }

    private static bool ProbeZip(string path)
    {
        using var zf = new ZipFile(path);
        foreach (ZipEntry e in zf)
            if (e.IsCrypted || e.AESKeySize > 0) return true;
        return false;
    }
}
