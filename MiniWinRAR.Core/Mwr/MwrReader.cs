using System.Buffers.Binary;
using System.Text.Json;
using MiniWinRAR.Core.Archive;
using MiniWinRAR.Core.Crypto;
using ZstdSharp;

namespace MiniWinRAR.Core.Mwr;

/// <summary>
/// 读取 .mwr 归档。布局与 MwrWriter 一致：
///   固定头 22B = Magic(4) + Version(1) + Flags(1) + Salt(16)
///   条目数据：加密 = [nonce(12)][ciphertext||tag(16)]；非加密 = [zstd 压缩体]
///   末尾 header 区 = [nonce(12)（加密时）][header JSON] + headerLen(u64 LE)
///
/// 安全说明：未加密归档的 header JSON 是攻击者可控的，因此构造时对 headerLen 做边界检查，
/// ReadFile 对每个条目的 data_offset / compressed_size / uncompressed_size 做边界检查，
/// 任何越界都抛 <see cref="ArchiveCorruptedException"/>，绝不从不可信大小直接分配内存。
/// </summary>
public class MwrReader : IDisposable
{
    /// <summary>单条目解压后大小的硬上限（1 GiB），防止 header 声称超大尺寸触发巨大分配。</summary>
    public const long MaxUncompressedSize = 1L << 30;

    private readonly Stream _stream;
    private readonly byte[]? _key;
    private bool _disposed;

    /// <summary>归档内条目元数据表（header 解析后可用）。</summary>
    public List<EntryMeta> Entries { get; }

    /// <summary>归档是否整体加密（Flags bit0）。</summary>
    public bool IsEncrypted { get; }

    /// <summary>
    /// 解析固定头 + 末尾 header。密码错误抛 <see cref="InvalidPasswordException"/>，
    /// header_len 越界 / 结构非法抛 <see cref="ArchiveCorruptedException"/>。
    /// </summary>
    public MwrReader(Stream input, string? password)
    {
        _stream = input ?? throw new ArgumentNullException(nameof(input));
        if (!input.CanRead || !input.CanSeek)
            throw new ArgumentException("输入流必须支持读取与定位。", nameof(input));

        if (input.Length < 8)
            throw new ArchiveCorruptedException("归档过短，缺少末尾长度字段。");
        if (input.Length < MwrFormat.FixedHeaderLen)
            throw new ArchiveCorruptedException("归档过短，无法读取固定头。");

        // 1. 固定头
        input.Position = 0;
        var fixedHeader = new byte[MwrFormat.FixedHeaderLen];
        input.ReadExactly(fixedHeader);
        if (!fixedHeader.AsSpan(0, 4).SequenceEqual(MwrFormat.Magic))
            throw new ArchiveCorruptedException("不是 .mwr 归档（魔数不匹配）。");
        if (fixedHeader[4] != MwrFormat.Version)
            throw new ArchiveCorruptedException($"不支持的归档版本：{fixedHeader[4]}。");

        IsEncrypted = (fixedHeader[5] & MwrFormat.FlagEncrypted) != 0;

        // 2. 末尾 8B headerLen(u64 LE)——攻击者可控，先做全部边界检查再分配。
        var lengthFieldPos = input.Length - 8;
        input.Position = lengthFieldPos;
        Span<byte> lenBuf = stackalloc byte[8];
        input.ReadExactly(lenBuf);
        var headerLen = BinaryPrimitives.ReadUInt64LittleEndian(lenBuf);

        if (headerLen < 1)
            throw new ArchiveCorruptedException("末尾 header 长度无效。");
        if (headerLen > (ulong)(input.Length - 8))
            throw new ArchiveCorruptedException("末尾 header 长度越界。");
        if (IsEncrypted && headerLen < (ulong)(CryptoService.NonceLen + 1))
            throw new ArchiveCorruptedException("加密归档的 header 长度无效。");
        if (headerLen > int.MaxValue)
            throw new ArchiveCorruptedException("末尾 header 长度过大。");

        // 3. 读取 header 块（加密时含 12B nonce 前缀）。
        var headerStart = lengthFieldPos - (long)headerLen;
        var headerBlock = new byte[(int)headerLen];
        input.Position = headerStart;
        input.ReadExactly(headerBlock);

        byte[] headerPlain;
        if (IsEncrypted)
        {
            if (password is null)
                throw new InvalidPasswordException();
            var salt = fixedHeader.AsSpan(6).ToArray();
            _key = CryptoService.DeriveKey(password, salt);
            var nonce = headerBlock[..CryptoService.NonceLen];
            var ciphertext = headerBlock[CryptoService.NonceLen..];
            // 解密失败（GCM tag 不匹配）→ InvalidPasswordException
            headerPlain = CryptoService.Decrypt(_key, nonce, ciphertext);
        }
        else
        {
            headerPlain = headerBlock;
        }

        try
        {
            Entries = MwrFormat.Deserialize(headerPlain);
        }
        catch (JsonException e)
        {
            throw new ArchiveCorruptedException("归档 header 无法解析。", e);
        }
    }

