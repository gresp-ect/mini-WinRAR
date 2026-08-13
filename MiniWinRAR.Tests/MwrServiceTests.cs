using MiniWinRAR.Core.Archive;
using MiniWinRAR.Core.Crypto;
using MiniWinRAR.Core.Mwr;

namespace MiniWinRAR.Tests;

/// <summary>
/// .mwr 归档服务测试：compress→list→extract→preview 端到端、加密 round-trip、
/// 路径穿越防护、filter、进度与取消。
/// </summary>
public class MwrServiceTests : IDisposable
{
    private readonly string _root;

    public MwrServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mwr-svc-" + Guid.NewGuid().ToString("N"));
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
    private string MwrPath => Path.Combine(_root, "a.mwr");

    [Theory]
    [InlineData(CompressionLevel.Store)]
    [InlineData(CompressionLevel.Fast)]
    [InlineData(CompressionLevel.Best)]
    public void RoundTrip_ContentPreserved(CompressionLevel level)
    {
        var src = SrcDir;
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        var textData = "hello 世界 mwr round-trip"u8.ToArray();
        var binData = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var mtime = new DateTimeOffset(2024, 5, 1, 12, 30, 45, TimeSpan.Zero);
        File.WriteAllBytes(Path.Combine(src, "a.txt"), textData);
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), binData);
        File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), mtime.UtcDateTime);

        var svc = new MwrService();
        var stats = svc.Compress(new[] { src }, MwrPath, level, null, null!, CancellationToken.None);
        Assert.Equal(4, stats.EntryCount); // src(dir), a.txt, sub(dir), sub/b.bin
        Assert.Equal(textData.Length + binData.Length, stats.TotalSize);
        Assert.True(File.Exists(MwrPath), ".mwr 文件已生成");

        var entries = svc.List(MwrPath, null);
        Assert.Equal(4, entries.Count);
        var a = entries.Single(e => e.Name == "a.txt");
        Assert.Equal(textData.Length, a.Size);
        Assert.False(a.IsDir);
        Assert.False(a.IsEncrypted);
        Assert.Equal(mtime.ToUnixTimeSeconds(), a.Mtime.ToUnixTimeSeconds());
        Assert.True(entries.Single(e => e.Name == "src").IsDir);
        Assert.True(entries.Single(e => e.Name == "sub").IsDir);
        Assert.True(entries.Single(e => e.Name == "sub/b.bin").Size == binData.Length);

        var stats2 = svc.Extract(MwrPath, OutDir, null, null, null!, CancellationToken.None);
        Assert.Equal(2, stats2.EntryCount); // 只计文件，不计目录
        Assert.Equal(textData, File.ReadAllBytes(Path.Combine(OutDir, "a.txt")));
        Assert.Equal(binData, File.ReadAllBytes(Path.Combine(OutDir, "sub", "b.bin")));
    }

    [Fact]
    public void EncryptedRoundTrip_ContentPreserved()
    {
        var src = SrcDir;
        Directory.CreateDirectory(src);
        var data = "top secret 密码 payload"u8.ToArray();
        File.WriteAllBytes(Path.Combine(src, "s.txt"), data);

        var svc = new MwrService();
        svc.Compress(new[] { src }, MwrPath, CompressionLevel.Best, "pw-123", null!, CancellationToken.None);

        var entries = svc.List(MwrPath, "pw-123");
        Assert.Equal(2, entries.Count); // src(dir) + s.txt
        var e = entries.Single(x => x.Name == "s.txt");
        Assert.True(e.IsEncrypted, "加密归档的条目应标记为加密");
        Assert.Equal(data.Length, e.Size);

        // .mwr header 整体加密：无密码/错误密码无法列出
        Assert.Throws<InvalidPasswordException>(() => svc.List(MwrPath, null));
        Assert.Throws<InvalidPasswordException>(() => svc.List(MwrPath, "wrong"));

        svc.Extract(MwrPath, OutDir, "pw-123", null, null!, CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(OutDir, "s.txt")));
    }

    [Fact]
    public void Extract_WrongPassword_Throws_NoOutput()
    {
        var src = SrcDir;
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "s.txt"), "secret"u8.ToArray());
        var svc = new MwrService();
        svc.Compress(new[] { src }, MwrPath, CompressionLevel.Fast, "right-pw", null!, CancellationToken.None);

        Assert.Throws<InvalidPasswordException>(() =>
            svc.Extract(MwrPath, OutDir, "wrong-pw", null, null!, CancellationToken.None));
        // 错误密码不应产生任何输出文件
        Assert.False(Directory.Exists(OutDir) && Directory.EnumerateFiles(OutDir, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Extract_SkipsTraversalEntries()
    {
        // 构造含恶意条目名的归档（MwrWriter 不校验名字，模拟攻击者可控 header）
        using (var fs = File.Create(MwrPath))
        using (var w = new MwrWriter(fs, null))
        {
            w.AddFile("good.txt", "ok"u8.ToArray(), 0, CompressionLevel.Fast);
            w.AddFile("../evil.txt", "pwn"u8.ToArray(), 0, CompressionLevel.Fast);
            w.AddFile("a/../../escape.txt", "esc"u8.ToArray(), 0, CompressionLevel.Fast);
            w.AddFile("a/./dot.txt", "dot"u8.ToArray(), 0, CompressionLevel.Fast);
            w.AddFile("a//double.txt", "db"u8.ToArray(), 0, CompressionLevel.Fast);
            w.AddDir("../evil-dir", 0);
            w.Finish();
        }

        var svc = new MwrService();
        var stats = svc.Extract(MwrPath, OutDir, null, null, null!, CancellationToken.None);

        Assert.Equal(1, stats.EntryCount); // 只有 good.txt 计入
        Assert.Equal("ok"u8.ToArray(), File.ReadAllBytes(Path.Combine(OutDir, "good.txt")));

        var parent = Path.GetDirectoryName(OutDir)!;
        Assert.False(File.Exists(Path.Combine(parent, "evil.txt")), ".. 不应逃逸到上级目录");
        Assert.False(File.Exists(Path.Combine(OutDir, "evil.txt")), "evil.txt 不应被写入");
        Assert.False(File.Exists(Path.Combine(parent, "escape.txt")), "a/../../ 不应逃逸");
        Assert.False(File.Exists(Path.Combine(OutDir, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(OutDir, "dot.txt")), "含 . 组件的条目应跳过");
        Assert.False(File.Exists(Path.Combine(OutDir, "double.txt")), "含空组件的条目应跳过");
        Assert.False(Directory.Exists(Path.Combine(parent, "evil-dir")), "恶意目录条目不应创建");
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
        var svc = new MwrService();
        svc.Compress(new[] { src }, MwrPath, CompressionLevel.Fast, null, null!, CancellationToken.None);

        var text = svc.Preview(MwrPath, "note.txt", null);
        Assert.Equal("text", text.Kind);
        Assert.Equal("hello 世界 preview", text.Text);
        Assert.NotNull(text.Bytes);

        var img = svc.Preview(MwrPath, "pic.png", null);
        Assert.Equal("image", img.Kind);
        Assert.Null(img.Text);

        var bin = svc.Preview(MwrPath, "blob.bin", null);
        Assert.Equal("binary", bin.Kind);
        Assert.Null(bin.Text);
    }

    [Fact]
    public void Preview_MissingEntry_Throws()
    {
        var src = SrcDir;
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "x");
        var svc = new MwrService();
        svc.Compress(new[] { src }, MwrPath, CompressionLevel.Fast, null, null!, CancellationToken.None);

        Assert.Throws<ArchiveCorruptedException>(() => svc.Preview(MwrPath, "nope.txt", null));
    }

    [Fact]
    public void Extract_Filter_OnlyExtractsMatching()
    {
        var src = SrcDir;
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "a.txt"), "A"u8.ToArray());
        File.WriteAllBytes(Path.Combine(src, "sub", "b.txt"), "B"u8.ToArray());
        var svc = new MwrService();
        svc.Compress(new[] { src }, MwrPath, CompressionLevel.Fast, null, null!, CancellationToken.None);

        svc.Extract(MwrPath, OutDir, null, new[] { "sub/b.txt" }, null!, CancellationToken.None);
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

        var svc = new MwrService();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.Compress(new[] { srcA, srcB }, MwrPath, CompressionLevel.Fast, null, null!, CancellationToken.None));
        Assert.Contains("report.txt", ex.Message);
        Assert.False(File.Exists(MwrPath), "检测到重复后不应产出归档文件");

        // 两个不同目录下的同名文件直接作为文件路径压缩，同样应拒绝
        var mwr2 = Path.Combine(_root, "b.mwr");
        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            svc.Compress(
                new[] { Path.Combine(srcA, "report.txt"), Path.Combine(srcB, "report.txt") },
                mwr2, CompressionLevel.Fast, null, null!, CancellationToken.None));
        Assert.Contains("report.txt", ex2.Message);
    }

    [Fact]
    public void Compress_ReportsProgress_RespectsCancellation()
    {
        var src = SrcDir;
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), new string('x', 1000));
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), new string('y', 1000));

        var progress = new SyncProgress();
        var svc = new MwrService();
        svc.Compress(new[] { src }, MwrPath, CompressionLevel.Fast, null, progress, CancellationToken.None);

        Assert.Equal(2, progress.Values.Count); // 两个文件各报一次
        Assert.Equal(100, progress.Values[^1].Pct);
        Assert.Contains(progress.Values, p => p.Name == "a.txt" || p.Name == "sub/b.txt");

        // 预取消：立即抛 OperationCanceledException
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            svc.Compress(new[] { src }, MwrPath, CompressionLevel.Fast, null, null!, cts.Token));
    }

    /// <summary>同步 IProgress：避免 Progress&lt;T&gt; 在无 SynchronizationContext 时的异步投递竞态。</summary>
    private sealed class SyncProgress : IProgress<ProgressInfo>
    {
        public List<ProgressInfo> Values { get; } = new();
        public void Report(ProgressInfo value) => Values.Add(value);
    }
}
