# mini-WinRAR 设计文档

- **日期**：2026-08-13
- **状态**：已批准（待实施）
- **主题**：一个桌面压缩/解压 GUI 应用，支持 ZIP 与自定义 `.mwr` 格式

## 1. 概述

mini-WinRAR 是一个 Tauri 桌面应用，提供类 WinRAR 的图形界面，用于压缩与解压文件。支持两种归档格式：

1. **ZIP** —— 标准格式，兼容主流解压软件。
2. **`.mwr`** —— 自研归档格式，Zstd 压缩 + AES-256-GCM 认证加密（含文件名加密）。

核心压缩、解压、加密逻辑全部在 Rust 侧实现，前端 Vue 3 仅负责 UI。

## 2. 需求

### 2.1 功能需求

- 将多个文件/文件夹压缩为 ZIP 或 `.mwr` 归档。
- 解压 ZIP / `.mwr` 归档到指定目录。
- 浏览归档内部文件列表。
- 可选密码加密（ZIP 用 AES-256；`.mwr` 用 AES-256-GCM + Argon2id）。
- 三级压缩比：store / fast / best。
- 压缩/解压过程显示进度条，支持取消。
- 拖拽文件到窗口即可加入操作。
- 预览归档内文件：文本文件显示内容，图片显示缩略图。

### 2.2 非功能需求

- 大文件流式处理，避免一次性载入内存。
- 密码不落盘，用后清零。
- 核心逻辑可单元测试。

### 2.3 明确不做（范围界定）

- 不支持 RAR / 7z 格式（RAR 有专利限制，7z 的 LZMA 实现复杂，超出本范围）。
- 不支持向已有归档追加文件。
- 不支持分卷、自解压（SFX）、归档修复。
- 不支持压缩级别的自定义数值（仅三档）。

## 3. 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 前端 | Vue 3 + Vite + TypeScript + Tailwind CSS | Tauri 官方支持 |
| 后端 | Rust（Tauri 2.x） | 核心逻辑 |
| ZIP | `zip` crate（启用 `aes-crypto` feature） | 读写 + AES-256 加密 |
| 自定义格式压缩 | `zstd` crate | Zstandard 压缩 |
| 加密 | `aes-gcm` + `argon2` crate | AES-256-GCM + Argon2id |
| 序列化 | `serde` + `bincode` | `.mwr` 元数据表 |

### 3.1 环境前置条件

- Rust 1.95（stable-x86_64-pc-windows-msvc）✅ 已确认
- Node.js 22 + pnpm 11 ✅ 已确认
- MSVC 14.50（VS 18 Community，VC.Tools.x86.x64 组件）✅ 已确认
- WebView2 131 ✅ 已确认

## 4. 架构与模块划分

```
┌─────────────────────────────────────────┐
│  前端 (WebView)                         │
│  Vue 3 + Vite + TS + Tailwind           │
│  只做 UI：拖拽、列表、进度条、预览      │
└───────────────┬─────────────────────────┘
                │ invoke (Tauri command) + event 流
┌───────────────▼─────────────────────────┐
│  Rust 后端 (核心)                       │
│  ├─ zip 模块   → zip crate (读写/加密/级别)│
│  ├─ mwr 模块   → 自定义格式（自研）      │
│  ├─ crypto 模块→ AES-GCM + Argon2id     │
│  ├─ fs 模块    → 文件系统/路径/临时文件  │
│  ├─ error 模块 → 统一 AppError          │
│  └─ commands   → 暴露给前端的接口        │
└─────────────────────────────────────────┘
```

### 4.1 目录结构

```
mini-WinRAR/
├── src/                          # 前端 Vue 3
│   ├── main.ts / App.vue
│   ├── components/
│   │   ├── Toolbar.vue           # 工具栏（压缩/解压/打开/返回）
│   │   ├── FileBrowser.vue       # 文件系统浏览
│   │   ├── ArchiveList.vue       # 归档内文件列表
│   │   ├── CompressDialog.vue    # 压缩设置对话框
│   │   ├── ExtractDialog.vue     # 解压设置对话框
│   │   ├── ProgressDialog.vue    # 进度条弹窗
│   │   └── PreviewPane.vue       # 文本预览 + 图片缩略图
│   ├── lib/
│   │   ├── commands.ts           # 封装 invoke() 调用
│   │   └── types.ts              # 与 Rust 对齐的 TS 类型
│   └── composables/              # 状态管理
├── src-tauri/
│   ├── src/
│   │   ├── lib.rs / main.rs
│   │   ├── commands.rs           # Tauri command 接口层
│   │   ├── zip.rs                # ZIP 压缩/解压
│   │   ├── mwr/
│   │   │   ├── mod.rs
│   │   │   ├── format.rs         # 二进制格式定义
│   │   │   ├── writer.rs / reader.rs
│   │   ├── crypto.rs             # AES-GCM + Argon2id
│   │   ├── fs.rs                 # 文件系统/路径处理
│   │   └── error.rs              # 统一错误类型
│   ├── Cargo.toml
│   └── tauri.conf.json
└── package.json
```

