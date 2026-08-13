namespace MiniWinRAR.Core.Archive;

/// <summary>
/// 压缩级别。与 Rust 版 <c>CompressionLevel</c> 一一对应。
/// ZIP：Store→Stored / Fast→Deflate(1) / Best→Deflate(9)；.mwr：Zstd 0/3/19。
/// </summary>
public enum CompressionLevel { Store, Fast, Best }

/// <summary>归档条目（ZIP 或 .mwr 统一视图）。Size 为未压缩大小。</summary>
public record ArchiveEntry(string Name, long Size, bool IsDir, DateTimeOffset Mtime, bool IsEncrypted);

/// <summary>压缩/解压操作结果。</summary>
public record ArchiveStats(long EntryCount, long TotalSize, string TargetPath);

/// <summary>预览结果。Kind ∈ "text"|"image"|"binary"。</summary>
public record PreviewResult(string Kind, string? Text, byte[]? Bytes);

/// <summary>进度上报：当前条目名 + 整体百分比（0-100）。</summary>
public record ProgressInfo(string Name, int Pct);
