# mini-WinRAR（C# WinForms）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 C# WinForms 重实现 mini-WinRAR 桌面压缩/解压工具（原生 Windows GUI），支持 ZIP 与自定义 `.mwr` 格式。

**Architecture:** 核心逻辑在 `MiniWinRAR.Core` 类库（纯逻辑，xUnit 测试）；UI 在 `MiniWinRAR`（WinForms）。`.mwr` 格式与加密参数完全复用已批准的 Rust 版 spec（二进制布局不变）。

**Tech Stack:** .NET 8（`net8.0-windows`）、C# WinForms、`System.IO.Compression` + SharpZipLib、ZstdSharp、`System.Security.Cryptography.AesGcm`、Isopoh.Cryptography.Argon2、`System.IO.Hashing.Crc32`、System.Text.Json、xUnit。

**Spec:** `docs/superpowers/specs/2026-08-13-mini-winrar-csharp-design.md`（复用 `2026-08-13-mini-winrar-design.md` 的格式定义与功能需求）

## Global Constraints

- .NET 8 LTS，`net8.0-windows` 目标框架；环境 SDK 8.0.424。
- 压缩级别三档 `CompressionLevel { Store, Fast, Best }`，ZIP 映射 `Stored`/`Deflated(1)`/`Deflated(9)`，`.mwr` 映射 Zstd `0`/`3`/`19`。
- 加密参数固定：Argon2id（内存 65536 KiB、迭代 3、并行 4、输出 32 字节）；AES-256-GCM，nonce 12 字节、tag 16 字节；salt 16 字节。
- `.mwr` 魔数 `"MWR1"`、版本 `1`、flag bit0 = 加密；`FIXED_HEADER_LEN = 22`。
- 密码不落盘。
- 进度通过 `IProgress<ProgressInfo>` 上报，取消通过 `CancellationToken`。
- **安全（继承自 Rust 版 final review 的教训，必须一开始就做对）**：ZIP 解压用 zip-slip 防护；`.mwr` 解压用路径穿越防护；`.mwr` 读取时 `header_len`/`compressed_size`/`uncompressed_size` 边界检查；目录递归跳过符号链接/junction + 深度上限。
- 旧 Rust/Vue 代码从工作树移除（git 历史保留）。

---

## File Structure

**`MiniWinRAR.Core/`：**

| 文件 | 职责 |
|---|---|
| `Crypto/CryptoService.cs` | Argon2id 派生 + AES-GCM 加解密 + 常量 |
| `Mwr/MwrFormat.cs` | 常量、`EntryMeta`、JSON 序列化 |
| `Mwr/MwrWriter.cs` | 写 `.mwr` |
| `Mwr/MwrReader.cs` | 读 `.mwr` |
| `Archive/ArchiveModels.cs` | `CompressionLevel`、`ArchiveEntry`、`ArchiveStats`、`PreviewResult`、`ProgressInfo` |
| `Archive/IArchiveService.cs` | 统一接口 |
| `Archive/ZipService.cs` | ZIP 实现 |
| `Archive/MwrService.cs` | `.mwr` 实现 |
| `Archive/FileCollector.cs` | 路径展开（递归收集，防 symlink 循环） |
| `Archive/PathSafety.cs` | 路径穿越防护（zip-slip / mwr 穿越） |

**`MiniWinRAR/`：**

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口 |
| `MainForm.cs` | 主窗口（MenuStrip/ToolStrip/ListView/StatusStrip/地址栏） |
| `Dialogs/CompressDialog.cs` | 压缩设置 |
| `Dialogs/ExtractDialog.cs` | 解压设置 |
| `Dialogs/ProgressDialog.cs` | 进度条 + 取消 |
| `Controls/ArchiveListView.cs` | 归档条目列表（可选，或直接用 ListView） |

**`MiniWinRAR.Tests/`：** xUnit 测试。

---

## Task 1: 解决方案脚手架 + 清理旧代码

