using MiniWinRAR.Core.Mwr;

namespace MiniWinRAR.Tests;

public class MwrFormatTests
{
    [Fact]
    public void SerializeDeserialize_Roundtrip_WithChineseFilename()
    {
        var entries = new List<EntryMeta>
        {
            new EntryMeta
            {
                Name = "a/中文.txt",
                UncompressedSize = 12345,
                CompressedSize = 4567,
                Mtime = 1700000000,
                IsDir = false,
                DataOffset = 22,
                Nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
                Crc32 = 0xDEADBEEF,
            },
            new EntryMeta
            {
                Name = "dir",
                UncompressedSize = 0,
                CompressedSize = 0,
                Mtime = 1700000001,
                IsDir = true,
                DataOffset = 0,
                Nonce = Array.Empty<byte>(),
                Crc32 = 0,
            },
        };

        var bytes = MwrFormat.Serialize(entries);
        var roundtrip = MwrFormat.Deserialize(bytes);

        Assert.Equal(2, roundtrip.Count);
        Assert.Equal("a/中文.txt", roundtrip[0].Name);
        Assert.Equal(12345L, roundtrip[0].UncompressedSize);
        Assert.Equal(4567L, roundtrip[0].CompressedSize);
        Assert.Equal(1700000000L, roundtrip[0].Mtime);
        Assert.False(roundtrip[0].IsDir);
        Assert.Equal(22L, roundtrip[0].DataOffset);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, roundtrip[0].Nonce);
        Assert.Equal(0xDEADBEEFu, roundtrip[0].Crc32);
        Assert.True(roundtrip[1].IsDir);
        Assert.Equal("dir", roundtrip[1].Name);
    }

    [Fact]
    public void Constants_AreStable()
    {
        Assert.True(MwrFormat.Magic.SequenceEqual("MWR1"u8));
        Assert.Equal((byte)1, MwrFormat.Version);
        Assert.Equal((byte)0x01, MwrFormat.FlagEncrypted);
        Assert.Equal(22L, MwrFormat.FixedHeaderLen);
    }
}
