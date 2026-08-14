namespace MiniWinRAR.Core.Archive;

/// <summary>归档路径判定工具：判断一个路径是否指向本工具支持的归档文件（.zip / .mwr）。</summary>
public static class ArchivePath
{
    public static bool IsArchive(string path) =>
        Path.GetExtension(path) is var ext &&
        (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
         ext.Equals(".mwr", StringComparison.OrdinalIgnoreCase));
}