**Files:**
- Create: `MiniWinRAR.sln`, `MiniWinRAR/` (winforms), `MiniWinRAR.Core/` (classlib), `MiniWinRAR.Tests/` (xunit)
- Delete: `src/`, `src-tauri/`, `package.json`, `pnpm-lock.yaml`, `pnpm-workspace.yaml`, `vite.config.ts`, `tsconfig*.json`, `index.html`, `public/`, `README.md`, `dist/`, `node_modules/`
- Modify: `.gitignore`（加入 C# 忽略项 `bin/`、`obj/`、`*.user`）

**Interfaces:**
- Produces: 可编译的解决方案骨架，`dotnet build` + `dotnet test` 通过。

- [ ] **Step 1: 用 dotnet CLI 创建解决方案与项目**

```bash
cd "C:\Users\G3429\Desktop\mini-WinRAR"
dotnet new sln -n MiniWinRAR
dotnet new classlib -n MiniWinRAR.Core -o MiniWinRAR.Core -f net8.0
dotnet new winforms -n MiniWinRAR -o MiniWinRAR -f net8.0
dotnet new xunit -n MiniWinRAR.Tests -o MiniWinRAR.Tests -f net8.0
dotnet sln add MiniWinRAR.Core MiniWinRAR MiniWinRAR.Tests
dotnet add MiniWinRAR reference MiniWinRAR.Core
dotnet add MiniWinRAR.Tests reference MiniWinRAR.Core
```

- [ ] **Step 2: 清理旧的 Rust/Vue 文件**

```bash
git rm -r src src-tauri package.json pnpm-lock.yaml pnpm-workspace.yaml vite.config.ts tsconfig.json tsconfig.node.json index.html public README.md 2>/dev/null
rm -rf dist node_modules .vscode 2>/dev/null
```

- [ ] **Step 3: 更新 .gitignore**（保留 secrets 规则，追加 C# 忽略项）

```
bin/
obj/
*.user
.vs/
```

- [ ] **Step 4: 验证构建与测试**

```bash
dotnet build && dotnet test
```

Expected: 构建成功，测试（空 xunit 模板）通过。

- [ ] **Step 5: 提交**

```bash
git add -A && git commit -m "chore: scaffold C# WinForms solution, remove Tauri/Vue"
```

---

## Task 2: Crypto/CryptoService.cs

**Files:**
- Create: `MiniWinRAR.Core/Crypto/CryptoService.cs`
- Modify: `MiniWinRAR.Core/MiniWinRAR.Core.csproj`（加 Isopoh.Cryptography.Argon2）
- Test: `MiniWinRAR.Tests/CryptoServiceTests.cs`

**Interfaces:**
- Produces:
  - `public const int SaltLen = 16; public const int NonceLen = 12; public const int TagLen = 16; public const int KeyLen = 32;`
  - `public static byte[] DeriveKey(string password, byte[] salt)`（Argon2id 65536/3/4/32）
  - `public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext)`（返回 ciphertext||tag）
  - `public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext)`（含 tag，失败抛 `InvalidPasswordException`）
  - `public static byte[] RandomBytes(int n)`

- [ ] **Step 1: 加依赖**

```bash
dotnet add MiniWinRAR.Core package Isopoh.Cryptography.Argon2
```

- [ ] **Step 2: 写失败测试**

```csharp
public class CryptoServiceTests {
    [Fact] public void Roundtrip_EncryptDecrypt() {
        var key = CryptoService.DeriveKey("password", new byte[CryptoService.SaltLen]);
        var nonce = new byte[CryptoService.NonceLen]; // 全 0 可预测，测试用
        var pt = Encoding.UTF8.GetBytes("hello, mini-WinRAR!");
        var ct = CryptoService.Encrypt(key, nonce, pt);
        Assert.Equal(pt.Length + CryptoService.TagLen, ct.Length);
        Assert.Equal(pt, CryptoService.Decrypt(key, nonce, ct));
    }
    [Fact] public void WrongKey_Fails() {
        var k1 = CryptoService.DeriveKey("right", new byte[CryptoService.SaltLen]);
        var k2 = CryptoService.DeriveKey("wrong", new byte[CryptoService.SaltLen]);
        var ct = CryptoService.Encrypt(k1, new byte[CryptoService.NonceLen], new byte[]{1,2,3});
        Assert.Throws<InvalidPasswordException>(() => CryptoService.Decrypt(k2, new byte[CryptoService.NonceLen], ct));
    }
    [Fact] public void DeriveKey_Deterministic_32Bytes() {
        var a = CryptoService.DeriveKey("pw", new byte[CryptoService.SaltLen]);
        var b = CryptoService.DeriveKey("pw", new byte[CryptoService.SaltLen]);
        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, CryptoService.DeriveKey("pw", new byte[CryptoService.SaltLen].Select(x => (byte)1).ToArray()));
    }
}
```

