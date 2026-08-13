using System.Diagnostics;
using MiniWinRAR.Core.Archive;

namespace MiniWinRAR.Tests;

/// <summary>
/// FileCollector 路径展开测试：文件/目录收集、名称语义（顶层条目用 basename、子项相对该目录）、
/// 缺失路径抛 FileNotFoundException、symlink/junction（reparse point）跳过、递归深度上限。
/// </summary>
public class FileCollectorTests : IDisposable
{
    private readonly string _root;

    public FileCollectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mwr-fc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Collect_FileInput_UsesBasename()
    {
        var f = Path.Combine(_root, "a.txt");
        File.WriteAllText(f, "hello");

        var entries = FileCollector.Collect(new[] { f });
        var e = Assert.Single(entries);
        Assert.Equal("a.txt", e.Name);
        Assert.False(e.IsDir);
        Assert.Equal(5, e.Size);
        Assert.True(e.Mtime > 0, "mtime 为 unix 秒");
    }

    [Fact]
    public void Collect_DirInput_TopLevelUsesBasename_ChildrenRelative()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "A");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "B");

        var entries = FileCollector.Collect(new[] { src });

        // 顶层目录条目用 basename
        var top = Assert.Single(entries, e => e.Name == "src");
        Assert.True(top.IsDir);
        Assert.Equal(0, top.Size);
        // 子项相对该目录展开（无 src/ 前缀）
        Assert.Contains(entries, e => e.Name == "a.txt" && !e.IsDir && e.Size == 1);
        Assert.Contains(entries, e => e.Name == "sub" && e.IsDir);
        Assert.Contains(entries, e => e.Name == "sub/b.txt" && !e.IsDir);
        Assert.DoesNotContain(entries, e => e.Name == "src/a.txt");
    }

    [Fact]
    public void Collect_MissingPath_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            FileCollector.Collect(new[] { Path.Combine(_root, "nope") }));
    }

    [Fact]
    public void Collect_MultiplePaths_IncludesAll()
    {
        var f1 = Path.Combine(_root, "one.txt");
        var f2 = Path.Combine(_root, "two.txt");
        File.WriteAllText(f1, "1");
        File.WriteAllText(f2, "2");

        var entries = FileCollector.Collect(new[] { f1, f2 });
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "one.txt", "two.txt" }, entries.Select(e => e.Name).OrderBy(n => n));
    }

    [Fact]
    public void Collect_SkipsReparsePoint_DoesNotFollow()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "real.txt"), "x");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "leak.txt"), "secret");

        // 目录 junction（reparse point，无需管理员即可创建），指向收集目录之外
        var link = Path.Combine(src, "link");
        Assert.True(TryCreateDirJunction(link, outside), "junction 创建失败");

        var entries = FileCollector.Collect(new[] { src });
        Assert.DoesNotContain(entries, e => e.Name == "link");
        Assert.DoesNotContain(entries, e => e.Name.StartsWith("link/"));
        Assert.Contains(entries, e => e.Name == "real.txt");
    }

    [Fact]
    public void Collect_ReparsePointCycle_Terminates()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "loop"));
        File.WriteAllText(Path.Combine(src, "loop", "a.txt"), "x");

        // loop/loop -> loop（自引用环，若不跳过会无限递归）
        var link = Path.Combine(src, "loop", "loop");
        Assert.True(TryCreateDirJunction(link, Path.Combine(src, "loop")), "junction 创建失败");

        var entries = FileCollector.Collect(new[] { src });
        Assert.Contains(entries, e => e.Name == "loop/a.txt");
        Assert.DoesNotContain(entries, e => e.Name == "loop/loop");
    }

    [Fact]
    public void Collect_DepthCap_PreventsInfiniteRecursion()
    {
        // 140 层嵌套目录：收集必须停在深度上限内，不无限递归、不越深收集
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        var current = src;
        for (int i = 0; i < 140; i++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }

        var entries = FileCollector.Collect(new[] { src });

        // 深度上限内最深一层（129 层 = 128 递归深度 + 顶层）应存在
        var atCap = string.Join("/", Enumerable.Repeat("d", 129));
        Assert.Contains(entries, e => e.Name == atCap);
        // 超出上限的层级不应存在
        var beyondCap = string.Join("/", Enumerable.Repeat("d", 130));
        Assert.DoesNotContain(entries, e => e.Name == beyondCap);
    }

    /// <summary>
    /// 用 mklink /J 创建目录 junction（reparse point）。无需管理员即可在 NTFS 上创建；
    /// 创建失败（非 NTFS 等）时返回 false 并让测试显式失败，避免静默通过。
    /// </summary>
    private static bool TryCreateDirJunction(string junctionPath, string targetPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
