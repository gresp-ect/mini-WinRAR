namespace MiniWinRAR.Core.Archive;

/// <summary>
/// 归档条目路径安全校验，防止 zip-slip / 路径穿越。
/// 校验通过返回原样 name（POSIX 风格相对路径，分隔符为 '/'），否则返回 null。
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// 返回可用于安全拼接的相对路径；任何不安全的输入返回 null：
    /// 空串、绝对路径（前导 '/'）、盘符（如 "C:"）、含 NUL、含反斜杠 '\'、
    /// 或含任何 ""/./.. 路径组件。
    /// </summary>
    public static string? SafeRelativePath(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.IndexOf('\0') >= 0) return null;
        if (name.Contains('\\')) return null;        // 统一反斜杠路径，Windows 上属绝对/穿越形态
        if (name.StartsWith('/')) return null;        // 绝对路径（POSIX）
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':') return null; // 盘符

        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0) return null;        // 空组件（"a//b"、"a/b/"）
            if (part == ".") return null;
            if (part == "..") return null;
        }
        return name;
    }
}