    /// <summary>
    /// 读取第 <paramref name="index"/> 个条目的原始数据：解密（如有）→ 解压 → CRC 校验。
    /// 数据越界 / 解压失败 / CRC 不匹配均抛 <see cref="ArchiveCorruptedException"/>。
    /// </summary>
    public byte[] ReadFile(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var meta = Entries[index];
        if (meta.IsDir)
            throw new InvalidOperationException($"目录条目没有数据：{meta.Name}");

        var dataOffset = meta.DataOffset;
        var compressedSize = meta.CompressedSize;
        var uncompressedSize = meta.UncompressedSize;

        // 边界检查：未加密时条目元数据来自攻击者可控的 header，必须全部校验。
        if (dataOffset < MwrFormat.FixedHeaderLen)
            throw new ArchiveCorruptedException("条目数据偏移无效。");
        if (compressedSize < 0 || uncompressedSize < 0)
            throw new ArchiveCorruptedException("条目大小为负。");
        if (uncompressedSize > MaxUncompressedSize)
            throw new ArchiveCorruptedException("条目解压后大小超过上限（1 GiB）。");
        if (dataOffset > _stream.Length)
            throw new ArchiveCorruptedException("条目数据偏移越界。");
        if (compressedSize > int.MaxValue)
            throw new ArchiveCorruptedException("条目压缩数据过大。");

        if (IsEncrypted)
        {
            // 加密条目 = [nonce(12)][ciphertext||tag(16)]，CompressedSize 不含 nonce。
            if (dataOffset > _stream.Length - CryptoService.NonceLen)
                throw new ArchiveCorruptedException("条目数据偏移越界。");
            if (compressedSize > _stream.Length - dataOffset - CryptoService.NonceLen)
                throw new ArchiveCorruptedException("条目数据长度越界。");

            _stream.Position = dataOffset;
            var nonce = new byte[CryptoService.NonceLen];
            _stream.ReadExactly(nonce);
            var payload = new byte[(int)compressedSize];
            _stream.ReadExactly(payload);

            var compressed = CryptoService.Decrypt(_key!, nonce, payload);
            return VerifyCrc(meta, Decompress(compressed, uncompressedSize));
        }

        if (compressedSize > _stream.Length - dataOffset)
            throw new ArchiveCorruptedException("条目数据长度越界。");

        _stream.Position = dataOffset;
        var blob = new byte[(int)compressedSize];
        _stream.ReadExactly(blob);
        return VerifyCrc(meta, Decompress(blob, uncompressedSize));
    }

    /// <summary>不释放输入流：调用方拥有它（与 MwrWriter 不拥有输出流对称）。</summary>
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>解压 zstd 数据。空文件跳过解压（ZstdSharp 拒绝 sizeBound &lt;= 0）。</summary>
    private static byte[] Decompress(byte[] compressed, long uncompressedSize)
    {
        if (uncompressedSize == 0)
            return Array.Empty<byte>();
        try
        {
            return Zstd.Decompress(compressed, (int)uncompressedSize);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            throw new ArchiveCorruptedException("条目数据解压失败。", e);
        }
    }

    private static byte[] VerifyCrc(EntryMeta meta, byte[] data)
    {
        if (Crc32.Compute(data) != meta.Crc32)
            throw new ArchiveCorruptedException($"条目 CRC32 校验失败：{meta.Name}");
        return data;
    }
}
