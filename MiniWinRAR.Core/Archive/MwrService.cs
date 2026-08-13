using System.Text;
using MiniWinRAR.Core.Mwr;

namespace MiniWinRAR.Core.Archive;

/// <summary>
/// .mwr 归档服务：基于 <see cref="MwrWriter"/> / <see cref="MwrReader"/> 实现 <see cref="IArchiveService"/>。
/// 压缩用 <see cref="FileCollector"/> 展开路径；解压/预览一律经 <see cref="PathSafety.SafeRelativePath"/>
/// 校验，杜绝路径穿越；支持进度上报与取消。
/// </summary>
public class MwrService : IArchiveService
{
    private const long PreviewCap = 1 << 20; // 预览最多读 1 MiB

    public ArchiveStats Compress(IEnumerable<string> paths, string target, CompressionLevel level,
        string? password, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        // 预取消尽早失败，避免创建会残留的目标文件。
        ct.ThrowIfCancellationRequested();

        // 先收集并做同名检测，任何失败都在创建目标文件前抛出（不产出半成品归档）。
        var entries = FileCollector.Collect(paths);

        var seen = new HashSet<string>(StringComparer.Ordinal); // 防同名条目在归档中歧义/覆盖
        foreach (var e in entries)
        {
            if (!seen.Add(e.Name)) throw new InvalidOperationException($"duplicate entry name: {e.Name}");
        }

        long totalSize = entries.Where(e => !e.IsDir).Sum(e => e.Size);

        var targetDir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

        using var fs = File.Create(target);
        using var writer = new MwrWriter(fs, password);

        long processed = 0;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var safe = PathSafety.SafeRelativePath(e.Name);
            if (safe is null) throw new InvalidOperationException($"非法归档条目名: {e.Name}");

            if (e.IsDir)
            {
                writer.AddDir(safe, e.Mtime);
            }
            else
            {
                writer.AddFile(safe, File.ReadAllBytes(e.Path), e.Mtime, level);
                processed += e.Size;
                progress?.Report(new ProgressInfo(safe,
                    totalSize == 0 ? 100 : (int)(processed * 100 / totalSize)));
            }
        }
        writer.Finish();

        return new ArchiveStats(entries.Count, totalSize, target);
    }

    public List<ArchiveEntry> List(string archivePath, string? password)
    {
        using var fs = File.OpenRead(archivePath);
        using var reader = new MwrReader(fs, password);

        return reader.Entries.Select(e => new ArchiveEntry(
            e.Name, e.UncompressedSize, e.IsDir,
            DateTimeOffset.FromUnixTimeSeconds(e.Mtime), reader.IsEncrypted)).ToList();
    }

    public ArchiveStats Extract(string archivePath, string targetDir, string? password,
        IEnumerable<string>? filter, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // 预取消尽早失败，不创建输出目录

        using var fs = File.OpenRead(archivePath);
        using var reader = new MwrReader(fs, password); // 密码错误在此抛出，先于任何输出文件

        var filterSet = filter is null ? null : new HashSet<string>(filter, StringComparer.Ordinal);
        var entries = reader.Entries;
        long totalBytes = entries.Where(e => !e.IsDir).Sum(e => e.UncompressedSize);

        Directory.CreateDirectory(targetDir);
        long processedBytes = 0, count = 0, writtenSize = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var meta = entries[i];

            if (meta.IsDir)
            {
                var safeDir = PathSafety.SafeRelativePath(meta.Name.TrimEnd('/'));
                if (safeDir is null) continue;
                if (filterSet is not null && !filterSet.Contains(meta.Name) && !filterSet.Contains(safeDir)) continue;
                Directory.CreateDirectory(Path.Combine(targetDir, safeDir));
                continue;
            }

            var safe = PathSafety.SafeRelativePath(meta.Name);
            if (safe is null) continue;                       // 路径穿越：跳过，不写也不计
            if (filterSet is not null && !filterSet.Contains(meta.Name)) continue;

            var targetPath = Path.Combine(targetDir, safe);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllBytes(targetPath, reader.ReadFile(i));

            count++;
            writtenSize += meta.UncompressedSize;
            processedBytes += meta.UncompressedSize;
            progress?.Report(new ProgressInfo(meta.Name,
                totalBytes == 0 ? 100 : (int)(processedBytes * 100 / totalBytes)));
        }

        return new ArchiveStats(count, writtenSize, targetDir);
    }

    public PreviewResult Preview(string archivePath, string entryName, string? password)
    {
        using var fs = File.OpenRead(archivePath);
        using var reader = new MwrReader(fs, password);

        var idx = reader.Entries.FindIndex(e => e.Name == entryName);
        if (idx < 0) throw new ArchiveCorruptedException($"归档中不存在条目: {entryName}");

        var meta = reader.Entries[idx];
        if (meta.IsDir) return new PreviewResult("binary", null, null);

        var data = reader.ReadFile(idx);
        var bytes = data.AsSpan(0, (int)Math.Min(data.Length, PreviewCap)).ToArray();
        var kind = PreviewClassifier.ClassifyKind(bytes);
        return new PreviewResult(kind, kind == "text" ? Encoding.UTF8.GetString(bytes) : null, bytes);
    }
}