- [ ] **Step 3: 运行确认失败** — `dotnet test`，FAIL（类不存在）。

- [ ] **Step 4: 实现 CryptoService.cs**

```csharp
using System.Security.Cryptography;
using Isopoh.Cryptography.Argon2;

namespace MiniWinRAR.Core.Crypto;

public class InvalidPasswordException : Exception {
    public InvalidPasswordException() : base("密码错误") {}
}

public static class CryptoService {
    public const int SaltLen = 16, NonceLen = 12, TagLen = 16, KeyLen = 32;

    public static byte[] RandomBytes(int n) {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    public static byte[] DeriveKey(string password, byte[] salt) {
        var config = new Argon2Config {
            Type = Argon2Type.DataIndependentAddressing, // Argon2id
            Version = Argon2Version.Nineteen,
            TimeCost = 3, MemoryCost = 65536, Lanes = 4, Threads = 4,
            Password = Encoding.UTF8.GetBytes(password),
            Salt = salt, HashLength = KeyLen,
        };
        using var argon2 = new Argon2(config);
        return argon2.Hash().Buffer.ToArray();
    }

    public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext) {
        var ct = new byte[plaintext.Length];
        var tag = new byte[TagLen];
        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(nonce, plaintext, ct, tag);
        return ct.Concat(tag).ToArray();
    }

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext) {
        try {
            var ctLen = ciphertext.Length - TagLen;
            var ct = ciphertext[..ctLen];
            var tag = ciphertext[ctLen..];
            var pt = new byte[ctLen];
            using var aes = new AesGcm(key, TagLen);
            aes.Decrypt(nonce, ct, tag, pt);
            return pt;
        } catch (CryptographicException) {
            throw new InvalidPasswordException();
        }
    }
}
```

- [ ] **Step 5: 运行确认通过** — `dotnet test`，3 个测试 PASS。

- [ ] **Step 6: 提交**

```bash
git add -A && git commit -m "feat: add AES-GCM + Argon2id crypto service"
```

---

## Task 3: Mwr/MwrFormat.cs

**Files:**
- Create: `MiniWinRAR.Core/Mwr/MwrFormat.cs`
- Test: `MiniWinRAR.Tests/MwrFormatTests.cs`

**Interfaces:**
- Produces:
  - `public const byte[] Magic = "MWR1"u8...; public const byte Version = 1; public const byte FlagEncrypted = 0x01; public const long FixedHeaderLen = 22;`
  - `public class EntryMeta { public string Name; public long UncompressedSize; public long CompressedSize; public long Mtime; public bool IsDir; public long DataOffset; public byte[] Nonce; public uint Crc32; }`
  - `public static byte[] Serialize(List<EntryMeta> entries)`（System.Text.Json）
  - `public static List<EntryMeta> Deserialize(byte[] bytes)`

- [ ] **Step 1: 写失败测试**（round-trip 含中文文件名 + 常量）

- [ ] **Step 2: 实现 MwrFormat.cs**（JsonSerializer，`Nonce` 用 `byte[]` 序列化为数组）

- [ ] **Step 3: 运行确认通过 + 提交**

---

## Task 4: Mwr/MwrWriter.cs

**Files:** Create `MiniWinRAR.Core/Mwr/MwrWriter.cs`；Modify csproj（加 ZstdSharp）；Test `MwrWriterTests.cs`

**Interfaces:**
- Produces:
  - `public class MwrWriter : IDisposable`
  - `public MwrWriter(Stream output, string? password)`（写 magic + version + flags + salt）
  - `public void AddFile(string name, byte[] data, long mtime, CompressionLevel level)`
  - `public void AddDir(string name, long mtime)`
  - `public void Finish()`（写末尾 header + 8B header 长度）

