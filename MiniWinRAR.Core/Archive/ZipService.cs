using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace MiniWinRAR.Core.Archive;

/// <summary>
/// ZIP 归档服务（基于 SharpZipLib）。支持 Store/Deflate 与 AES-256 加密。
/// 统一使用 SharpZipLib 读写（内置 System.IO.Compression 无 AES，SharpZipLib 两者皆可，
/// 单一代码路径避免两套读写行为分叉）。解压路径一律经 <see cref="PathSafety.SafeRelativePath"/>
/// 校验，杜绝 zip-slip。
/// </summary>
public class ZipService : IArchiveService
{
    private const long PreviewCap = 1 << 20; // 预览最多读 1 MiB

    public ArchiveStats Compress(IEnumerable<string> paths, string target, CompressionLevel level,
        string? password, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        // 收集（源绝对路径 → 归档内 '/' 相对名），并汇总未压缩总大小作为进度分母。
        var files = new List<(string FullPath, string EntryName)>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // 防同名条目在 zip 中静默覆盖
        long totalSize = 0;
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            if (Directory.Exists(full))
            {
                var rootLen = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = f[rootLen..].Replace('\\', '/');
                    if (!seen.Add(rel)) throw new InvalidOperationException($"duplicate entry name: {rel}");
                    files.Add((f, rel));
                    totalSize += new FileInfo(f).Length;
                }
            }
            else if (File.Exists(full))
            {
                var name = Path.GetFileName(full);
                if (!seen.Add(name)) throw new InvalidOperationException($"duplicate entry name: {name}");
                files.Add((full, name));
                totalSize += new FileInfo(full).Length;
            }
            else
            {
                throw new FileNotFoundException($"文件或目录不存在: {p}");
            }
        }

        var targetDir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

        using var fs = File.Create(target);
        using var zip = new ZipOutputStream(fs);
        if (password is not null) zip.Password = password;

        switch (level)
        {
            case CompressionLevel.Fast: zip.SetLevel(1); break;
            case CompressionLevel.Best: zip.SetLevel(9); break;
            // Store：每条目显式 CompressionMethod.Stored
        }

        long processed = 0;
        foreach (var (fullPath, entryName) in files)
        {
            ct.ThrowIfCancellationRequested();
            var safe = PathSafety.SafeRelativePath(entryName);
            if (safe is null) throw new InvalidOperationException($"非法归档条目名: {entryName}");

            var fi = new FileInfo(fullPath);
            var entry = new ZipEntry(safe) { DateTime = fi.LastWriteTime, Size = fi.Length };
            if (level == CompressionLevel.Store) entry.CompressionMethod = CompressionMethod.Stored;
            if (password is not null) entry.AESKeySize = 256; // AES-256

            zip.PutNextEntry(entry);
            using (var input = File.OpenRead(fullPath))
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.Write(buf, 0, n);
                }
            }
            zip.CloseEntry();

            processed += fi.Length;
            progress?.Report(new ProgressInfo(safe,
                totalSize == 0 ? 100 : (int)(processed * 100 / totalSize)));
        }

        return new ArchiveStats(files.Count, totalSize, target);
    }

    public List<ArchiveEntry> List(string archivePath, string? password)
    {
        using var fs = File.OpenRead(archivePath);
        using var zip = new ZipFile(fs);
        if (password is not null) zip.Password = password;

        var result = new List<ArchiveEntry>();
        foreach (ZipEntry e in zip)
        {
            result.Add(new ArchiveEntry(
                e.Name, e.Size, e.IsDirectory, new DateTimeOffset(e.DateTime),
                e.IsCrypted || e.AESKeySize > 0));
        }
        return result;
    }

    public ArchiveStats Extract(string archivePath, string targetDir, string? password,
        IEnumerable<string>? filter, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        using var fs = File.OpenRead(archivePath);
        using var zip = new ZipFile(fs);
        if (password is not null) zip.Password = password;

        var filterSet = filter is null ? null : new HashSet<string>(filter, StringComparer.Ordinal);
        var entries = zip.Cast<ZipEntry>().ToList();          // 先枚举，拿进度分母
        long totalBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

        Directory.CreateDirectory(targetDir);
        long processedBytes = 0, count = 0, writtenSize = 0;

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (e.IsDirectory)
            {
                var safeDir = PathSafety.SafeRelativePath(e.Name.TrimEnd('/'));
                if (safeDir is null) continue;
                if (filterSet is not null && !filterSet.Contains(e.Name) && !filterSet.Contains(safeDir)) continue;
                Directory.CreateDirectory(Path.Combine(targetDir, safeDir));
                continue;
            }

            var safe = PathSafety.SafeRelativePath(e.Name);
            if (safe is null) continue;                       // zip-slip：跳过，不写也不计
            if (filterSet is not null && !filterSet.Contains(e.Name)) continue;

            var targetPath = Path.Combine(targetDir, safe);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using (var src = zip.GetInputStream(e))
            using (var dst = File.Create(targetPath))
            {
                src.CopyTo(dst);
            }

            count++;
            writtenSize += e.Size;
            processedBytes += e.Size;
            progress?.Report(new ProgressInfo(e.Name,
                totalBytes == 0 ? 100 : (int)(processedBytes * 100 / totalBytes)));
        }

        return new ArchiveStats(count, writtenSize, targetDir);
    }

    public PreviewResult Preview(string archivePath, string entryName, string? password)
    {
        using var fs = File.OpenRead(archivePath);
        using var zip = new ZipFile(fs);
        if (password is not null) zip.Password = password;

        var entry = zip.GetEntry(entryName);
        if (entry is null) throw new ArchiveCorruptedException($"归档中不存在条目: {entryName}");
        if (entry.IsDirectory) return new PreviewResult("binary", null, null);

        var length = (int)Math.Min(entry.Size, PreviewCap);
        var bytes = new byte[length];
        using (var src = zip.GetInputStream(entry))
        {
            int read = 0;
            while (read < length)
            {
                int n = src.Read(bytes, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < length) Array.Resize(ref bytes, read);
        }

        var kind = ClassifyKind(bytes);
        return new PreviewResult(kind, kind == "text" ? Encoding.UTF8.GetString(bytes) : null, bytes);
    }

    private static string ClassifyKind(byte[] bytes)
    {
        if (IsImage(bytes)) return "image";
        if (LooksLikeText(bytes)) return "text";
        return "binary";
    }

    private static bool IsImage(byte[] b)
    {
        if (b.Length >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return true;
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;                   // JPEG
        if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return true;  // GIF
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return true;                                   // BMP
        return false;
    }

    /// <summary>启发式文本判定：无 NUL 且整体是合法 UTF-8（覆盖中文等多字节文本）。</summary>
    private static bool LooksLikeText(byte[] b)
    {
        if (b.Length == 0) return false;
        if (b.AsSpan().IndexOf((byte)0) >= 0) return false;
        return IsValidUtf8(b);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> b)
    {
        int i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            if (c < 0x80) { i++; continue; }
            int needed;
            if ((c & 0xE0) == 0xC0) needed = 1;
            else if ((c & 0xF0) == 0xE0) needed = 2;
            else if ((c & 0xF8) == 0xF0) needed = 3;
            else return false;
            if (i + needed >= b.Length) return false;
            for (int k = 1; k <= needed; k++)
                if ((b[i + k] & 0xC0) != 0x80) return false;
            i += needed + 1;
        }
        return true;
    }
}
