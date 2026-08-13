using System.Text.Json;

namespace MiniWinRAR.Core.Mwr;

/// <summary>
/// .mwr 归档格式定义：固定头常量、条目元数据、Header JSON 序列化。
/// 二进制布局与 Rust 版 spec 完全一致（Magic "MWR1" + Version 1 + Flags + Salt 16B = 22B 固定头）。
/// </summary>
public static class MwrFormat
{
    /// <summary>魔数 "MWR1"（4 字节）。C# 无 const 数组，用 ReadOnlySpan 提供同一字节序列。</summary>
    public static ReadOnlySpan<byte> Magic => "MWR1"u8;

    /// <summary>格式版本。</summary>
    public const byte Version = 1;

    /// <summary>Flags bit0：归档整体加密（Header 与条目均加密）。</summary>
    public const byte FlagEncrypted = 0x01;

    /// <summary>固定头长度 = Magic(4) + Version(1) + Flags(1) + Salt(16) = 22 字节。</summary>
    public const long FixedHeaderLen = 22;

    private static readonly JsonSerializerOptions Options = new()
    {
        // EntryMeta 按计划用公共字段定义；System.Text.Json 默认只序列化属性，需显式包含字段。
        IncludeFields = true,
    };

    /// <summary>将条目元数据表序列化为 UTF-8 JSON。</summary>
    public static byte[] Serialize(List<EntryMeta> entries)
        => JsonSerializer.SerializeToUtf8Bytes(entries, Options);

    /// <summary>
    /// 从 UTF-8 JSON 反序列化条目元数据表。未加密归档的 header 是攻击者可控的，
    /// 可能含 null 元素；过滤掉，避免读取时 NullReferenceException。
    /// </summary>
    public static List<EntryMeta> Deserialize(byte[] bytes)
    {
        var entries = JsonSerializer.Deserialize<List<EntryMeta>>(bytes, Options) ?? new List<EntryMeta>();
        entries.RemoveAll(e => e is null);
        return entries;
    }
}

/// <summary>单个归档条目的元数据（与 Rust 版 EntryMeta 字段一一对应）。</summary>
public class EntryMeta
{
    public string Name = "";                    // 相对路径，UTF-8，/ 分隔
    public long UncompressedSize;
    public long CompressedSize;
    public long Mtime;                          // unix 时间戳
    public bool IsDir;
    public long DataOffset;                     // 数据在归档中的偏移
    public byte[] Nonce = Array.Empty<byte>();  // 该条目的 GCM nonce（12 字节）
    public uint Crc32;                          // 解压后完整性校验
}
