using MiniWinRAR.Core.Archive;

namespace MiniWinRAR.Tests;

public class ArchivePathTests
{
    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("a.mwr", true)]
    [InlineData("a.ZIP", true)]
    [InlineData("a.Mwr", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.zip.bak", false)]
    [InlineData("folder", false)]
    [InlineData("", false)]
    public void IsArchive_DetectsZipAndMwr(string path, bool expected)
        => Assert.Equal(expected, ArchivePath.IsArchive(path));
}
