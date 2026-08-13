using MiniWinRAR.Core.Crypto;
using ZstdSharp;

namespace MiniWinRAR.Core.Mwr;

/// <summary>
/// 压缩级别。与 Rust 版 <c>CompressionLevel</c> 一一对应：
/// Store→0 / Fast→3 / Best→19（zstd level）。Task 6 会将其合并进 ArchiveModels，此处保持同名三变体。
/// </summary>
public enum CompressionLevel { Store, Fast, Best }

/// <summary>
/// 写入 .mwr 归档。布局与 Rust 版一致：
///   固定头 22B = Magic(4) + Version(1) + Flags(1) + Salt(16)
///   条目数据：加密 = [nonce(12)][ciphertext||tag(16)]；非加密 = [zstd 压缩体]
///   末尾 header 区 = [nonce(12)（加密时）][header JSON 密文/明文] + headerLen(u64 LE)
/// </summary>
public class MwrWriter : IDisposable
{
    private readonly Stream _output;
    private readonly byte[]? _key;
    private readonly List<EntryMeta> _entries = new();
    private long _offset;
    private bool _finished;

    /// <summary>写入固定头（magic + version + flags + salt），密码为空则不加密。</summary>
    public MwrWriter(Stream output, string? password)
    {
        _output = output;
        var salt = CryptoService.RandomBytes(CryptoService.SaltLen);
        _key = password is null ? null : CryptoService.DeriveKey(password, salt);

        var flags = _key is null ? (byte)0 : MwrFormat.FlagEncrypted;
        var header = new byte[MwrFormat.FixedHeaderLen];
        MwrFormat.Magic.CopyTo(header);
        header[4] = MwrFormat.Version;
        header[5] = flags;
        salt.CopyTo(header, 6);
        _output.Write(header, 0, header.Length);
        _offset = MwrFormat.FixedHeaderLen;
    }

    /// <summary>把用户压缩级别映射到 zstd level（沿用归档 spec）。</summary>
    public static int ZstdLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Store => 0,
        CompressionLevel.Fast => 3,
        CompressionLevel.Best => 19,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>压缩 → （可选）加密 → 写入条目数据，并记录元数据。</summary>
    public void AddFile(string name, byte[] data, long mtime, CompressionLevel level)
    {
        EnsureNotFinished();

        var compressed = Zstd.Compress(data, ZstdLevel(level));
        var crc32 = Crc32.Compute(data);

        var start = _offset;
        byte[] nonce, payload;
        if (_key is not null)
        {
            nonce = CryptoService.RandomBytes(CryptoService.NonceLen);
            payload = CryptoService.Encrypt(_key, nonce, compressed);
            Write(nonce);
        }
        else
        {
            nonce = Array.Empty<byte>();
            payload = compressed;
        }
        Write(payload);

        _entries.Add(new EntryMeta
        {
            Name = name,
            UncompressedSize = data.Length,
            CompressedSize = payload.Length, // 加密条目含 16B tag
            Mtime = mtime,
            IsDir = false,
            DataOffset = start,              // 条目起始（加密时指向 nonce）
            Nonce = nonce,
            Crc32 = crc32,                   // 原始（未压缩）数据 CRC32
        });
    }

    /// <summary>记录目录条目（无数据），data_offset 指向当前数据位置。</summary>
    public void AddDir(string name, long mtime)
    {
        EnsureNotFinished();
        _entries.Add(new EntryMeta
        {
            Name = name,
            UncompressedSize = 0,
            CompressedSize = 0,
            Mtime = mtime,
            IsDir = true,
            DataOffset = _offset,
            Nonce = Array.Empty<byte>(),
            Crc32 = 0,
        });
    }

    /// <summary>序列化条目元数据表，加密（可选）后写入末尾 header 区 + 8 字节 headerLen(u64 LE)。</summary>
    public void Finish()
    {
        EnsureNotFinished();
        _finished = true;

        var headerPlain = MwrFormat.Serialize(_entries);
        if (_key is not null)
        {
            var nonce = CryptoService.RandomBytes(CryptoService.NonceLen);
            var ciphertext = CryptoService.Encrypt(_key, nonce, headerPlain);
            Write(nonce);
            Write(ciphertext);
            WriteUInt64Le((ulong)(CryptoService.NonceLen + ciphertext.Length));
        }
        else
        {
            Write(headerPlain);
            WriteUInt64Le((ulong)headerPlain.Length);
        }
    }

    /// <summary>若未调用 Finish 则补写末尾 header（避免遗漏时产出截断归档）；不接管 output 流的所有权。</summary>
    public void Dispose()
    {
        if (!_finished) Finish();
    }

    private void EnsureNotFinished()
    {
        if (_finished) throw new InvalidOperationException("归档已 finish，不能再写入或重复 finish。");
    }

    private void Write(byte[] bytes)
    {
        _output.Write(bytes, 0, bytes.Length);
        _offset += bytes.Length;
    }

    private void WriteUInt64Le(ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        _output.Write(buf);
        _offset += 8;
    }
}