### 4.2 模块职责

| 模块 | 做什么 | 依赖 |
|---|---|---|
| `zip.rs` | ZIP 压缩/解压/列出/加密，压缩级别映射 | `zip` crate |
| `mwr/` | `.mwr` 的序列化、写入、读取 | `crypto`、`zstd`、`bincode` |
| `crypto.rs` | Argon2id 密钥派生、AES-GCM 加解密 | `aes-gcm`、`argon2` |
| `fs.rs` | 路径展开（文件夹递归）、文件读写、临时文件 | 标准库 |
| `commands.rs` | 暴露给前端的 5 个核心命令 | 上述各模块 |
| `error.rs` | 统一 `AppError`，可序列化到前端 | serde |

## 5. 核心命令接口

前端与 Rust 之间通过 5 个 Tauri command 交互，另加 2 个进度事件。

| 命令 | 输入 | 输出 | 进度事件 |
|---|---|---|---|
| `compress` | 路径列表、格式(zip/mwr)、级别、密码 | 目标路径 + 统计 | `compress://progress` |
| `list_archive` | 归档路径、密码 | 条目列表 | — |
| `extract` | 归档路径、目标目录、密码、条目筛选 | 统计 | `extract://progress` |
| `preview_file` | 归档路径、条目、密码 | 文本内容或图片字节 | — |
| `pick_paths` | 模式(选文件/选目录/选保存) | 路径 | — |

进度事件载荷：

```ts
type ProgressPayload = { name: string; pct: number }  // pct: 0-100
```

### 5.1 数据流（压缩为例）

```
GUI 选文件 → invoke("compress", {paths, format:"zip", level:"best", password})
  → commands.rs 校验参数 → 分发 zip.rs / mwr::writer 流式处理
  → 每处理一个条目 emit("compress://progress", {name, pct})
  → 前端监听 event 更新进度条
  → 完成返回 {target_path, entry_count, total_size}
```

## 6. ZIP 模块设计

- 写入：`zip::ZipWriter`，`CompressionMethod::Deflated`。
- 压缩级别映射：`store → Stored`，`fast → Deflated(1)`，`best → Deflated(9)`。
- 加密：`zip` crate 的 `aes-crypto` feature，AES-256。
- 读取：`zip::ZipArchive`，列出条目、解密、解压单个/全部。
- 流式：逐条目读源文件 → 写入 `ZipWriter`，不整体载入内存。

## 7. `.mwr` 自定义格式

### 7.1 二进制布局（加密归档）

```
┌─────────────────────────────────────────────────────┐
│ Magic "MWR1" (4B)                                    │
│ Version (1B) = 1                                     │
│ Flags (1B)            bit0 = encrypted               │
│ Salt (16B)            随机盐                          │
├─────────────────────────────────────────────────────┤
│ HeaderNonce (12B)     [仅加密时]                      │
│ Header 密文 (变长)    元数据表，AES-GCM 加密          │
│ Header Tag (16B)      GCM 认证标签                    │
├─────────────────────────────────────────────────────┤
│ Entry 0 密文 (变长)   见条目结构                      │
│ Entry 1 密文                                         │
│ ...                                                  │
└─────────────────────────────────────────────────────┘
```

非加密归档：`Salt` 仍占 16 字节（可全 0），Header 明文存储（不含 nonce/tag），条目仅 Zstd 压缩不加密。

### 7.2 单个条目结构（自包含，支持单文件解压）

```
[EntryNonce (12B)] [Entry 密文 = Zstd压缩后数据] [Entry Tag (16B)]
```

非加密时条目为 `[Zstd 压缩后数据]`，无 nonce/tag。

### 7.3 Header 元数据表（序列化后整体加密）

```rust
struct EntryMeta {
    name: String,           // 相对路径，UTF-8，/ 分隔
    uncompressed_size: u64,
    compressed_size: u64,
    mtime: u64,             // unix 时间戳
    is_dir: bool,
    data_offset: u64,       // 数据在归档中的偏移
    nonce: [u8; 12],        // 该条目的 GCM nonce
    crc32: u32,             // 解压后完整性校验
}
```

- 序列化：`serde` + `bincode` 序列化 `Vec<EntryMeta>` 得到 Header 明文，加密后写入。

### 7.4 加密与密钥派生

```
密码 → Argon2id(salt, 内存 65536 KiB, 迭代 3, 并行 4) → 32 字节主密钥
主密钥 ──用于──► 加密 Header + 所有条目（每个用独立 12B 随机 nonce）
```