- [ ] **Step 1: 加依赖** `dotnet add MiniWinRAR.Core package ZstdSharp.Port`
- [ ] **Step 2: 写失败测试**（写入固定头 + zstd 级别映射）
- [ ] **Step 3: 实现**（BinaryWriter + 与 Rust 版相同布局：加密条目 = nonce + ciphertext||tag；`data_offset` 指向条目起始；`compressed_size` 含 tag）
- [ ] **Step 4: 运行确认通过 + 提交**

关键布局（与 Rust 版一致）：

```
偏移 0: Magic(4) Version(1) Flags(1) Salt(16)   ← 22 字节固定头
偏移 22: 条目数据（加密: nonce(12)+ciphertext||tag；非加密: zstd 数据）
末尾: header 区 = [nonce(12)(加密)] [header JSON 明文/密文] [headerLen u64 LE]
```

---

## Task 5: Mwr/MwrReader.cs

**Files:** Create `MiniWinRAR.Core/Mwr/MwrReader.cs`；Test `MwrReaderTests.cs`

**Interfaces:**
- Produces:
  - `public class MwrReader : IDisposable`
  - `public MwrReader(Stream input, string? password)`（解析头 + 末尾 header，密码错抛 InvalidPasswordException，`header_len` 越界抛 ArchiveCorruptedException）
  - `public List<EntryMeta> Entries { get; }`、`public bool IsEncrypted { get; }`
  - `public byte[] ReadFile(int index)`（解密 + 解压 + CRC 校验）

- [ ] **Step 1: 写失败测试**（round-trip 非加密 + 加密 + 错密码 + truncated header）
- [ ] **Step 2: 实现**（**必须含边界检查**：`header_len` 越界、`data_offset + compressed_size > 文件长`、`uncompressed_size > 1 GiB` 均抛 `ArchiveCorruptedException`）
- [ ] **Step 3: 运行确认通过 + 提交**

---

## Task 6: Archive/ZipService.cs

**Files:** Create `Archive/ArchiveModels.cs`、`Archive/IArchiveService.cs`、`Archive/PathSafety.cs`、`Archive/ZipService.cs`；Modify csproj（加 SharpZipLib）；Test `ZipServiceTests.cs`

