using MiniWinRAR.Core.Archive;
using MiniWinRAR.Core.Mwr;

namespace MiniWinRAR.Tests;

public class ArchiveProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _src;

    public ArchiveProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"probe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _src = Path.Combine(_dir, "a.txt");
        File.WriteAllText(_src, "probe data");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Mwr_Encrypted_Detected()
    {
        var path = Path.Combine(_dir, "enc.mwr");
        using (var fs = File.Create(path))
        using (var w = new MwrWriter(fs, "secret"))
        {
            w.AddFile("a.txt", [1, 2, 3], 0, CompressionLevel.Fast);
            w.Finish();
        }
        Assert.True(ArchiveProbe.IsEncrypted(path));
    }

    [Fact]
    public void Mwr_Plain_NotDetected()
    {
        var path = Path.Combine(_dir, "plain.mwr");
        using (var fs = File.Create(path))
        using (var w = new MwrWriter(fs, null))
        {
            w.AddFile("a.txt", [1, 2, 3], 0, CompressionLevel.Fast);
            w.Finish();
        }
        Assert.False(ArchiveProbe.IsEncrypted(path));
    }

    [Fact]
    public void Zip_Encrypted_Detected()
    {
        var path = Path.Combine(_dir, "enc.zip");
        new ZipService().Compress([_src], path, CompressionLevel.Fast, "secret", null, CancellationToken.None);
        Assert.True(ArchiveProbe.IsEncrypted(path));
    }

    [Fact]
    public void Zip_Plain_NotDetected()
    {
        var path = Path.Combine(_dir, "plain.zip");
        new ZipService().Compress([_src], path, CompressionLevel.Fast, null, null, CancellationToken.None);
        Assert.False(ArchiveProbe.IsEncrypted(path));
    }
}
