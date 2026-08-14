# mini-WinRAR

一个桌面压缩 / 解压工具，C# + WinForms 原生 Windows GUI，.NET 8。

支持标准 **ZIP** 格式，以及自研加密归档格式 **.mwr**（Zstd 压缩 + AES-256-GCM 加密 + Argon2id 密钥派生）。

## 功能

- **压缩**：选中的文件 / 目录打包为 `.zip` 或 `.mwr`
  - 可选密码：ZIP 用 AES-256 加密，.mwr 用 AES-256-GCM + Argon2id 派生密钥
  - 进度对话框，可取消
- **解压**：`.zip` / `.mwr` 解压到任意目标目录
  - 自动探测归档是否需要密码；仅当归档加密时显示密码输入框
  - 目标目录可浏览选择、对话框可拉伸
- **文件浏览**：内置文件系统视图，与资源管理器一致的 **Shell 图标**
  - 16 / 32 / 48px 各档位原生清晰（`IShellItemImageFactory`）
  - 保留 alpha 通道（无黑框）、方向正确
  - 特殊文件夹（桌面、音乐、视频、下载、文档等）显示专属图标
  - `.exe` / `.ico` / `.lnk` 等显示文件自身内嵌图标
- **Ctrl + 鼠标滚轮** 缩放文件 / 目录列表（图标 + 文字同步缩放）
- **拖拽**：把文件 / 归档拖进窗口即可触发对应操作
- 归档条目可预览（文本 / 图片分类），操作互斥防止并发冲突

## 技术栈

| 层 | 技术 |
| --- | --- |
| 语言 / 框架 | C# 12, .NET 8 (`net8.0-windows`) |
| GUI | Windows Forms（代码式布局，无 Designer / .resx） |
| ZIP | SharpZipLib 1.4.2（含 AES-256 加密） |
| .mwr 压缩 | ZstdSharp 0.7.2 |
| 加密 | `AesGcm`（.NET 内置）+ Isopoh Argon2 2.0.0 |
| 测试 | xUnit 2.5.3（93 个用例） |

## 项目结构

```
MiniWinRAR.sln
├── MiniWinRAR/               # WinForms 主程序（UI）
│   ├── MainForm.cs           # 主窗口：浏览 / 压缩 / 解压 / 缩放
│   ├── ShellIcon.cs          # Shell 图标（SHGetFileInfo + IShellItemImageFactory）
│   ├── Program.cs
│   └── Dialogs/              # 压缩 / 解压 / 进度对话框
├── MiniWinRAR.Core/          # 核心类库（无 UI 依赖）
│   ├── Archive/              # ZIP / .mwr 服务、加密探测、路径安全
│   ├── Crypto/               # AES-GCM、CRC32
│   └── Mwr/                  # .mwr 格式读写器
├── MiniWinRAR.Tests/         # xUnit 测试
└── NuGet.Config              # 仓库级 NuGet 配置
```

## 构建与运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
# 构建 Release
dotnet build -c Release

# 运行（或直接运行 bin/Release/net8.0-windows/MiniWinRAR.exe）
dotnet run --project MiniWinRAR

# 运行测试
dotnet test
```

> 仓库自带 `NuGet.Config`（仓库级），必要时需覆盖机器级损坏的 fallback 路径。

## 归档格式

### ZIP

标准 ZIP 格式，通过 SharpZipLib 读写。设置密码时使用 AES-256 加密条目；未加密时使用 Deflate 压缩。

### .mwr（自定义加密格式）

固定二进制头（明文）+ 加密条目流：

```
Magic(4) | Version(1) | Flags(1) | Salt(16)          # 22 字节固定头
```

- `Flags` bit0 表示是否加密
- 文件名、内容均由 AES-256-GCM 加密；密钥由 Argon2id（口令 + 随机 Salt）派生
- 压缩使用 Zstd

## 测试

```bash
dotnet test
```

覆盖：Mwr 读写 / 格式、ZIP 压缩解压、AES-GCM 加密、Argon2 派生、路径安全、加密探测、文件收集。93 个用例全部通过。

## 许可证

[MIT License](LICENSE)