**Interfaces:**
- Produces:
  - `ArchiveModels.cs`：`enum CompressionLevel`、`record ArchiveEntry(...)`、`record ArchiveStats(...)`、`record PreviewResult(...)`、`record ProgressInfo(string Name, int Pct)`、`class ArchiveCorruptedException`
  - `PathSafety.cs`：`public static string? SafeRelativePath(string name)`（`..`/绝对路径/盘符/NUL/`\` → null）
  - `IArchiveService.cs`：见 spec §4
  - `ZipService`：实现 `IArchiveService`

- [ ] **Step 1: 加依赖** `dotnet add MiniWinRAR.Core package SharpZipLib`
- [ ] **Step 2: 写失败测试**（round-trip + AES 加密 + zip-slip 防护）
- [ ] **Step 3: 实现**（`System.IO.Compression` 做 Deflate 读写；AES 用 SharpZipLib `ZipOutputStream`+`Password`/`AESKeySize`，或执行时按 SharpZipLib 实际 API 调整；解压用 `SafeRelativePath` 防 zip-slip）
- [ ] **Step 4: 运行确认通过 + 提交**

---

## Task 7: Archive/MwrService.cs + FileCollector.cs

**Files:** Create `Archive/FileCollector.cs`、`Archive/MwrService.cs`；Test `MwrServiceTests.cs`、`FileCollectorTests.cs`

**Interfaces:**
- Produces:
  - `FileCollector.cs`：`public static List<FileEntry> Collect(IEnumerable<string> paths)`（递归收集，`file_type` 跳过 symlink，深度上限 128；`FileEntry(string Name, string Path, bool IsDir, long Size, long Mtime)`）
  - `MwrService`：实现 `IArchiveService`（内部用 MwrWriter/MwrReader）

- [ ] **Step 1: 写失败测试**（mwr 端到端 compress→list→extract→preview；路径穿越防护；symlink 跳过）
- [ ] **Step 2: 实现**（`extract` 用 `PathSafety.SafeRelativePath` 防穿越；进度 `IProgress` + 取消 `CancellationToken`）
- [ ] **Step 3: 运行确认通过 + 提交**

---

## Task 8: WinForms MainForm.cs

**Files:** Create `MiniWinRAR/MainForm.cs`（含 Designer 或代码式布局）；Modify `Program.cs`

**Interfaces:**
- Produces: 主窗口（MenuStrip 文件/命令/工具/选项/帮助；ToolStrip 添加/解压到/查看；地址栏；ListView Details 列 名称/大小/类型/修改时间；StatusStrip 选中统计）。

- [ ] **Step 1: 实现主窗口**（代码式布局，ListView `View.Details`、`AllowDrop=true`、`FullRowSelect`）
- [ ] **Step 2: 文件系统浏览**（双击目录进入，`FileCollector`/`DirectoryInfo` 列目录）
- [ ] **Step 3: `dotnet build` 通过 + 提交**

---

## Task 9: WinForms 对话框

**Files:** Create `Dialogs/CompressDialog.cs`、`Dialogs/ExtractDialog.cs`、`Dialogs/ProgressDialog.cs`

**Interfaces:**
- Produces:
  - `CompressDialog`（格式 zip/mwr、级别、密码；返回配置）
  - `ExtractDialog`（目标目录 FolderBrowserDialog、密码）
  - `ProgressDialog`（ProgressBar + 取消按钮 + CancellationTokenSource）

- [ ] **Step 1: 实现三个对话框**
- [ ] **Step 2: `dotnet build` 通过 + 提交**

---

## Task 10: 事件接线 + 拖拽

**Files:** Modify `MainForm.cs`

**Interfaces:**
- Consumes: 前序所有。
- Produces: 完整流程——压缩/解压/打开归档/预览/拖拽都接线。

- [ ] **Step 1: 接线**（工具栏/菜单事件 → 对话框 → `IArchiveService` + `async/await` + `IProgress` + `CancellationToken`；`DragEnter`/`DragDrop` 用 `DataFormats.FileDrop`）
- [ ] **Step 2: `dotnet build` + `dotnet test` 通过 + 提交**

---

## Task 11: 端到端验证

- [ ] **Step 1: 全量验证**

```bash
dotnet build && dotnet test
```

Expected: 全部通过，无警告。

- [ ] **Step 2: 手动 GUI 走查清单**（留给用户，subagent 无法交互执行）

```
dotnet run --project MiniWinRAR
```

1. 浏览目录、选中文件 → 压缩为 zip → 系统解压软件可打开
2. 压缩为 mwr（密码）→ 打开输入密码 → 预览文本 → 解压 → 内容一致
3. 解压输错密码 → 提示「密码错误」
4. 拖拽文件夹 → 自动进入；拖拽归档 → 打开归档视图

- [ ] **Step 3: 提交**

```bash
git add -A && git commit -m "feat: complete end-to-end WinForms flow"
```

---

## Self-Review

- **Spec 覆盖**：功能需求（压缩/解压/列出/加密/级别/进度/取消/拖拽/预览）→ Task 6/7/8/9/10；`.mwr` 格式 + 加密 → Task 2/3/4/5；安全（zip-slip/mwr 穿越/header_len 边界/symlink）→ Task 5/6/7 明确要求；旧代码清理 → Task 1。
- **类型一致性**：`CompressionLevel`/`ArchiveEntry`/`ArchiveStats`/`PreviewResult`/`ProgressInfo` 在 ArchiveModels.cs 统一定义，`IArchiveService` 接口贯穿。
- **安全**：从 Rust 版 final review 的教训中预先内置（边界检查、路径穿越防护、symlink 防护），不在最后一刻才补。
- **已知待确认**：SharpZipLib 的 AES API（`Password`/`AESKeySize`）与 ZstdSharp 的 `Compressor` API 可能有版本差异，执行时以 NuGet 包实际 API 为准修正（核心流程不变）。
