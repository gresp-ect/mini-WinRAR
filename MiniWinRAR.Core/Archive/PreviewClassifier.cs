namespace MiniWinRAR.Core.Archive;

/// <summary>
/// 预览内容分类（text / image / binary），ZIP 与 .mwr 归档服务共用。
/// 从 ZipService 抽出，避免两份实现分叉。
/// </summary>
internal static class PreviewClassifier
{
    /// <summary>按 image → text → binary 顺序判定内容种类。</summary>
    public static string ClassifyKind(byte[] bytes)
    {
        if (IsImage(bytes)) return "image";
        if (LooksLikeText(bytes)) return "text";
        return "binary";
    }

    /// <summary>魔数识别：WebP / PNG / JPEG / GIF / BMP。</summary>
    public static bool IsImage(byte[] b)
    {
        if (b.Length >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return true;
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;                   // JPEG
        if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return true;  // GIF
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return true;                                   // BMP
        return false;
    }

    /// <summary>启发式文本判定：无 NUL 且整体是合法 UTF-8（覆盖中文等多字节文本）。</summary>
    public static bool LooksLikeText(byte[] b)
    {
        if (b.Length == 0) return false;
        if (b.AsSpan().IndexOf((byte)0) >= 0) return false;
        return IsValidUtf8(b);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> b)
    {
        int i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            if (c < 0x80) { i++; continue; }
            int needed;
            if ((c & 0xE0) == 0xC0) needed = 1;
            else if ((c & 0xF0) == 0xE0) needed = 2;
            else if ((c & 0xF8) == 0xF0) needed = 3;
            else return false;
            if (i + needed >= b.Length) return false;
            for (int k = 1; k <= needed; k++)
                if ((b[i + k] & 0xC0) != 0x80) return false;
            i += needed + 1;
        }
        return true;
    }
}
