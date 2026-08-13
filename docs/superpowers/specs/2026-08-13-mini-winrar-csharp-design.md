# mini-WinRAR（C# WinForms 版）设计文档

- **日期**：2026-08-13
- **状态**：已批准（替代 Tauri/Rust/Vue 实现）
- **主题**：原生 Windows 桌面压缩/解压应用，支持 ZIP 与自定义 `.mwr` 格式

## 1. 概述

mini-WinRAR 是一个 **C# WinForms** 原生 Windows 桌面应用，提供类 WinRAR 的图形界面。核心压缩/解压/加密逻辑在 `MiniWinRAR.Core` 类库中实现，UI 在 `MiniWinRAR`（WinForms）中实现。

功能需求、`.mwr` 二进制格式、加密参数、压缩级别映射**完全复用** `2026-08-13-mini-winrar-design.md`（第 2、5、6、7、8 节），本文件只替换技术栈与架构（第 3、4 节）。

## 2. 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 目标框架 | .NET 8（LTS，`net8.0-windows`） | 环境已装 SDK 8.0.424 |
| UI | C# WinForms | 原生 Windows 控件 |
| ZIP | `System.IO.Compression` + **SharpZipLib** | 内置库无 ZIP AES，SharpZipLib 补 AES-256 |
| .mwr 压缩 | **ZstdSharp** | 纯 C# 托管 Zstd 实现 |
| .mwr 加密 | `System.Security.Cryptography.AesGcm` + **Isopoh.Cryptography.Argon2** | AES-256-GCM + Argon2id |
| CRC32 | `System.IO.Hashing.Crc32` | 内置 |
| 序列化 | `System.Text.Json` | .mwr 元数据 |
| 测试 | xUnit | |

## 3. 解决方案结构

```
MiniWinRAR.sln
├── MiniWinRAR.Core/              # 类库（纯逻辑，无 UI 依赖，可单元测试）
│   ├── Crypto/CryptoService.cs   # Argon2id 派生 + AES-GCM 加解密
│   ├── Mwr/MwrFormat.cs          # 常量 + EntryMeta（复用 spec 布局）
│   ├── Mwr/MwrWriter.cs          # 写 .mwr
│   ├── Mwr/MwrReader.cs          # 读 .mwr
│   ├── Archive/ArchiveEntry.cs   # 归档条目 DTO
│   ├── Archive/IArchiveService.cs# 统一接口 Compress/List/Extract/Preview
│   ├── Archive/ZipService.cs     # ZIP 实现
│   ├── Archive/MwrService.cs     # .mwr 实现
│   ├── Archive/ProgressReporter.cs # IProgress + CancellationToken
│   └── MiniWinRAR.Core.csproj
├── MiniWinRAR/                   # WinForms UI
│   ├── Program.cs
│   ├── MainForm.cs               # 主窗口（WinRAR 经典布局）
│   ├── Dialogs/CompressDialog.cs
│   ├── Dialogs/ExtractDialog.cs
│   ├── Dialogs/ProgressDialog.cs
│   └── MiniWinRAR.csproj
└── MiniWinRAR.Tests/             # xUnit
    └── MiniWinRAR.Tests.csproj
```

## 4. 核心接口

```csharp
public enum CompressionLevel { Store, Fast, Best }

public record ArchiveEntry(string Name, long Size, bool IsDir, DateTimeOffset Mtime, bool IsEncrypted);

public interface IArchiveService {
    ArchiveStats Compress(IEnumerable<string> paths, string target,
        CompressionLevel level, string? password,
        IProgress<ProgressInfo> progress, CancellationToken ct);
    List<ArchiveEntry> List(string archivePath, string? password);
    ArchiveStats Extract(string archivePath, string targetDir, string? password,
        IEnumerable<string>? filter, IProgress<ProgressInfo> progress, CancellationToken ct);
    PreviewResult Preview(string archivePath, string entryName, string? password);
}
```

进度：`ProgressInfo(string Name, int Pct)`；取消用 `CancellationToken`。

## 5. WinForms 主窗口布局

```
┌─────────────────────────────────────────────────────┐
│ MenuStrip: 文件  命令  工具  收藏夹  选项  帮助      │
│ ToolStrip: [添加] [解压到] [测试] [查看] [删除] ...  │
│ 地址栏: [C:\...▾] [转到]                             │
├─────────────────────────────────────────────────────┤
│ ListView (Details): 名称 | 大小 | 类型 | 修改时间    │
├─────────────────────────────────────────────────────┤
│ StatusStrip: N 个对象  X MB                          │
└─────────────────────────────────────────────────────┘
```

- 原生控件：MenuStrip、ToolStrip、ListView、StatusStrip、`OpenFileDialog`/`FolderBrowserDialog`/`SaveFileDialog`。
- 拖拽：`AllowDrop` + `DragEnter`/`DragDrop`（`DataFormats.FileDrop` 拿真实路径）。
- 进度/取消：`async/await` + `IProgress<ProgressInfo>` + `CancellationToken`。

## 6. 复用（不变的部分）

- `.mwr` 二进制布局：Magic "MWR1" + Version 1 + Flags + Salt 16B + 条目数据区 + 末尾 Header（含 8B header 长度）。
- `EntryMeta` 字段：name/uncompressed_size/compressed_size/mtime/is_dir/data_offset/nonce[12]/crc32。
- 加密：Argon2id（内存 65536 KiB/迭代 3/并行 4/输出 32B）+ AES-256-GCM（nonce 12B/tag 16B），文件名加密。
- 压缩级别：ZIP `Stored`/`Deflated(1)`/`Deflated(9)`；mwr Zstd `0`/`3`/`19`。
- 错误场景：InvalidPassword / ArchiveCorrupted / FileNotFound / PermissionDenied / DiskFull / UnsupportedFormat。

## 7. Definition of Done

1. `dotnet build` + `dotnet test` 全绿。
2. 无硬编码密码、无调试残留。
3. `.mwr` round-trip（加密/非加密）、ZIP round-trip、zip-slip 防护、mwr 路径穿越防护、header_len 边界检查均有测试。
4. 旧 Rust/Vue 代码从工作树移除（保留在 git 历史）。
