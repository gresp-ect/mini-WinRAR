using System.Text;
using MiniWinRAR.Core.Archive;
using MiniWinRAR.Core.Crypto;
using MiniWinRAR.Core.Mwr;
using ZstdSharp;

namespace MiniWinRAR.Tests;

public class MwrWriterTests
{
    [Theory]
    [InlineData(CompressionLevel.Store, 0)]
    [InlineData(CompressionLevel.Fast, 3)]
    [InlineData(CompressionLevel.Best, 19)]
    public void ZstdLevel_MapsToSpec(CompressionLevel level, int expected)
        => Assert.Equal(expected, MwrWriter.ZstdLevel(level));

    [Fact]
    public void WritesFixedHeader_Unencrypted()
    {
        using var ms = new MemoryStream();
        using (var w = new MwrWriter(ms, null))
        {
            w.AddFile("a.txt", "hello"u8.ToArray(), 0, CompressionLevel.Fast);
            w.Finish();
        }

        var bytes = ms.ToArray();
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual(MwrFormat.Magic), "magic MWR1");
        Assert.Equal((byte)1, bytes[4]);   // version
        Assert.Equal((byte)0, bytes[5]);   // flags: 未加密
        Assert.Equal(22L, MwrFormat.FixedHeaderLen);
    }

    [Fact]
    public void Layout_Unencrypted_EntryFollowsHeader_Decompressible()
    {
        var data = "the quick brown fox jumps over the lazy dog 中文测试"u8.ToArray();
        using var ms = new MemoryStream();
        using (var w = new MwrWriter(ms, null))
        {
            w.AddFile("a/中文.txt", data, 1700000000, CompressionLevel.Best);
            w.Finish();
        }

        var bytes = ms.ToArray();
        var headerLen = (int)BitConverter.ToUInt64(bytes.AsSpan(bytes.Length - 8, 8));
        var headerBlock = bytes[(int)(bytes.Length - 8 - headerLen)..^8];
        var entryData = bytes[22..(int)(bytes.Length - 8 - headerLen)];

        // 末尾 header 区是明文 JSON，可反序列化
        var entries = MwrFormat.Deserialize(headerBlock);
        var entry = Assert.Single(entries);
        Assert.Equal("a/中文.txt", entry.Name);
        Assert.Equal(22L, entry.DataOffset);            // 条目起始 = 固定头之后
        Assert.Equal(data.Length, entry.UncompressedSize);
        Assert.Equal(entryData.Length, entry.CompressedSize);
        Assert.Equal(Crc32.Compute(data), entry.Crc32); // CRC32 基于原始数据
        Assert.False(entry.IsDir);

        // 非加密条目数据 = zstd 压缩体，可解回原始数据
        var decompressed = Zstd.Decompress(entryData, data.Length);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Layout_Encrypted_NoncePlusCiphertextTag()
    {
        var password = "secret";
        var data = "sensitive payload with 密码"u8.ToArray();
        using var ms = new MemoryStream();
        using (var w = new MwrWriter(ms, password))
        {
            w.AddFile("secret.txt", data, 0, CompressionLevel.Fast);
            w.Finish();
        }

        var bytes = ms.ToArray();
        Assert.NotEqual((byte)0, bytes[5] & MwrFormat.FlagEncrypted);

        var salt = bytes[6..22];
        var headerLen = (int)BitConverter.ToUInt64(bytes.AsSpan(bytes.Length - 8, 8));
        var entryEnd = bytes.Length - 8 - (int)headerLen;

        // 加密条目 = nonce(12) + ciphertext||tag(16)，DataOffset 指向 nonce
        var entryNonce = bytes[22..(22 + CryptoService.NonceLen)];
        var entryPayload = bytes[(22 + CryptoService.NonceLen)..entryEnd];
        Assert.Equal(CryptoService.NonceLen, entryNonce.Length);

        var key = CryptoService.DeriveKey(password, salt);
        var compressed = CryptoService.Decrypt(key, entryNonce, entryPayload);
        var decompressed = Zstd.Decompress(compressed, data.Length);
        Assert.Equal(data, decompressed);

        // 末尾 header 区自身也是 nonce + 密文||tag，headerLen 包含 header nonce
        Assert.True(headerLen >= CryptoService.NonceLen + CryptoService.TagLen);

        // 解密 header 区，验证 EntryMeta 与加密条目布局一致
        var headerBlock = bytes[^((int)headerLen + 8)..^8];
        var headerNonce = headerBlock[..CryptoService.NonceLen];
        var headerCipher = headerBlock[CryptoService.NonceLen..];
        var headerPlain = CryptoService.Decrypt(key, headerNonce, headerCipher);
        var entry = Assert.Single(MwrFormat.Deserialize(headerPlain));
        Assert.Equal("secret.txt", entry.Name);
        Assert.Equal(22L, entry.DataOffset);                       // 指向 nonce
        Assert.Equal(entryPayload.Length, entry.CompressedSize);   // 含 16B tag
        Assert.Equal(data.Length, entry.UncompressedSize);
        Assert.Equal(Crc32.Compute(data), entry.Crc32);
        Assert.Equal(CryptoService.NonceLen, entry.Nonce.Length);
        Assert.False(entry.IsDir);
    }

    [Fact]
    public void AddDir_RecordsMeta_NoPayload()
    {
        using var ms = new MemoryStream();
        using (var w = new MwrWriter(ms, null))
        {
            w.AddDir("folder", 1700000001);
            w.Finish();
        }

        var bytes = ms.ToArray();
        var headerLen = (int)BitConverter.ToUInt64(bytes.AsSpan(bytes.Length - 8, 8));
        var headerBlock = bytes[(int)(bytes.Length - 8 - headerLen)..^8];
        var entry = Assert.Single(MwrFormat.Deserialize(headerBlock));
        Assert.True(entry.IsDir);
        Assert.Equal("folder", entry.Name);
        Assert.Equal(0L, entry.CompressedSize);
        Assert.Equal(22L, entry.DataOffset);
        // 目录条目不写数据，归档总长 = 22 + header 区 + 8
        Assert.Equal(22L + headerBlock.Length + 8L, bytes.Length);
    }

    [Fact]
    public void Layout_MultipleEntries_DataOffsetChainAdvances()
    {
        var data0 = "first file payload 第一"u8.ToArray();
        var data1 = "second file payload 第二"u8.ToArray();
        using var ms = new MemoryStream();
        using (var w = new MwrWriter(ms, null))
        {
            w.AddFile("a.txt", data0, 0, CompressionLevel.Fast);
            w.AddFile("b.txt", data1, 0, CompressionLevel.Best);
            w.Finish();
        }

        var bytes = ms.ToArray();
        var headerLen = (int)BitConverter.ToUInt64(bytes.AsSpan(bytes.Length - 8, 8));
        var headerBlock = bytes[(int)(bytes.Length - 8 - headerLen)..^8];
        var entries = MwrFormat.Deserialize(headerBlock);

        Assert.Equal(2, entries.Count);
        Assert.Equal(22L, entries[0].DataOffset);                       // 第一个条目在固定头之后
        Assert.Equal(entries[0].CompressedSize, entries[1].DataOffset - entries[0].DataOffset);
        // 非加密条目无 nonce，payload = zstd 压缩体，故 offset 链 = 22 + N0
        Assert.Equal(22L + entries[0].CompressedSize, entries[1].DataOffset);

        // 用 entries[1].DataOffset 直接定位并解压第二个条目数据
        var entry1Data = bytes[(int)entries[1].DataOffset..(int)(bytes.Length - 8 - headerLen)];
        Assert.Equal(entries[1].CompressedSize, entry1Data.Length);
        Assert.Equal(data1, Zstd.Decompress(entry1Data, data1.Length));
    }

    [Fact]
    public void AddFile_EmptyData_ProducesValidArchive()
    {
        using var ms = new MemoryStream();
        using (var w = new MwrWriter(ms, null))
        {
            w.AddFile("empty.txt", Array.Empty<byte>(), 0, CompressionLevel.Fast);
            w.Finish();
        }

        var bytes = ms.ToArray();
        var headerLen = (int)BitConverter.ToUInt64(bytes.AsSpan(bytes.Length - 8, 8));
        var headerBlock = bytes[(int)(bytes.Length - 8 - headerLen)..^8];
        var entryData = bytes[22..(int)(bytes.Length - 8 - headerLen)];

        var entry = Assert.Single(MwrFormat.Deserialize(headerBlock));
        Assert.Equal(0L, entry.UncompressedSize);

        // ZstdSharp 的 Decompress 要求输出上界 > 0；空文件解压结果为 0 字节，上界 1 即可。
        var decompressed = Zstd.Decompress(entryData, 1);
        Assert.Empty(decompressed);
    }

    [Fact]
    public void Finish_Twice_Throws()
    {
        using var ms = new MemoryStream();
        var w = new MwrWriter(ms, null);
        w.AddFile("a.txt", "x"u8.ToArray(), 0, CompressionLevel.Store);
        w.Finish();
        Assert.Throws<InvalidOperationException>(() => w.Finish());
        w.Dispose();
    }
}
