namespace MiniWinRAR.Core.Archive;

/// <summary>收集到的文件条目。Name 为归档内 '/' 分隔相对名，Path 为源绝对路径。</summary>
public record FileEntry(string Name, string Path, bool IsDir, long Size, long Mtime);

/// <summary>
/// 把一组文件/目录路径展开为归档条目列表（与 Rust 版 <c>collect_entries</c> 语义一致）：
/// 文件输入 → Name = 文件名；目录输入 → 顶层目录条目用 basename，子项相对该目录（"a.txt"、"sub/b.txt"）。
/// Mtime 为 unix 秒。
///
/// 安全：顶层与递归都通过文件属性检测 reparse point（symlink/junction），一律跳过、绝不跟随
/// （junction 自环若跟随会无限递归）；递归深度上限 128 兜底保证终止。
/// 不存在的输入路径抛 <see cref="FileNotFoundException"/>。
/// </summary>
public static class FileCollector
{
    /// <summary>递归深度上限，防止环形/病态目录树导致无限递归。</summary>
    private const int MaxWalkDepth = 128;

    /// <summary>展开 paths 中每个文件/目录。目录返回 "目录条目 + 其子项" 的序列。</summary>
    public static List<FileEntry> Collect(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var result = new List<FileEntry>();
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);

            FileAttributes attrs;
            try
            {
                // File.GetAttributes 对不存在的路径抛 FileNotFoundException（FileInfo.Attributes
                // 对缺失路径返回 -1 哨兵值，会误判为 reparse point 而静默跳过）；
                // 返回的是链接自身的属性（ReparsePoint 位），不跟随 symlink/junction。
                attrs = File.GetAttributes(full);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new FileNotFoundException($"文件或目录不存在: {p}");
            }

            // symlink/junction：顶层也不跟随，直接跳过（保证不跟随任何链接）。
            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;

            if ((attrs & FileAttributes.Directory) != 0)
            {
                var baseName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                result.Add(new FileEntry(baseName, full, true, 0, UnixSeconds(LastWriteUtc(full))));
                WalkDir(full, "", 0, result);
            }
            else
            {
                var fi = new FileInfo(full);
                result.Add(new FileEntry(fi.Name, full, false, fi.Length, UnixSeconds(fi.LastWriteTimeUtc)));
            }
        }
        return result;
    }

    /// <summary>递归收集目录内容。prefix 为空时子项名不带前导 '/'，与 Rust 版相对名一致。</summary>
    private static void WalkDir(string dir, string prefix, int depth, List<FileEntry> outList)
    {
        if (depth > MaxWalkDepth) return; // 深度兜底：保证终止

        foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            // reparse point（symlink/junction）：不跟随、不收集、不递归
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            var rel = prefix.Length == 0 ? item.Name : $"{prefix}/{item.Name}";

            if ((item.Attributes & FileAttributes.Directory) != 0)
            {
                outList.Add(new FileEntry(rel, item.FullName, true, 0, UnixSeconds(item.LastWriteTimeUtc)));
                WalkDir(item.FullName, rel, depth + 1, outList);
            }
            else if (item is FileInfo f)
            {
                outList.Add(new FileEntry(rel, item.FullName, false, f.Length, UnixSeconds(f.LastWriteTimeUtc)));
            }
        }
    }

    private static DateTime LastWriteUtc(string path) => new FileInfo(path).LastWriteTimeUtc;

    private static long UnixSeconds(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
}