- 密钥派生一次；所有条目共享主密钥，nonce 各不相同，杜绝重用。
- 每个条目独立 AES-GCM 加密，密文 + tag 一起存。
- 密码错误检测：GCM 解密失败（认证标签不匹配）→ `InvalidPassword`。
- 密码用后 `zeroize` 清零，不落盘。
- 文件名加密：加密归档时，整个 Header（含文件名）一起加密，列出文件列表需先输入密码。

### 7.5 压缩与完整性

| 项 | 选择 |
|---|---|
| 压缩 | Zstd，级别 store→0 / fast→3 / best→19 |
| 目录 | `is_dir=true` 条目无数据（`compressed_size=0`），解压时创建目录 |
| 完整性 | 加密时靠 GCM tag；非加密时靠 CRC32 |

## 8. 加密模块

- 密钥派生：`argon2::Argon2`（Argon2id 变体），参数：内存 65536 KiB、迭代 3、并行 4、输出 32 字节。
- 加密：`aes_gcm::Aes256Gcm`，每个条目/Header 用 12 字节随机 nonce，输出密文 + 16 字节 tag。
- 随机数来源：`rand` crate（`OsRng` 安全随机）。
- 密码生命周期：接收 `&str` → 派生 → 派生后立即 `zeroize` 明文密码与密钥。

## 9. GUI 布局

### 9.1 主窗口

```
┌────────────────────────────────────────────────────────────┐
│ 工具栏: [压缩] [解压] [打开归档] [返回上级] [刷新]          │
├────────────────────────────────────────────────────────────┤
│  路径栏: C:\Users\G3429\Documents ...                       │
├──────────────────────────────┬─────────────────────────────┤
│  文件浏览器 (FileBrowser)     │  预览面板 (PreviewPane)      │
│  📁 folder                    │  文本内容 / 图片缩略图       │
│  📄 readme.txt                │                             │
│  🖼 image.png                 │                             │
├──────────────────────────────┴─────────────────────────────┤
│  状态栏: 选中 3 项 · 共 12.4 MB                             │
└────────────────────────────────────────────────────────────┘
```

### 9.2 浏览模式

1. **文件系统模式**：浏览本地目录，选中文件/文件夹后「压缩」。
2. **归档模式**：打开 .zip/.mwr 后，主区切换为 `ArchiveList.vue`，可预览、解压。

### 9.3 对话框

- **压缩对话框**：格式（zip/mwr）、级别（store/fast/best）、密码（勾选「加密」启用）。
- **解压对话框**：目标目录、密码（加密归档必填）。
- **进度对话框**：进度条 + 当前文件名 + 取消按钮。

### 9.4 拖拽

使用 Tauri 2 的 `onDragDropEvent` 获取真实文件路径（绕过 WebView 的 HTML5 drag 路径限制）。

### 9.5 文件预览

- 文本：`preview_file` 解压文件内容（限制 ≤ 1MB），返回 UTF-8 字符串。
- 图片：解压到临时文件，前端读取为 blob URL 显示缩略图。

## 10. 错误处理

```rust
#[derive(Serialize)]
enum AppError {
    InvalidPassword,        // GCM 认证失败 → "密码错误"
    ArchiveCorrupted,       // 结构/CRC 校验失败
    FileNotFound,
    PermissionDenied,
    DiskFull,
    UnsupportedFormat,      // 非 zip/mwr 文件
    Io(String),
}
```

- 所有 `invoke` 返回 `Result<T, AppError>`，前端统一 `try/catch`。
- 轻错误用 toast 通知，严重错误用对话框。
- 重点提示场景：密码错误、磁盘满、权限不足。

## 11. 测试策略

| 层 | 内容 |
|---|---|
| Rust 单元 | `crypto`：加解密 round-trip、错密码必失败；`mwr`：写→读 round-trip（加密/非加密/目录/空文件/大文件）；`zip`：压缩→解压内容一致、AES 加密 |
| Rust 集成 | `compress → extract → 逐字节对比`（含中文文件名、嵌套目录、边界文件） |
| 前端 | Vitest + Vue Test Utils：`commands.ts` 封装、关键组件渲染 |

## 12. 定义完成（Definition of Done）

1. 代码符合项目既有约定（Rust 模块边界清晰，前端组件单一职责）。
2. `cargo clippy` 与 `cargo test` 通过。
3. 前端 `vue-tsc` 类型检查与 `vitest` 通过。
4. 无硬编码密码、无调试日志、无注释掉的代码。
5. 构建成功（`tauri build` 或 `cargo build` + `pnpm build`）。
6. Git diff 已审阅，无意外文件（`.env`、密钥、`node_modules`、`target/` 均不提交）。
