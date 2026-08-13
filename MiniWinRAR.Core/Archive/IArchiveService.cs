namespace MiniWinRAR.Core.Archive;

/// <summary>
/// 统一归档接口（ZIP 与 .mwr 共用）。压缩/解压报告进度、支持取消；
/// 解压与预览必须经过 PathSafety 校验，杜绝路径穿越。
/// </summary>
public interface IArchiveService
{
    /// <summary>把 paths（文件或目录）压缩到 target。返回条目数与未压缩总大小。</summary>
    ArchiveStats Compress(IEnumerable<string> paths, string target,
        CompressionLevel level, string? password,
        IProgress<ProgressInfo> progress, CancellationToken ct);

    /// <summary>列出归档条目元信息（不解密内容；加密条目也能列出）。</summary>
    List<ArchiveEntry> List(string archivePath, string? password);

    /// <summary>解压到 targetDir。filter 为 null 或包含要解压的条目名；穿越条目直接跳过。</summary>
    ArchiveStats Extract(string archivePath, string targetDir, string? password,
        IEnumerable<string>? filter, IProgress<ProgressInfo> progress, CancellationToken ct);

    /// <summary>预览单个条目内容：分类为 text / image / binary，返回文本或原始字节。</summary>
    PreviewResult Preview(string archivePath, string entryName, string? password);
}
