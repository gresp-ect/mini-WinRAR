namespace MiniWinRAR.Core.Archive;

/// <summary>
/// 归档文件损坏 / 结构非法 / 字段越界时抛出。.mwr header 在未加密时是攻击者可控的，
/// 所有从 header 派生的长度与偏移在使用前都必须做边界检查，失败统一抛此异常。
/// </summary>
public class ArchiveCorruptedException : Exception
{
    public ArchiveCorruptedException() : base("归档文件已损坏。") { }

    public ArchiveCorruptedException(string message) : base(message) { }

    public ArchiveCorruptedException(string message, Exception innerException) : base(message, innerException) { }
}
