using System.Buffers.Binary;
using MiniWinRAR.Core.Archive;
using MiniWinRAR.Core.Crypto;
using MiniWinRAR.Core.Mwr;
using ZstdSharp;

namespace MiniWinRAR.Tests;

public class MwrReaderTests
{
    // ---------- 工具方法 ----------

    /// <summary>用 MwrWriter（Task 4 真实输出）写一个归档并回到起始位置。</summary>
    private static MemoryStream WriteArchive(string? password, Action<MwrWriter> populate)
    {
        var ms = new MemoryStream();
        using (var w = new MwrWriter(ms, password))
        {
            populate(w);
            w.Finish();
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>构造 22B 固定头（magic + version + flags + 全零 salt），用于手工构造损坏归档。</summary>
    private static byte[] FixedHeader(byte flags)
    {
        var h = new byte[MwrFormat.FixedHeaderLen];
        MwrFormat.Magic.CopyTo(h);
        h[4] = MwrFormat.Version;
        h[5] = flags;
        return h;
    }

    private static byte[] Concat(params byte[][] arrays) => arrays.SelectMany(a => a).ToArray();

    private static byte[] UInt64Le(ulong value)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        return buf;
    }

    // ---------- Round-trip（非加密） ----------

    [Fact]
    public void RoundTrip_Unencrypted_EntriesAndDataMatch()
    {
        var data0 = "first file 中文"u8.ToArray();
        var data1 = "second file payload 第二"u8.ToArray();
        using var ms = WriteArchive(null, w =>
        {
            w.AddDir("folder", 1700000001);
            w.AddFile("a.txt", data0, 1700000000, CompressionLevel.Fast);
            w.AddFile("folder/b.txt", data1, 1700000002, CompressionLevel.Best);
        });

        using var reader = new MwrReader(ms, null);
        Assert.False(reader.IsEncrypted);
        Assert.Equal(3, reader.Entries.Count);

        Assert.Equal("folder", reader.Entries[0].Name);
        Assert.True(reader.Entries[0].IsDir);
        Assert.Equal("a.txt", reader.Entries[1].Name);
        Assert.Equal(data0.Length, reader.Entries[1].UncompressedSize);
        Assert.Equal("folder/b.txt", reader.Entries[2].Name);
        Assert.Equal(data1.Length, reader.Entries[2].UncompressedSize);

        Assert.Equal(data0, reader.ReadFile(1));
        Assert.Equal(data1, reader.ReadFile(2));
        // 目录条目没有数据
        Assert.Throws<InvalidOperationException>(() => reader.ReadFile(0));
    }

    [Fact]
    public void RoundTrip_EmptyFile_ReturnsEmptyArray()
    {
        using var ms = WriteArchive(null, w => w.AddFile("empty.txt", Array.Empty<byte>(), 0, CompressionLevel.Fast));
        using var reader = new MwrReader(ms, null);
        Assert.Empty(reader.ReadFile(0));
    }

    // ---------- Round-trip（加密） ----------

    [Fact]
    public void RoundTrip_Encrypted_EntriesAndDataMatch()
    {
        var data = "sensitive payload 机密内容"u8.ToArray();
        using var ms = WriteArchive("p@ss", w => w.AddFile("secret.bin", data, 1700000000, CompressionLevel.Best));

        using var reader = new MwrReader(ms, "p@ss");
        Assert.True(reader.IsEncrypted);
        var entry = Assert.Single(reader.Entries);
        Assert.Equal("secret.bin", entry.Name);
        Assert.Equal(data.Length, entry.UncompressedSize);
        Assert.Equal(data, reader.ReadFile(0));
    }

    // ---------- 密码错误 ----------

    [Fact]
    public void WrongPassword_ThrowsInvalidPasswordException()
    {
        using var ms = WriteArchive("correct", w => w.AddFile("s.txt", "top secret"u8.ToArray(), 0, CompressionLevel.Fast));
        Assert.Throws<InvalidPasswordException>(() => new MwrReader(ms, "wrong"));
    }

    [Fact]
    public void EncryptedArchive_WithoutPassword_ThrowsInvalidPasswordException()
    {
        using var ms = WriteArchive("correct", w => w.AddFile("s.txt", "x"u8.ToArray(), 0, CompressionLevel.Fast));
        Assert.Throws<InvalidPasswordException>(() => new MwrReader(ms, null));
    }

    // ---------- 损坏 / 截断归档（headerLen 越界） ----------

    [Fact]
    public void OversizedHeaderLen_ThrowsArchiveCorruptedException()
    {
        // 有效魔数 + 固定头 + 垃圾数据，但末尾 headerLen 远超文件剩余长度
        var archive = Concat(FixedHeader(0), "garbage payload"u8.ToArray(), UInt64Le(ulong.MaxValue));
        using var ms = new MemoryStream(archive);
        Assert.Throws<ArchiveCorruptedException>(() => new MwrReader(ms, null));
    }

    [Fact]
    public void ZeroHeaderLen_ThrowsArchiveCorruptedException()
    {
        var archive = Concat(FixedHeader(0), UInt64Le(0));
        using var ms = new MemoryStream(archive);
        Assert.Throws<ArchiveCorruptedException>(() => new MwrReader(ms, null));
    }

    [Fact]
    public void TruncatedArchive_TooShort_ThrowsArchiveCorruptedException()
    {
        // 只有 22B 固定头，缺末尾 header 区
        using var ms = new MemoryStream(FixedHeader(0));
        Assert.Throws<ArchiveCorruptedException>(() => new MwrReader(ms, null));
    }

    [Fact]
    public void EncryptedHeaderTooShort_ThrowsArchiveCorruptedException()
    {
        // 加密标志 + 合法固定头，但 headerLen(5) < nonce(12)+1
        var archive = Concat(FixedHeader(MwrFormat.FlagEncrypted), "some data"u8.ToArray(), UInt64Le(5));
        using var ms = new MemoryStream(archive);
        Assert.Throws<ArchiveCorruptedException>(() => new MwrReader(ms, "anypass"));
    }

    [Fact]
    public void EncryptedHeaderLen13To27_ThrowsArchiveCorruptedException_NotArgumentOutOfRange()
    {
        // headerLen ∈ [13, 27]：> nonce(12) 但 < nonce+tag(28)。若不拦截，密文 1..15B 会在
        // Decrypt 的 ciphertext[..负值] 处抛 ArgumentOutOfRangeException（捕不到）。
        var archive = Concat(FixedHeader(MwrFormat.FlagEncrypted), UInt64Le(13));
        using var ms = new MemoryStream(archive);
        var ex = Record.Exception(() => new MwrReader(ms, "anypass"));
        Assert.IsType<ArchiveCorruptedException>(ex);
        Assert.IsNotType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public void NullEntryInHeader_IsFiltered_NoNullReference()
    {
        var entries = new List<EntryMeta>
        {
            null!, // 攻击者构造的 null 元素
            new() { Name = "a.txt", UncompressedSize = 0, CompressedSize = 0, Mtime = 0, IsDir = false, DataOffset = 22, Nonce = Array.Empty<byte>(), Crc32 = 0 },
        };
        var header = MwrFormat.Serialize(entries);
        var archive = Concat(FixedHeader(0), header, UInt64Le((ulong)header.Length));
        using var ms = new MemoryStream(archive);
        using var reader = new MwrReader(ms, null);
        var entry = Assert.Single(reader.Entries);
        Assert.Equal("a.txt", entry.Name);
        Assert.Empty(reader.ReadFile(0));
    }

    [Fact]
    public void BadMagic_ThrowsArchiveCorruptedException()
    {
        var archive = Concat("XXXX"u8.ToArray(), FixedHeader(0).AsSpan(4).ToArray(), UInt64Le(4));
        using var ms = new MemoryStream(archive);
        Assert.Throws<ArchiveCorruptedException>(() => new MwrReader(ms, null));
    }

    // ---------- 条目级边界检查（攻击者可控 header） ----------

    [Fact]
    public void EntryDataOffsetBeyondStream_ThrowsArchiveCorruptedException()
    {
        var entries = new List<EntryMeta>
        {
            new() { Name = "x.bin", UncompressedSize = 10, CompressedSize = 10, Mtime = 0, IsDir = false, DataOffset = 99_999, Nonce = Array.Empty<byte>(), Crc32 = 0 },
        };
        var header = MwrFormat.Serialize(entries);
        var archive = Concat(FixedHeader(0), header, UInt64Le((ulong)header.Length));
        using var ms = new MemoryStream(archive);
        using var reader = new MwrReader(ms, null);
        Assert.Throws<ArchiveCorruptedException>(() => reader.ReadFile(0));
    }

    [Fact]
    public void EntryUncompressedSizeOver1GiB_ThrowsArchiveCorruptedException()
    {
        var entries = new List<EntryMeta>
        {
            new() { Name = "big.bin", UncompressedSize = (1L << 30) + 1, CompressedSize = 0, Mtime = 0, IsDir = false, DataOffset = 22, Nonce = Array.Empty<byte>(), Crc32 = 0 },
        };
        var header = MwrFormat.Serialize(entries);
        var archive = Concat(FixedHeader(0), header, UInt64Le((ulong)header.Length));
        using var ms = new MemoryStream(archive);
        using var reader = new MwrReader(ms, null);
        Assert.Throws<ArchiveCorruptedException>(() => reader.ReadFile(0));
    }

    [Fact]
    public void EntryNegativeSize_ThrowsArchiveCorruptedException()
    {
        var entries = new List<EntryMeta>
        {
            new() { Name = "neg.bin", UncompressedSize = -1, CompressedSize = 0, Mtime = 0, IsDir = false, DataOffset = 22, Nonce = Array.Empty<byte>(), Crc32 = 0 },
        };
        var header = MwrFormat.Serialize(entries);
        var archive = Concat(FixedHeader(0), header, UInt64Le((ulong)header.Length));
        using var ms = new MemoryStream(archive);
        using var reader = new MwrReader(ms, null);
        Assert.Throws<ArchiveCorruptedException>(() => reader.ReadFile(0));
    }

    // ---------- CRC 校验 ----------

    [Fact]
    public void CorruptCrc_ThrowsArchiveCorruptedException()
    {
        var data = "hello world"u8.ToArray();
        var compressed = Zstd.Compress(data, 3);
        var entries = new List<EntryMeta>
        {
            // Crc32 故意写错：数据能解压，但完整性校验不过
            new() { Name = "x.bin", UncompressedSize = data.Length, CompressedSize = compressed.Length, Mtime = 0, IsDir = false, DataOffset = 22, Nonce = Array.Empty<byte>(), Crc32 = 12_345 },
        };
        var header = MwrFormat.Serialize(entries);
        var archive = Concat(FixedHeader(0), compressed, header, UInt64Le((ulong)header.Length));
        using var ms = new MemoryStream(archive);
        using var reader = new MwrReader(ms, null);
        Assert.Throws<ArchiveCorruptedException>(() => reader.ReadFile(0));
    }
}
