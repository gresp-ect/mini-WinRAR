using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using MiniWinRAR.Core.Archive;

namespace MiniWinRAR.Tests;

/// <summary>
/// ZIP 归档服务测试：round-trip、AES-256 加密 round-trip、zip-slip 防护、路径安全校验。
/// </summary>
public class ZipServiceTests : IDisposable
{
    private readonly string _root;

    public ZipServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mwr-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string SrcDir => Path.Combine(_root, "src");
    private string OutDir => Path.Combine(_root, "out");
    private string ZipPath => Path.Combine(_root, "a.zip");

    [Theory]
    [InlineData(CompressionLevel.Fast)]
    [InlineData(CompressionLevel.Best)]
    [InlineData(CompressionLevel.Store)]
    public void RoundTrip_ContentPreserved(CompressionLevel level)
    {
        var src = SrcDir;
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        var textData = "hello 世界 zip round-trip"u8.ToArray();
        var binData = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var mtime = new DateTimeOffset(2024, 5, 1, 12, 30, 45, TimeSpan.Zero);
        File.WriteAllBytes(Path.Combine(src, "a.txt"), textData);
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), binData);
        File.SetLastWriteTime(Path.Combine(src, "a.txt"), mtime.LocalDateTime);

        var svc = new ZipService();
        var stats = svc.Compress(new[] { src }, ZipPath, level, null, null!, CancellationToken.None);
        Assert.Equal(2, stats.EntryCount);
        Assert.Equal(textData.Length + binData.Length, stats.TotalSize);
        Assert.True(File.Exists(ZipPath), "zip 文件已生成");

        var entries = svc.List(ZipPath, null);
        Assert.Equal(2, entries.Count);
        var a = entries.Single(e => e.Name == "a.txt");
        Assert.Equal(textData.Length, a.Size);
        Assert.False(a.IsDir);
        Assert.False(a.IsEncrypted);
        Assert.True(Math.Abs((a.Mtime - mtime).TotalMinutes) < 2, "mtime 大致一致（DOS 时间 2s 分辨率）");
        Assert.Contains(entries, e => e.Name == "sub/b.bin");
        Assert.True(entries.Single(e => e.Name == "sub/b.bin").Size == binData.Length);

        var stats2 = svc.Extract(ZipPath, OutDir, null, null, null!, CancellationToken.None);
        Assert.Equal(2, stats2.EntryCount);
        Assert.Equal(textData, File.ReadAllBytes(Path.Combine(OutDir, "a.txt")));
        Assert.Equal(binData, File.ReadAllBytes(Path.Combine(OutDir, "sub", "b.bin")));
    }

    [Fact]
    public void EncryptedRoundTrip_Aes256_ContentPreserved()
    {
        var src = SrcDir;
        Directory.CreateDirectory(src);
        var data = "top secret 密码 payload"u8.ToArray();
        File.WriteAllBytes(Path.Combine(src, "s.txt"), data);

        var svc = new ZipService();
        svc.Compress(new[] { src }, ZipPath, CompressionLevel.Best, "pw-123", null!, CancellationToken.None);

        var entries = svc.List(ZipPath, "pw-123");
        var e = Assert.Single(entries);
        Assert.Equal("s.txt", e.Name);
        Assert.True(e.IsEncrypted, "带密码的条目应标记为加密");
        Assert.Equal(data.Length, e.Size);

        // 直接用 SharpZipLib 验证写入的是 AES-256（AESKeySize=256），而非 ZipCrypto 降级
        using (var fs = File.OpenRead(ZipPath))
        using (var zf = new ZipFile(fs))
        {
            zf.Password = "pw-123";
            var zentry = Assert.Single(zf.Cast<ZipEntry>());
            Assert.Equal(256, zentry.AESKeySize);
        }

        svc.Extract(ZipPath, OutDir, "pw-123", null, null!, CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(OutDir, "s.txt")));
    }

    [Fact]
    public void Extract_WrongPassword_Throws()
    {
        var src = SrcDir;
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "s.txt"), "secret"u8.ToArray());
        var svc = new ZipService();
        svc.Compress(new[] { src }, ZipPath, CompressionLevel.Fast, "right-pw", null!, CancellationToken.None);

        Assert.Throws<ZipException>(() =>
            svc.Extract(ZipPath, OutDir, "wrong-pw", null, null!, CancellationToken.None));
        // 错误密码不应产生任何输出文件
        Assert.False(Directory.Exists(OutDir) && Directory.EnumerateFiles(OutDir, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Extract_SkipsZipSlipEntries()
    {
        Directory.CreateDirectory(OutDir);
        using (var fs = File.Create(ZipPath))
        using (var zip = new ZipOutputStream(fs))
        {
            PutEntry(zip, "good.txt", "ok"u8.ToArray());
            PutEntry(zip, "../evil.txt", "pwn"u8.ToArray());
            PutEntry(zip, "a/../../escape.txt", "esc"u8.ToArray());
            PutEntry(zip, "a/./dot.txt", "dot"u8.ToArray());
        }

        var svc = new ZipService();
        var stats = svc.Extract(ZipPath, OutDir, null, null, null!, CancellationToken.None);

        Assert.Equal(1, stats.EntryCount); // 只有 good.txt 计入
        Assert.Equal("ok"u8.ToArray(), File.ReadAllBytes(Path.Combine(OutDir, "good.txt")));

        var parent = Path.GetDirectoryName(OutDir)!;
        Assert.False(File.Exists(Path.Combine(parent, "evil.txt")), ".. 不应逃逸到上级目录");
        Assert.False(File.Exists(Path.Combine(OutDir, "evil.txt")), "evil.txt 不应被写入");
        Assert.False(File.Exists(Path.Combine(parent, "escape.txt")), "a/../.. 不应逃逸");
        Assert.False(File.Exists(Path.Combine(OutDir, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(OutDir, "dot.txt")), "含 . 组件的条目应跳过");
    }

    [Fact]
    public void List_EncryptedEntry_ReportsIsEncrypted_WithoutPassword()
    {
        var src = SrcDir;
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "s.txt"), "secret"u8.ToArray());
        var svc = new ZipService();
        svc.Compress(new[] { src }, ZipPath, CompressionLevel.Fast, "pw", null!, CancellationToken.None);

        // 无密码也能列出元信息（不解密内容）
        var entries = svc.List(ZipPath, null);
        var e = Assert.Single(entries);
        Assert.True(e.IsEncrypted);
    }

    [Fact]
    public void Preview_ClassifiesText_Image_Binary()
    {
        var src = SrcDir;
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "note.txt"), "hello 世界 preview"u8.ToArray());
        File.WriteAllBytes(Path.Combine(src, "pic.png"),
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 });
        File.WriteAllBytes(Path.Combine(src, "blob.bin"), new byte[] { 0x00, 0xFF, 0xFE, 0x00, 0x12 });
        var svc = new ZipService();
        svc.Compress(new[] { src }, ZipPath, CompressionLevel.Fast, null, null!, CancellationToken.None);

        var text = svc.Preview(ZipPath, "note.txt", null);
        Assert.Equal("text", text.Kind);
        Assert.Equal("hello 世界 preview", text.Text);
        Assert.NotNull(text.Bytes);

        var img = svc.Preview(ZipPath, "pic.png", null);
        Assert.Equal("image", img.Kind);
        Assert.Null(img.Text);

        var bin = svc.Preview(ZipPath, "blob.bin", null);
        Assert.Equal("binary", bin.Kind);
        Assert.Null(bin.Text);
    }

    [Fact]
    public void Extract_Filter_OnlyExtractsMatching()
    {
        var src = SrcDir;
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "a.txt"), "A"u8.ToArray());
        File.WriteAllBytes(Path.Combine(src, "sub", "b.txt"), "B"u8.ToArray());
        var svc = new ZipService();
        svc.Compress(new[] { src }, ZipPath, CompressionLevel.Fast, null, null!, CancellationToken.None);

        svc.Extract(ZipPath, OutDir, null, new[] { "sub/b.txt" }, null!, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(OutDir, "a.txt")));
        Assert.Equal("B"u8.ToArray(), File.ReadAllBytes(Path.Combine(OutDir, "sub", "b.txt")));
    }

    [Fact]
    public void Compress_DuplicateEntryNames_Throws()
    {
        // 两个不同目录下同名文件 → 目录压缩会得到相同条目名，必须拒绝而非静默覆盖
        var srcA = Path.Combine(SrcDir, "A");
        var srcB = Path.Combine(SrcDir, "B");
        Directory.CreateDirectory(srcA);
        Directory.CreateDirectory(srcB);
        File.WriteAllBytes(Path.Combine(srcA, "report.txt"), "A"u8.ToArray());
        File.WriteAllBytes(Path.Combine(srcB, "report.txt"), "B"u8.ToArray());

        var svc = new ZipService();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.Compress(new[] { srcA, srcB }, ZipPath, CompressionLevel.Fast, null, null!, CancellationToken.None));
        Assert.Contains("report.txt", ex.Message);
        Assert.False(File.Exists(ZipPath), "检测到重复后不应产出归档文件");

        // 两个不同目录下的同名文件直接作为文件路径压缩，同样应拒绝
        var zip2 = Path.Combine(_root, "b.zip");
        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            svc.Compress(
                new[] { Path.Combine(srcA, "report.txt"), Path.Combine(srcB, "report.txt") },
                zip2, CompressionLevel.Fast, null, null!, CancellationToken.None));
        Assert.Contains("report.txt", ex2.Message);
    }

    private static void PutEntry(ZipOutputStream zip, string name, byte[] data)
    {
        var e = new ZipEntry(name);
        zip.PutNextEntry(e);
        zip.Write(data, 0, data.Length);
        zip.CloseEntry();
    }
}

/// <summary>PathSafety.SafeRelativePath 的纯函数校验（不依赖磁盘）。</summary>
public class PathSafetyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil")]
    [InlineData("C:\\Windows\\evil")]
    [InlineData("..")]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("a/../b.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/b/")]
    [InlineData("a\\b.txt")]
    [InlineData("x\0y.txt")]
    [InlineData(".")]
    public void SafeRelativePath_RejectsUnsafe(string name)
        => Assert.Null(PathSafety.SafeRelativePath(name));

    [Theory]
    [InlineData("a.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("中文名/子/文档.txt")]
    [InlineData("a/b/c/d.bin")]
    public void SafeRelativePath_AcceptsSafe(string name)
        => Assert.Equal(name, PathSafety.SafeRelativePath(name));
}
