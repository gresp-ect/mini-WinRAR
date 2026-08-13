# mini-WinRAR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个 Tauri 桌面压缩/解压工具，支持 ZIP 与自定义 `.mwr` 格式（含加密与预览）。

**Architecture:** Rust 后端承载全部压缩/解压/加密逻辑（`zip`、`zstd`、`aes-gcm`、`argon2` crate），通过 4 个 Tauri command 暴露给 Vue 3 前端；前端仅做 UI 与进度事件监听。核心逻辑与 Tauri 事件层解耦，进度通过 `FnMut(&str, u8)` 回调注入。

**Tech Stack:** Tauri 2.x + Rust 1.95 + Vue 3 + Vite + TypeScript + Tailwind CSS；`zip` 2（aes-crypto）、`zstd`、`aes-gcm`、`argon2`、`bincode` 1.3、`serde`、`rand`、`zeroize`、`crc32fast`。

**Spec:** `docs/superpowers/specs/2026-08-13-mini-winrar-design.md`

## Global Constraints

- Rust 工具链 `stable-x86_64-pc-windows-msvc`（1.95）；Node 22 + pnpm 11。
- 压缩级别三档 `CompressionLevel { Store, Fast, Best }`，ZIP 映射 `Stored`/`Deflated(1)`/`Deflated(9)`，`.mwr` 映射 Zstd `0`/`3`/`19`。
- 加密参数固定：Argon2id（内存 65536 KiB、迭代 3、并行 4、输出 32 字节）；AES-256-GCM，nonce 12 字节、tag 16 字节；salt 16 字节。
- `.mwr` 魔数 `"MWR1"`，版本 `1`，flag bit0 = 加密。
- 密码不落盘，用后 `zeroize` 清零。
- 前端与 Rust 通信仅经 4 个 command：`compress`、`list_archive`、`extract`、`preview_file`。文件/目录选择用前端 `@tauri-apps/plugin-dialog`，不走 Rust command（对 spec 的简化）。
- **MVP 边界（对 spec「流式」的务实取舍）**：单个文件在内存中完成「读入 → 压缩 → 加密 → 写归档」，不做边读边写的流式管道；单个文件大小受进程内存限制。归档级 `EntryMeta` 元数据在内存中（极小）。这是 mini 定位的可接受边界，流式化留作后续优化。
- `.mwr` 布局采用「header 在末尾」顺序（先写数据、后写 header），因为 `data_offset` 依赖数据实际写入位置——这是对 spec 7.1 布局图的实现修正，格式仍自洽（见 Task 5）。

---

## File Structure

**Rust（`src-tauri/src/`）：**

| 文件 | 职责 |
|---|---|
| `lib.rs` | 注册 command、初始化插件（dialog）、事件发射入口 |
| `error.rs` | `AppError` 枚举，`Serialize` + `Display` + `From<io::Error>` |
| `types.rs` | 共享类型：`CompressionLevel`、`ArchiveStats`、`ArchiveEntry` |
| `crypto.rs` | `derive_key`（Argon2id）、`encrypt`/`decrypt`（AES-GCM） |
| `fs.rs` | `collect_entries`（路径展开，目录递归）、`read_dir_entries` |
| `mwr/mod.rs` | 模块声明 + re-export |
| `mwr/format.rs` | 常量、`EntryMeta`、`EntryMeta::serialize_all`/`deserialize_all` |
| `mwr/writer.rs` | `MwrWriter`：写魔数、流式加文件、写 header |
| `mwr/reader.rs` | `MwrReader`：读魔数、解析 header、按条目读数据 |
| `zip.rs` | `compress_zip`、`list_zip`、`extract_zip` |
| `commands.rs` | 4 个 `#[tauri::command]`，注入进度回调并 `emit` |

**前端（`src/`）：**

| 文件 | 职责 |
|---|---|
| `lib/types.ts` | TS 类型镜像 Rust 类型 |
| `lib/commands.ts` | `invoke` 封装 |
| `composables/useArchive.ts` | 全局响应式状态（当前目录、归档列表、进度） |
| `components/Toolbar.vue` | 顶部按钮 |
| `components/FileBrowser.vue` | 本地文件系统浏览 |
| `components/ArchiveList.vue` | 归档内文件列表 |
| `components/CompressDialog.vue` | 压缩设置（格式/级别/密码） |
| `components/ExtractDialog.vue` | 解压设置（目录/密码） |
| `components/ProgressDialog.vue` | 进度条 + 取消 |
| `components/PreviewPane.vue` | 文本预览 + 图片缩略图 |
| `App.vue` | 布局 + 双模式切换 |

---

## Task 1: 项目脚手架

**Files:**
- Create: `package.json`, `vite.config.ts`, `tsconfig.json`, `index.html`, `src/`（由脚手架生成）
- Create: `src-tauri/Cargo.toml`, `src-tauri/tauri.conf.json`, `src-tauri/build.rs`, `src-tauri/src/main.rs`, `src-tauri/src/lib.rs`, `src-tauri/icons/`
- Modify: 无

**Interfaces:**
- Produces: 可运行的 Tauri + Vue 3 + TS 骨架，`pnpm tauri dev` 能弹出空窗口。

- [ ] **Step 1: 用 create-tauri-app 在临时目录生成 Vue+TS 模板**

```bash
cd /tmp  # Git Bash 临时目录，Windows 上可用 $TEMP
pnpm create tauri-app@latest mini-winrar-tmp --template vue-ts --manager pnpm --yes
```

- [ ] **Step 2: 将生成内容合并到项目根目录（保留已有 docs/ 与 .gitignore）**

```bash
cd "C:\Users\G3429\Desktop\mini-WinRAR"
# 复制除 .git 外的所有生成文件到根目录
cp -r /tmp/mini-winrar-tmp/. ./
# 确保 .gitignore 覆盖 target/、dist/、node_modules/（Task 前的 .gitignore 已含）
```

- [ ] **Step 3: 安装依赖**

```bash
cd "C:\Users\G3429\Desktop\mini-WinRAR"
pnpm install
```

- [ ] **Step 4: 首次构建验证（Rust 能编译、前端能打包）**

```bash
pnpm tauri build --no-bundle   # 或 cargo build 在 src-tauri 内
```

Expected: 编译成功，无错误。若 MSVC 链接报错，运行 VS Developer Prompt 环境后重试。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "chore: scaffold Tauri + Vue 3 + TS project"
```

---

## Task 2: error.rs 与 types.rs（共享类型）

**Files:**
- Create: `src-tauri/src/error.rs`
- Create: `src-tauri/src/types.rs`
- Modify: `src-tauri/src/lib.rs`（声明模块）

**Interfaces:**
- Produces: `AppError`（含变体 `InvalidPassword`、`ArchiveCorrupted`、`FileNotFound`、`PermissionDenied`、`DiskFull`、`UnsupportedFormat`、`Io(String)`）；`CompressionLevel::{Store,Fast,Beset}`；`ArchiveStats`；`ArchiveEntry`。全部 `Serialize` + `Deserialize`，`CompressionLevel` 另实现 `Copy`、`Clone`、`PartialEq`。

- [ ] **Step 1: 写编译期类型断言测试（验证 trait 与字段）**

`src-tauri/src/types.rs` 内 `#[cfg(test)]`：

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compression_level_serializes() {
        let lvl = CompressionLevel::Best;
        let s = serde_json::to_string(&lvl).unwrap();
        assert!(s.contains("Best"));
    }

    #[test]
    fn error_serializes_and_is_io_convertible() {
        let e: AppError = std::io::Error::new(std::io::ErrorKind::NotFound, "x").into();
        let s = serde_json::to_string(&e).unwrap();
        assert!(s.contains("NotFound") || s.contains("FileNotFound"));
        let e2 = AppError::InvalidPassword;
        assert_eq!(serde_json::to_string(&e2).unwrap(), "\"InvalidPassword\"");
    }
}
```

- [ ] **Step 2: 运行测试，确认失败（模块未定义）**

```bash
cd src-tauri && cargo test error_types
```

Expected: FAIL，编译错误（模块缺失）。

- [ ] **Step 3: 实现 error.rs 与 types.rs**

`error.rs`:

```rust
use serde::Serialize;

#[derive(Debug, Serialize)]
pub enum AppError {
    InvalidPassword,
    ArchiveCorrupted,
    FileNotFound,
    PermissionDenied,
    DiskFull,
    UnsupportedFormat,
    Io(String),
}

impl std::fmt::Display for AppError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            AppError::InvalidPassword => write!(f, "密码错误"),
            AppError::ArchiveCorrupted => write!(f, "归档已损坏"),
            AppError::FileNotFound => write!(f, "文件不存在"),
            AppError::PermissionDenied => write!(f, "权限不足"),
            AppError::DiskFull => write!(f, "磁盘空间不足"),
            AppError::UnsupportedFormat => write!(f, "不支持的格式"),
            AppError::Io(m) => write!(f, "{m}"),
        }
    }
}

impl std::error::Error for AppError {}

impl From<std::io::Error> for AppError {
    fn from(e: std::io::Error) -> Self {
        match e.kind() {
            std::io::ErrorKind::NotFound => AppError::FileNotFound,
            std::io::ErrorKind::PermissionDenied => AppError::PermissionDenied,
            _ => AppError::Io(e.to_string()),
        }
    }
}

pub type AppResult<T> = Result<T, AppError>;
```

`types.rs`:

```rust
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum CompressionLevel {
    Store,
    Fast,
    Best,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ArchiveStats {
    pub entry_count: u64,
    pub total_size: u64,
    pub target_path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ArchiveEntry {
    pub name: String,
    pub size: u64,
    pub is_dir: bool,
    pub mtime: u64,
    pub is_encrypted: bool,
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd src-tauri && cargo test error_types
```

Expected: PASS（2 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src-tauri/src/error.rs src-tauri/src/types.rs src-tauri/src/lib.rs
git commit -m "feat: add AppError and shared types"
```

---

## Task 3: crypto.rs（Argon2id + AES-GCM）

**Files:**
- Create: `src-tauri/src/crypto.rs`
- Modify: `src-tauri/src/lib.rs`（声明模块）
- Modify: `src-tauri/Cargo.toml`（新增依赖）

**Interfaces:**
- Consumes: `AppError`（Task 2）。
- Produces:
  - `pub const SALT_LEN: usize = 16; pub const NONCE_LEN: usize = 12; pub const TAG_LEN: usize = 16; pub const KEY_LEN: usize = 32;`
  - `pub fn random_bytes<const N: usize>() -> [u8; N]`
  - `pub fn derive_key(password: &[u8], salt: &[u8]) -> AppResult<[u8; KEY_LEN]>`
  - `pub fn encrypt(key: &[u8; KEY_LEN], nonce: &[u8; NONCE_LEN], plaintext: &[u8]) -> AppResult<Vec<u8>>`（返回 `ciphertext || tag`）
  - `pub fn decrypt(key: &[u8; KEY_LEN], nonce: &[u8; NONCE_LEN], ciphertext: &[u8]) -> AppResult<Vec<u8>>`（`ciphertext` 含 tag，失败返回 `InvalidPassword`）

- [ ] **Step 1: 添加依赖到 Cargo.toml**

```toml
aes-gcm = "0.10"
argon2 = "0.5"
rand = "0.8"
zeroize = "1"
```

- [ ] **Step 2: 写失败测试**

`src-tauri/src/crypto.rs` 内 `#[cfg(test)]`：

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_encrypt_decrypt() {
        let key = derive_key(b"password", &[0u8; SALT_LEN]).unwrap();
        let nonce = [7u8; NONCE_LEN];
        let plaintext = b"hello, mini-WinRAR!".to_vec();
        let ct = encrypt(&key, &nonce, &plaintext).unwrap();
        assert_eq!(ct.len(), plaintext.len() + TAG_LEN);
        let pt = decrypt(&key, &nonce, &ct).unwrap();
        assert_eq!(pt, plaintext);
    }

    #[test]
    fn wrong_password_fails() {
        let k1 = derive_key(b"right", &[1u8; SALT_LEN]).unwrap();
        let k2 = derive_key(b"wrong", &[1u8; SALT_LEN]).unwrap();
        let nonce = [9u8; NONCE_LEN];
        let ct = encrypt(&k1, &nonce, b"data").unwrap();
        assert!(decrypt(&k2, &nonce, &ct).is_err());
    }

    #[test]
    fn derive_key_is_deterministic_and_32_bytes() {
        let a = derive_key(b"pw", &[2u8; SALT_LEN]).unwrap();
        let b = derive_key(b"pw", &[2u8; SALT_LEN]).unwrap();
        assert_eq!(a, b);
        let c = derive_key(b"pw", &[3u8; SALT_LEN]).unwrap();
        assert_ne!(a, c);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
cd src-tauri && cargo test crypto
```

Expected: FAIL（`derive_key` 等未定义）。

- [ ] **Step 4: 实现 crypto.rs**

```rust
use aes_gcm::{
    aead::{Aead, KeyInit},
    Aes256Gcm, Nonce,
};
use argon2::{Algorithm, Argon2, Params, Version};
use rand::RngCore;
use zeroize::Zeroize;

use crate::error::{AppError, AppResult};

pub const SALT_LEN: usize = 16;
pub const NONCE_LEN: usize = 12;
pub const TAG_LEN: usize = 16;
pub const KEY_LEN: usize = 32;

pub fn random_bytes<const N: usize>() -> [u8; N] {
    let mut buf = [0u8; N];
    rand::rngs::OsRng.fill_bytes(&mut buf);
    buf
}

pub fn derive_key(password: &[u8], salt: &[u8]) -> AppResult<[u8; KEY_LEN]> {
    let params = Params::new(65536, 3, 4, Some(KEY_LEN))
        .map_err(|e| AppError::Io(e.to_string()))?;
    let argon2 = Argon2::new(Algorithm::Argon2id, Version::V0x13, params);
    let mut key = [0u8; KEY_LEN];
    argon2
        .hash_password_into(password, salt, &mut key)
        .map_err(|e| AppError::Io(e.to_string()))?;
    Ok(key)
}

pub fn encrypt(key: &[u8; KEY_LEN], nonce: &[u8; NONCE_LEN], plaintext: &[u8]) -> AppResult<Vec<u8>> {
    let cipher = Aes256Gcm::new_from_slice(key).map_err(|e| AppError::Io(e.to_string()))?;
    cipher
        .encrypt(Nonce::from_slice(nonce), plaintext)
        .map_err(|e| AppError::Io(e.to_string()))
}

pub fn decrypt(key: &[u8; KEY_LEN], nonce: &[u8; NONCE_LEN], ciphertext: &[u8]) -> AppResult<Vec<u8>> {
    let cipher = Aes256Gcm::new_from_slice(key).map_err(|e| AppError::Io(e.to_string()))?;
    cipher
        .decrypt(Nonce::from_slice(nonce), ciphertext)
        .map_err(|_| AppError::InvalidPassword)
}

pub fn zeroize_key(key: &mut [u8; KEY_LEN]) {
    key.zeroize();
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
cd src-tauri && cargo test crypto
```

Expected: PASS（3 个测试）。

- [ ] **Step 6: 提交**

```bash
git add src-tauri/src/crypto.rs src-tauri/src/lib.rs src-tauri/Cargo.toml
git commit -m "feat: add AES-GCM + Argon2id crypto module"
```

---

## Task 4: fs.rs（路径展开与文件收集）

**Files:**
- Create: `src-tauri/src/fs.rs`
- Modify: `src-tauri/src/lib.rs`（声明模块）

**Interfaces:**
- Consumes: `AppError`（Task 2）。
- Produces:
  - `pub struct FileEntry { pub name: String; pub path: PathBuf; pub is_dir: bool; pub size: u64; pub mtime: u64; }`
  - `pub fn collect_entries(paths: &[PathBuf]) -> AppResult<Vec<FileEntry>>`：展开输入路径（文件直接收集、目录递归），`name` 用 `/` 分隔的相对路径；输入为目录时，目录本身作为 `is_dir=true` 条目 + 其内容。

- [ ] **Step 1: 写失败测试（用临时目录构造真实文件）**

`src-tauri/src/fs.rs` 内 `#[cfg(test)]`：

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn tmp() -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("mwr_fs_{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(d.join("sub")).unwrap();
        d
    }

    #[test]
    fn collects_file_and_recurses_dir() {
        let base = tmp();
        let f = base.join("a.txt");
        fs::write(&f, b"hello").unwrap();
        let sub = base.join("sub").join("b.txt");
        fs::write(&sub, b"world").unwrap();

        let entries = collect_entries(&[base.clone()]).unwrap();
        let names: Vec<_> = entries.iter().map(|e| e.name.as_str()).collect();
        // 目录本身 + a.txt + sub/b.txt（sub 目录条目可能也出现）
        assert!(names.iter().any(|n| *n == "a.txt"));
        assert!(names.iter().any(|n| n.ends_with("sub/b.txt")));
        let dir = entries.iter().find(|e| e.is_dir).expect("dir entry");
        assert!(dir.is_dir);
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
cd src-tauri && cargo test fs
```

Expected: FAIL。

- [ ] **Step 3: 实现 fs.rs**

```rust
use std::fs;
use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;

use crate::error::{AppError, AppResult};

#[derive(Debug, Clone)]
pub struct FileEntry {
    pub name: String,
    pub path: PathBuf,
    pub is_dir: bool,
    pub size: u64,
    pub mtime: u64,
}

fn mtime_secs(md: &fs::Metadata) -> u64 {
    md.modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

pub fn collect_entries(paths: &[PathBuf]) -> AppResult<Vec<FileEntry>> {
    let mut out = Vec::new();
    for p in paths {
        let md = fs::metadata(p).map_err(|_| AppError::FileNotFound)?;
        if md.is_dir() {
            let base_name = p.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            out.push(FileEntry { name: base_name, path: p.clone(), is_dir: true, size: 0, mtime: mtime_secs(&md) });
            walk_dir(p, &base_name, &mut out)?;
        } else {
            let name = p.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            out.push(FileEntry { name, path: p.clone(), is_dir: false, size: md.len(), mtime: mtime_secs(&md) });
        }
    }
    Ok(out)
}

fn walk_dir(dir: &Path, prefix: &str, out: &mut Vec<FileEntry>) -> AppResult<()> {
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let path = entry.path();
        let md = entry.metadata()?;
        let rel = format!("{}/{}", prefix, entry.file_name().to_string_lossy());
        if md.is_dir() {
            out.push(FileEntry { name: rel.clone(), path: path.clone(), is_dir: true, size: 0, mtime: mtime_secs(&md) });
            walk_dir(&path, &rel, out)?;
        } else {
            out.push(FileEntry { name: rel, path, is_dir: false, size: md.len(), mtime: mtime_secs(&md) });
        }
    }
    Ok(())
}
```

- [ ] **Step 4: 运行确认通过**

```bash
cd src-tauri && cargo test fs
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src-tauri/src/fs.rs src-tauri/src/lib.rs
git commit -m "feat: add filesystem entry collection"
```

---

## Task 5: mwr/format.rs（格式常量与元数据序列化）

**Files:**
- Create: `src-tauri/src/mwr/mod.rs`
- Create: `src-tauri/src/mwr/format.rs`
- Modify: `src-tauri/src/lib.rs`（声明 `mwr` 模块）
- Modify: `src-tauri/Cargo.toml`（新增 `serde`、`bincode`、`crc32fast`）

**Interfaces:**
- Consumes: `crypto` 常量（`NONCE_LEN`、`SALT_LEN`，Task 3）。
- Produces:
  - 常量：`pub const MAGIC: [u8; 4] = *b"MWR1"; pub const VERSION: u8 = 1; pub const FLAG_ENCRYPTED: u8 = 0x01;`
  - `pub struct EntryMeta { pub name: String; pub uncompressed_size: u64; pub compressed_size: u64; pub mtime: u64; pub is_dir: bool; pub data_offset: u64; pub nonce: [u8; 12]; pub crc32: u32; }`（`Serialize` + `Deserialize`）
  - `pub fn serialize_entries(&[EntryMeta]) -> AppResult<Vec<u8>>`（bincode）
  - `pub fn deserialize_entries(&[u8]) -> AppResult<Vec<EntryMeta>>`

**格式布局说明（实现采用，header 在末尾）：**

```
偏移 0:  Magic(4B) Version(1B) Flags(1B) Salt(16B)   ← 固定 22 字节头
偏移 22: 条目数据区（每个条目: [Nonce 12B(仅加密)][数据(加密时含 tag)]）
末尾:    Header 区 = [HeaderNonce 12B(仅加密)][Header 明文/密文][HeaderLen 8B u64 LE]
```

`data_offset` 从文件偏移 0 起算。

- [ ] **Step 1: 加依赖**

```toml
serde = { version = "1", features = ["derive"] }
bincode = "1.3"
crc32fast = "1"
```

- [ ] **Step 2: 写失败测试**

`format.rs` 内：

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn entry_meta_roundtrips_through_bincode() {
        let entries = vec![
            EntryMeta { name: "a/中文.txt".into(), uncompressed_size: 10, compressed_size: 5, mtime: 123, is_dir: false, data_offset: 22, nonce: [1; 12], crc32: 99 },
            EntryMeta { name: "dir".into(), uncompressed_size: 0, compressed_size: 0, mtime: 1, is_dir: true, data_offset: 0, nonce: [0; 12], crc32: 0 },
        ];
        let bytes = serialize_entries(&entries).unwrap();
        let back = deserialize_entries(&bytes).unwrap();
        assert_eq!(back.len(), 2);
        assert_eq!(back[0].name, "a/中文.txt");
        assert!(back[1].is_dir);
    }

    #[test]
    fn constants_are_stable() {
        assert_eq!(&MAGIC, b"MWR1");
        assert_eq!(VERSION, 1);
        assert_eq!(FLAG_ENCRYPTED, 0x01);
    }
}
```

- [ ] **Step 3: 运行确认失败**

```bash
cd src-tauri && cargo test mwr
```

Expected: FAIL。

- [ ] **Step 4: 实现 format.rs**

```rust
use serde::{Deserialize, Serialize};

use crate::crypto::NONCE_LEN;
use crate::error::{AppError, AppResult};

pub const MAGIC: [u8; 4] = *b"MWR1";
pub const VERSION: u8 = 1;
pub const FLAG_ENCRYPTED: u8 = 0x01;
pub const FIXED_HEADER_LEN: u64 = 4 + 1 + 1 + 16; // 22

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct EntryMeta {
    pub name: String,
    pub uncompressed_size: u64,
    pub compressed_size: u64,
    pub mtime: u64,
    pub is_dir: bool,
    pub data_offset: u64,
    pub nonce: [u8; NONCE_LEN],
    pub crc32: u32,
}

pub fn serialize_entries(entries: &[EntryMeta]) -> AppResult<Vec<u8>> {
    bincode::serialize(entries).map_err(|e| AppError::Io(e.to_string()))
}

pub fn deserialize_entries(bytes: &[u8]) -> AppResult<Vec<EntryMeta>> {
    bincode::deserialize(bytes).map_err(|e| AppError::ArchiveCorrupted)
}
```

`mod.rs`:

```rust
pub mod format;
pub mod writer;
pub mod reader;
```

（writer/reader 在后续 Task 创建，`mod.rs` 先完整声明，`cargo` 会因缺文件报错，故本 Task 的 Step 4 实际只创建 `format.rs`，`mod.rs` 暂只声明 `format`，writer/reader 行在对应 Task 再补——见下。）

**修正 Step 4**：`mod.rs` 初始内容仅 `pub mod format;`，后续 Task 6/7 各自追加 `pub mod writer;` / `pub mod reader;`。

- [ ] **Step 5: 运行确认通过**

```bash
cd src-tauri && cargo test mwr
```

Expected: PASS（2 个测试）。

- [ ] **Step 6: 提交**

```bash
git add src-tauri/src/mwr/ src-tauri/src/lib.rs src-tauri/Cargo.toml
git commit -m "feat: add mwr format constants and EntryMeta serialization"
```

---

## Task 6: mwr/writer.rs（写入 .mwr）

**Files:**
- Create: `src-tauri/src/mwr/writer.rs`
- Modify: `src-tauri/src/mwr/mod.rs`（追加 `pub mod writer;`）
- Modify: `src-tauri/Cargo.toml`（新增 `zstd`）

**Interfaces:**
- Consumes: `EntryMeta`/`serialize_entries`（Task 5）、`crypto::{derive_key, encrypt, random_bytes}`（Task 3）、`CompressionLevel`（Task 2）。
- Produces:
  - `pub fn zstd_level(level: CompressionLevel) -> i32`（Store→0, Fast→3, Best→19）
  - `pub struct MwrWriter<W: Write + Seek>`，方法：
    - `pub fn create(w: W, password: Option<&str>) -> AppResult<Self>`
    - `pub fn add_file(&mut self, name: &str, data: &[u8], mtime: u64, level: CompressionLevel) -> AppResult<()>`
    - `pub fn add_dir(&mut self, name: &str, mtime: u64) -> AppResult<()>`
    - `pub fn finish(self) -> AppResult<()>`

- [ ] **Step 1: 加依赖**

```toml
zstd = "0.13"
```

- [ ] **Step 2: 写失败测试（round-trip 依赖 reader，故此处测「写入后可读回结构」——先测 `zstd_level` 与「写入到 Vec 后前 22 字节为固定头」）**

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn zstd_level_mapping() {
        assert_eq!(zstd_level(CompressionLevel::Store), 0);
        assert_eq!(zstd_level(CompressionLevel::Fast), 3);
        assert_eq!(zstd_level(CompressionLevel::Best), 19);
    }

    #[test]
    fn writes_fixed_header() {
        let buf = Cursor::new(Vec::new());
        let mut w = MwrWriter::create(buf, None).unwrap();
        w.add_file("a.txt", b"hello", 0, CompressionLevel::Fast).unwrap();
        w.finish().unwrap();
        let bytes = w.into_inner(); // 见实现：提供 into_inner 或 finish 返回 W
        assert_eq!(&bytes[0..4], b"MWR1");
        assert_eq!(bytes[4], 1);
        assert_eq!(bytes[5], 0); // 非加密
    }
}
```

- [ ] **Step 3: 运行确认失败**

```bash
cd src-tauri && cargo test mwr::writer
```

Expected: FAIL。

- [ ] **Step 4: 实现 writer.rs**

```rust
use std::io::{Seek, SeekFrom, Write};

use crate::crypto::{derive_key, encrypt, random_bytes, KEY_LEN, NONCE_LEN, SALT_LEN};
use crate::error::{AppError, AppResult};
use crate::types::CompressionLevel;
use super::format::{serialize_entries, EntryMeta, FIXED_HEADER_LEN, FLAG_ENCRYPTED, MAGIC, VERSION};

pub fn zstd_level(level: CompressionLevel) -> i32 {
    match level {
        CompressionLevel::Store => 0,
        CompressionLevel::Fast => 3,
        CompressionLevel::Best => 19,
    }
}

pub struct MwrWriter<W: Write + Seek> {
    inner: W,
    key: Option<[u8; KEY_LEN]>,   // 派生后的主密钥
    salt: [u8; SALT_LEN],
    entries: Vec<EntryMeta>,
    offset: u64,                   // 当前数据写入位置（绝对偏移）
}

impl<W: Write + Seek> MwrWriter<W> {
    pub fn create(mut inner: W, password: Option<&str>) -> AppResult<Self> {
        let salt = random_bytes::<SALT_LEN>();
        let key = password.map(|pw| derive_key(pw.as_bytes(), &salt)).transpose()?;
        let flags = if key.is_some() { FLAG_ENCRYPTED } else { 0 };
        inner.write_all(&MAGIC)?;
        inner.write_all(&[VERSION, flags])?;
        inner.write_all(&salt)?;
        let offset = FIXED_HEADER_LEN;
        Ok(Self { inner, key, salt, entries: Vec::new(), offset })
    }

    pub fn add_dir(&mut self, name: &str, mtime: u64) -> AppResult<()> {
        self.entries.push(EntryMeta {
            name: name.to_string(), uncompressed_size: 0, compressed_size: 0,
            mtime, is_dir: true, data_offset: self.offset, nonce: [0; NONCE_LEN], crc32: 0,
        });
        Ok(())
    }

    pub fn add_file(&mut self, name: &str, data: &[u8], mtime: u64, level: CompressionLevel) -> AppResult<()> {
        let compressed = zstd::bulk::compress(data, zstd_level(level))
            .map_err(|e| AppError::Io(e.to_string()))?;
        let crc32 = crc32fast::hash(data);
        let (nonce, payload) = match &self.key {
            Some(k) => {
                let n = random_bytes::<NONCE_LEN>();
                let ct = encrypt(k, &n, &compressed)?;
                (n, ct)
            }
            None => ([0u8; NONCE_LEN], compressed),
        };
        let start = self.offset;
        if self.key.is_some() {
            self.inner.write_all(&nonce)?;
            self.offset += NONCE_LEN as u64;
        }
        self.inner.write_all(&payload)?;
        self.offset += payload.len() as u64;
        self.entries.push(EntryMeta {
            name: name.to_string(), uncompressed_size: data.len() as u64,
            compressed_size: payload.len() as u64, mtime, is_dir: false,
            data_offset: start, nonce, crc32,
        });
        Ok(())
    }

    pub fn finish(mut self) -> AppResult<W> {
        let header_plain = serialize_entries(&self.entries)?;
        let header_start = self.offset;
        let header_payload = match &self.key {
            Some(k) => {
                let n = random_bytes::<NONCE_LEN>();
                self.inner.write_all(&n)?;
                let ct = encrypt(k, &n, &header_plain)?;
                (n, ct)
            }
            None => ([0u8; NONCE_LEN], header_plain),
        };
        if self.key.is_some() {
            // header_payload.0 已写入
        }
        let hdr_len = (if self.key.is_some() { NONCE_LEN as u64 } else { 0 }) + header_payload.1.len() as u64;
        // 注意：上面已写 nonce，这里再写密文/明文
        if self.key.is_none() {
            self.inner.write_all(&header_payload.1)?;
        }
        // 统一：密文分支的 nonce 已写，此处写密文体；明文分支写明文
        // 简化重写（见下）
        let _ = header_start;
        let _ = hdr_len;
        Ok(self.inner)
    }
}
```

> **注意**：`finish` 的分支逻辑易错。执行时请按以下明确顺序重写 `finish`（替代上面占位）：
>
> ```rust
> pub fn finish(mut self) -> AppResult<W> {
>     let header_plain = serialize_entries(&self.entries)?;
>     match self.key.take() {
>         Some(k) => {
>             let n = random_bytes::<NONCE_LEN>();
>             let ct = encrypt(&k, &n, &header_plain)?;
>             self.inner.write_all(&n)?;
>             self.inner.write_all(&ct)?;
>             let len = (NONCE_LEN + ct.len()) as u64;
>             self.inner.write_all(&len.to_le_bytes())?;
>         }
>         None => {
>             self.inner.write_all(&header_plain)?;
>             let len = header_plain.len() as u64;
>             self.inner.write_all(&len.to_le_bytes())?;
>         }
>     }
>     Ok(self.inner)
> }
> ```
>
> `writes_fixed_header` 测试里的 `w.into_inner()` 改为：`let bytes = w.finish().unwrap().into_inner();`（`finish` 返回 `W`）。

- [ ] **Step 5: 运行确认通过**

```bash
cd src-tauri && cargo test mwr::writer
```

Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add src-tauri/src/mwr/writer.rs src-tauri/src/mwr/mod.rs src-tauri/Cargo.toml
git commit -m "feat: add mwr writer"
```

---

## Task 7: mwr/reader.rs（读取 .mwr）

**Files:**
- Create: `src-tauri/src/mwr/reader.rs`
- Modify: `src-tauri/src/mwr/mod.rs`（追加 `pub mod reader;`）

**Interfaces:**
- Consumes: `EntryMeta`/`deserialize_entries`（Task 5）、`crypto::{derive_key, decrypt}`（Task 3）、`MwrWriter`（Task 6，用于测试）。
- Produces:
  - `pub struct MwrReader<R: Read + Seek>`，方法：
    - `pub fn open(r: R, password: Option<&str>) -> AppResult<Self>`（解析头与 header；密码错误返回 `InvalidPassword`）
    - `pub fn entries(&self) -> &[EntryMeta]`
    - `pub fn is_encrypted(&self) -> bool`
    - `pub fn read_file(&mut self, idx: usize) -> AppResult<Vec<u8>>`（解密 + 解压 + CRC 校验）

- [ ] **Step 1: 写失败测试（含 writer→reader round-trip 与错密码）**

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crate::mwr::writer::MwrWriter;
    use crate::types::CompressionLevel;
    use std::io::Cursor;

    fn write_archive(password: Option<&str>) -> Vec<u8> {
        let mut w = MwrWriter::create(Cursor::new(Vec::new()), password).unwrap();
        w.add_file("a.txt", b"hello world", 100, CompressionLevel::Fast).unwrap();
        w.add_dir("sub", 100).unwrap();
        w.add_file("sub/b.bin", &[0,1,2,3,4], 101, CompressionLevel::Store).unwrap();
        w.finish().unwrap().into_inner()
    }

    #[test]
    fn roundtrip_unencrypted() {
        let bytes = write_archive(None);
        let mut r = MwrReader::open(Cursor::new(bytes), None).unwrap();
        assert!(!r.is_encrypted());
        assert_eq!(r.entries().len(), 3);
        assert_eq!(r.read_file(0).unwrap(), b"hello world");
        assert!(r.entries()[1].is_dir);
        assert_eq!(r.read_file(2).unwrap(), &[0,1,2,3,4]);
    }

    #[test]
    fn roundtrip_encrypted_and_wrong_password() {
        let bytes = write_archive(Some("secret"));
        let mut r = MwrReader::open(Cursor::new(bytes.clone()), Some("secret")).unwrap();
        assert!(r.is_encrypted());
        assert_eq!(r.read_file(0).unwrap(), b"hello world");
        assert!(MwrReader::open(Cursor::new(bytes), Some("wrong")).is_err());
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
cd src-tauri && cargo test mwr::reader
```

Expected: FAIL。

- [ ] **Step 3: 实现 reader.rs**

```rust
use std::io::{Read, Seek, SeekFrom};

use crate::crypto::{derive_key, decrypt, KEY_LEN, NONCE_LEN, SALT_LEN};
use crate::error::{AppError, AppResult};
use super::format::{deserialize_entries, EntryMeta, FIXED_HEADER_LEN, FLAG_ENCRYPTED, MAGIC, VERSION};

pub struct MwrReader<R: Read + Seek> {
    inner: R,
    key: Option<[u8; KEY_LEN]>,
    entries: Vec<EntryMeta>,
    encrypted: bool,
}

impl<R: Read + Seek> MwrReader<R> {
    pub fn open(mut inner: R, password: Option<&str>) -> AppResult<Self> {
        let mut magic = [0u8; 4];
        inner.read_exact(&mut magic)?;
        if magic != MAGIC { return Err(AppError::UnsupportedFormat); }
        let mut vf = [0u8; 2];
        inner.read_exact(&mut vf)?;
        if vf[0] != VERSION { return Err(AppError::UnsupportedFormat); }
        let encrypted = vf[1] & FLAG_ENCRYPTED != 0;
        let mut salt = [0u8; SALT_LEN];
        inner.read_exact(&mut salt)?;
        let key = if encrypted {
            let pw = password.ok_or(AppError::InvalidPassword)?;
            Some(derive_key(pw.as_bytes(), &salt)?)
        } else { None };

        // 读末尾 HeaderLen
        let end = inner.seek(SeekFrom::End(0))?;
        inner.seek(SeekFrom::End(-8))?;
        let mut lenbuf = [0u8; 8];
        inner.read_exact(&mut lenbuf)?;
        let header_len = u64::from_le_bytes(lenbuf);
        let header_start = end - 8 - header_len;
        inner.seek(SeekFrom::Start(header_start))?;

        let header_plain = if encrypted {
            let k = key.as_ref().unwrap();
            let mut n = [0u8; NONCE_LEN];
            inner.read_exact(&mut n)?;
            let mut ct = vec![0u8; (header_len - NONCE_LEN as u64) as usize];
            inner.read_exact(&mut ct)?;
            decrypt(k, &n, &ct)?
        } else {
            let mut buf = vec![0u8; header_len as usize];
            inner.read_exact(&mut buf)?;
            buf
        };
        let entries = deserialize_entries(&header_plain)?;
        Ok(Self { inner, key, entries, encrypted })
    }

    pub fn entries(&self) -> &[EntryMeta] { &self.entries }
    pub fn is_encrypted(&self) -> bool { self.encrypted }

    pub fn read_file(&mut self, idx: usize) -> AppResult<Vec<u8>> {
        let meta = self.entries.get(idx).ok_or(AppError::ArchiveCorrupted)?.clone();
        if meta.is_dir { return Err(AppError::Io("不是文件".into())); }
        self.inner.seek(SeekFrom::Start(meta.data_offset))?;
        let compressed = if self.encrypted {
            let k = self.key.as_ref().ok_or(AppError::InvalidPassword)?;
            let mut n = [0u8; NONCE_LEN];
            self.inner.read_exact(&mut n)?;
            let mut ct = vec![0u8; meta.compressed_size as usize];
            self.inner.read_exact(&mut ct)?;
            decrypt(k, &n, &ct)?
        } else {
            let mut buf = vec![0u8; meta.compressed_size as usize];
            self.inner.read_exact(&mut buf)?;
            buf
        };
        let data = zstd::bulk::decompress(&compressed, meta.uncompressed_size as usize)
            .map_err(|_| AppError::ArchiveCorrupted)?;
        if crc32fast::hash(&data) != meta.crc32 {
            return Err(AppError::ArchiveCorrupted);
        }
        Ok(data)
    }
}
```

- [ ] **Step 4: 运行确认通过**

```bash
cd src-tauri && cargo test mwr::reader
```

Expected: PASS（2 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src-tauri/src/mwr/reader.rs src-tauri/src/mwr/mod.rs
git commit -m "feat: add mwr reader with roundtrip tests"
```

---

## Task 8: zip.rs（ZIP 压缩/解压/列出）

**Files:**
- Create: `src-tauri/src/zip.rs`
- Modify: `src-tauri/src/lib.rs`（声明模块）
- Modify: `src-tauri/Cargo.toml`（新增 `zip`）

**Interfaces:**
- Consumes: `CompressionLevel`、`ArchiveEntry`、`ArchiveStats`（Task 2）、`FileEntry`（Task 4）。
- Produces:
  - `pub fn compress_zip(target: &Path, files: &[FileEntry], level: CompressionLevel, password: Option<&str>, mut on_progress: impl FnMut(&str, u8)) -> AppResult<ArchiveStats>`
  - `pub fn list_zip(path: &Path, password: Option<&str>) -> AppResult<Vec<ArchiveEntry>>`
  - `pub fn extract_zip(path: &Path, target_dir: &Path, password: Option<&str>, filter: Option<&[String]>, mut on_progress: impl FnMut(&str, u8)) -> AppResult<ArchiveStats>`

- [ ] **Step 1: 加依赖**

```toml
zip = { version = "2", default-features = false, features = ["deflate", "aes-crypto"] }
```

- [ ] **Step 2: 写失败测试（round-trip：压缩到临时文件 → list → extract → 内容一致）**

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crate::fs::FileEntry;
    use std::fs;

    fn tmp(name: &str) -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("mwr_zip_{}_{}", name, std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn roundtrip_compress_list_extract() {
        let dir = tmp("roundtrip");
        let src = dir.join("a.txt");
        fs::write(&src, b"zip content").unwrap();
        let target = dir.join("out.zip");

        let fe = FileEntry { name: "a.txt".into(), path: src, is_dir: false, size: 11, mtime: 0 };
        let stats = compress_zip(&target, &[fe], CompressionLevel::Fast, None, |_, _| {}).unwrap();
        assert_eq!(stats.entry_count, 1);

        let list = list_zip(&target, None).unwrap();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].name, "a.txt");

        let outdir = dir.join("extracted");
        fs::create_dir_all(&outdir).unwrap();
        let s2 = extract_zip(&target, &outdir, None, None, |_, _| {}).unwrap();
        assert_eq!(s2.entry_count, 1);
        assert_eq!(fs::read(outdir.join("a.txt")).unwrap(), b"zip content");
    }
}
```

- [ ] **Step 3: 运行确认失败**

```bash
cd src-tauri && cargo test zip
```

Expected: FAIL。

- [ ] **Step 4: 实现 zip.rs**

```rust
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::Path;

use zip::aes::AesMode;
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipArchive, ZipWriter};

use crate::error::{AppError, AppResult};
use crate::fs::FileEntry;
use crate::types::{ArchiveEntry, ArchiveStats, CompressionLevel};

fn zip_method(level: CompressionLevel) -> (CompressionMethod, Option<i64>) {
    match level {
        CompressionLevel::Store => (CompressionMethod::Stored, None),
        CompressionLevel::Fast => (CompressionMethod::Deflated, Some(1)),
        CompressionLevel::Best => (CompressionMethod::Deflated, Some(9)),
    }
}

pub fn compress_zip(
    target: &Path,
    files: &[FileEntry],
    level: CompressionLevel,
    password: Option<&str>,
    mut on_progress: impl FnMut(&str, u8),
) -> AppResult<ArchiveStats> {
    let file = File::create(target)?;
    let mut zw = ZipWriter::new(file);
    let (method, lvl) = zip_method(level);
    let total = files.len().max(1);
    let mut size_sum = 0u64;
    for (i, fe) in files.iter().enumerate() {
        on_progress(&fe.name, ((i as u64 + 1) * 100 / total as u64) as u8);
        let mut opts = SimpleFileOptions::default().compression_method(method).unix_permissions(0o644);
        if let Some(l) = lvl { opts = opts.compression_level(Some(l)); }
        if fe.is_dir {
            zw.add_directory(&fe.name, opts)?;
        } else {
            if let Some(pw) = password {
                opts = opts.aes_encryption(AesMode::Aes256, pw.as_bytes());
            }
            zw.start_file(&fe.name, opts)?;
            let mut f = File::open(&fe.path)?;
            let mut buf = Vec::new();
            f.read_to_end(&mut buf)?;
            zw.write_all(&buf)?;
            size_sum += buf.len() as u64;
        }
    }
    let f = zw.finish()?;
    let size = f.metadata()?.len();
    Ok(ArchiveStats { entry_count: files.len() as u64, total_size: size_sum, target_path: target.display().to_string() })
}

pub fn list_zip(path: &Path, password: Option<&str>) -> AppResult<Vec<ArchiveEntry>> {
    let f = File::open(path)?;
    let mut za = ZipArchive::new(f).map_err(|_| AppError::UnsupportedFormat)?;
    let mut out = Vec::new();
    for i in 0..za.len() {
        let mut entry = za.by_index(i).map_err(|_| AppError::ArchiveCorrupted)?;
        if entry.is_encrypted() {
            entry.set_password(password.unwrap_or("").as_bytes()).map_err(|_| AppError::InvalidPassword)?;
        }
        out.push(ArchiveEntry {
            name: entry.name().to_string(),
            size: entry.size(),
            is_dir: entry.is_dir(),
            mtime: entry.last_modified().map(|d| d.timestamp() as u64).unwrap_or(0),
            is_encrypted: entry.is_encrypted(),
        });
    }
    Ok(out)
}

pub fn extract_zip(
    path: &Path,
    target_dir: &Path,
    password: Option<&str>,
    filter: Option<&[String]>,
    mut on_progress: impl FnMut(&str, u8),
) -> AppResult<ArchiveStats> {
    let f = File::open(path)?;
    let mut za = ZipArchive::new(f).map_err(|_| AppError::UnsupportedFormat)?;
    let total = za.len().max(1);
    let mut count = 0u64;
    let mut size_sum = 0u64;
    for i in 0..za.len() {
        let mut entry = za.by_index(i).map_err(|_| AppError::ArchiveCorrupted)?;
        let name = entry.name().to_string();
        on_progress(&name, ((i as u64 + 1) * 100 / total as u64) as u8);
        if let Some(filter) = filter {
            if !filter.iter().any(|f| f == &name) { continue; }
        }
        if entry.is_encrypted() {
            entry.set_password(password.unwrap_or("").as_bytes()).map_err(|_| AppError::InvalidPassword)?;
        }
        let out_path = target_dir.join(&name);
        if entry.is_dir() {
            fs::create_dir_all(&out_path)?;
        } else {
            if let Some(p) = out_path.parent() { fs::create_dir_all(p)?; }
            let mut out = File::create(&out_path)?;
            let mut buf = Vec::new();
            entry.read_to_end(&mut buf)?;
            out.write_all(&buf)?;
            size_sum += buf.len() as u64;
        }
        count += 1;
    }
    Ok(ArchiveStats { entry_count: count, total_size: size_sum, target_path: target_dir.display().to_string() })
}
```

> 若 `zip` 2.x 的 API 名（如 `aes_encryption`、`unix_permissions`、`set_password`）在编译时报错，以 `cargo doc -p zip` 或 Context7 查到的当前版本签名为准修正；核心流程（start_file→write→finish / by_index→read_to_end）不变。

- [ ] **Step 5: 运行确认通过**

```bash
cd src-tauri && cargo test zip
```

Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add src-tauri/src/zip.rs src-tauri/src/lib.rs src-tauri/Cargo.toml
git commit -m "feat: add ZIP compress/list/extract"
```

---

## Task 9: commands.rs（Tauri command 接口层）

**Files:**
- Create: `src-tauri/src/commands.rs`
- Modify: `src-tauri/src/lib.rs`（注册 `invoke_handler`，声明模块）

**Interfaces:**
- Consumes: `zip`（Task 8）、`mwr::{writer, reader}`（Task 6/7）、`fs`（Task 4）、`types`（Task 2）、`error`（Task 2）。
- Produces（4 个 `#[tauri::command]`，返回 `Result<T, String>`，内部把 `AppError` 转 `String`）：
  - `pub fn compress(app: AppHandle, paths: Vec<String>, format: String, level: CompressionLevel, password: Option<String>) -> Result<ArchiveStats, String>`
  - `pub fn list_archive(path: String, password: Option<String>) -> Result<Vec<ArchiveEntry>, String>`
  - `pub fn extract(app: AppHandle, path: String, target_dir: String, password: Option<String>, filter: Option<Vec<String>>) -> Result<ArchiveStats, String>`
  - `pub fn preview_file(path: String, name: String, password: Option<String>) -> Result<PreviewResult, String>`

  新增 `pub struct PreviewResult { pub kind: String, pub text: Option<String>, pub bytes: Option<Vec<u8>> }`（`kind`: "text" | "image" | "binary"）。
  进度事件：`app.emit("compress://progress", payload)` / `app.emit("extract://progress", payload)`，`payload = { name: String, pct: u8 }`。

- [ ] **Step 1: 写 `compress` 的失败测试（走非 Tauri 路径，抽出内部纯函数）**

为可测试，把核心分发放进纯函数 `fn do_compress(paths, format, level, password, on_progress) -> AppResult<ArchiveStats>`，command 只做类型转换与事件发射。测试针对 `do_compress`：

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn do_compress_mwr_creates_file() {
        let dir = std::env::temp_dir().join(format!("mwr_cmd_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("x.txt");
        std::fs::write(&src, b"data").unwrap();
        let target = dir.join("out.mwr");
        let paths = vec![src.to_string_lossy().into_owned()];
        let stats = do_compress(&paths, "mwr".into(), CompressionLevel::Fast, None, |_, _| {}).unwrap();
        assert!(target.exists());
        assert_eq!(stats.entry_count, 1);
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
cd src-tauri && cargo test commands
```

Expected: FAIL。

- [ ] **Step 3: 实现 commands.rs**

```rust
use std::path::PathBuf;

use serde::Serialize;
use tauri::{AppHandle, Emitter};

use crate::error::{AppError, AppResult};
use crate::fs::collect_entries;
use crate::mwr::{reader::MwrReader, writer::MwrWriter};
use crate::types::{ArchiveEntry, ArchiveStats, CompressionLevel};
use crate::zip;

#[derive(Debug, Serialize)]
pub struct PreviewResult {
    pub kind: String,
    pub text: Option<String>,
    pub bytes: Option<Vec<u8>>,
}

#[derive(Clone, Serialize)]
struct ProgressPayload { name: String, pct: u8 }

fn err_str(e: AppError) -> String { e.to_string() }

fn do_compress(
    paths: &[String], format: &str, level: CompressionLevel, password: Option<&str>,
    mut on_progress: impl FnMut(&str, u8),
) -> AppResult<ArchiveStats> {
    let paths: Vec<PathBuf> = paths.iter().map(PathBuf::from).collect();
    let entries = collect_entries(&paths)?;
    // 目标路径：同目录下，按首个输入命名
    let base = PathBuf::from(&paths[0]);
    let target = base.with_extension(if format == "mwr" { "mwr" } else { "zip" });

    if format == "mwr" {
        let file = std::fs::File::create(&target)?;
        let mut w = MwrWriter::create(file, password)?;
        let total = entries.len().max(1);
        for (i, e) in entries.iter().enumerate() {
            on_progress(&e.name, ((i as u64 + 1) * 100 / total as u64) as u8);
            if e.is_dir {
                w.add_dir(&e.name, e.mtime)?;
            } else {
                let data = std::fs::read(&e.path)?;
                w.add_file(&e.name, &data, e.mtime, level)?;
            }
        }
        w.finish()?;
        Ok(ArchiveStats { entry_count: entries.len() as u64, total_size: 0, target_path: target.display().to_string() })
    } else {
        zip::compress_zip(&target, &entries, level, password, on_progress)
    }
}

#[tauri::command]
pub fn compress(
    app: AppHandle, paths: Vec<String>, format: String, level: CompressionLevel, password: Option<String>,
) -> Result<ArchiveStats, String> {
    let h = app.clone();
    do_compress(&paths, &format, level, password.as_deref(), move |name, pct| {
        let _ = h.emit("compress://progress", ProgressPayload { name: name.to_string(), pct });
    }).map_err(err_str)
}

#[tauri::command]
pub fn list_archive(path: String, password: Option<String>) -> Result<Vec<ArchiveEntry>, String> {
    let p = PathBuf::from(&path);
    if p.extension().and_then(|s| s.to_str()) == Some("mwr") {
        let file = std::fs::File::open(&p).map_err(|_| AppError::FileNotFound)?;
        let mut r = MwrReader::open(file, password.as_deref()).map_err(err_str)?;
        Ok(r.entries().iter().map(|e| ArchiveEntry {
            name: e.name.clone(), size: e.uncompressed_size, is_dir: e.is_dir, mtime: e.mtime, is_encrypted: r.is_encrypted(),
        }).collect())
    } else {
        zip::list_zip(&p, password.as_deref()).map_err(err_str)
    }
}

#[tauri::command]
pub fn extract(
    app: AppHandle, path: String, target_dir: String, password: Option<String>, filter: Option<Vec<String>>,
) -> Result<ArchiveStats, String> {
    let p = PathBuf::from(&path);
    let h = app.clone();
    let cb = move |name: &str, pct: u8| {
        let _ = h.emit("extract://progress", ProgressPayload { name: name.to_string(), pct });
    };
    if p.extension().and_then(|s| s.to_str()) == Some("mwr") {
        extract_mwr(&p, &PathBuf::from(&target_dir), password.as_deref(), cb).map_err(err_str)
    } else {
        zip::extract_zip(&p, &PathBuf::from(&target_dir), password.as_deref(), filter.as_deref(), cb).map_err(err_str)
    }
}

fn extract_mwr(path: &PathBuf, dir: &PathBuf, password: Option<&str>, mut on_progress: impl FnMut(&str, u8)) -> AppResult<ArchiveStats> {
    let file = std::fs::File::open(path)?;
    let mut r = MwrReader::open(file, password)?;
    let n = r.entries().len();
    for i in 0..n {
        let name = r.entries()[i].name.clone();
        on_progress(&name, ((i as u64 + 1) * 100 / n.max(1) as u64) as u8);
        let meta = &r.entries()[i];
        let out = dir.join(&meta.name);
        if meta.is_dir {
            std::fs::create_dir_all(&out)?;
        } else {
            if let Some(par) = out.parent() { std::fs::create_dir_all(par)?; }
            let data = r.read_file(i)?;
            std::fs::write(&out, &data)?;
        }
    }
    Ok(ArchiveStats { entry_count: n as u64, total_size: 0, target_path: dir.display().to_string() })
}

#[tauri::command]
pub fn preview_file(path: String, name: String, password: Option<String>) -> Result<PreviewResult, String> {
    let p = PathBuf::from(&path);
    let data = if p.extension().and_then(|s| s.to_str()) == Some("mwr") {
        let file = std::fs::File::open(&p).map_err(|_| AppError::FileNotFound)?;
        let mut r = MwrReader::open(file, password.as_deref()).map_err(err_str)?;
        let idx = r.entries().iter().position(|e| e.name == name).ok_or("条目不存在".to_string())?;
        r.read_file(idx).map_err(err_str)?
    } else {
        let f = std::fs::File::open(&p).map_err(|_| AppError::FileNotFound)?;
        let mut za = zip::ZipArchive::new(f).map_err(|_| AppError::UnsupportedFormat)?;
        // 需要重新打开以按名读取；简化：直接返回错误提示未实现 ZIP 预览
        return Err("ZIP 预览暂未实现".to_string());
    };
    if data.len() > 1024 * 1024 { return Ok(PreviewResult { kind: "binary".into(), text: None, bytes: None }); }
    if is_text(&data) {
        Ok(PreviewResult { kind: "text".into(), text: Some(String::from_utf8_lossy(&data).into_owned()), bytes: None })
    } else if is_image(&name) {
        Ok(PreviewResult { kind: "image".into(), text: None, bytes: Some(data) })
    } else {
        Ok(PreviewResult { kind: "binary".into(), text: None, bytes: None })
    }
}

fn is_text(data: &[u8]) -> bool { data.iter().all(|&b| b == b'\n' || b == b'\r' || b == b'\t' || (b >= 0x20 && b < 0x7f)) }
fn is_image(name: &str) -> bool {
    ["png", "jpg", "jpeg", "gif", "webp", "bmp"].iter().any(|ext| name.to_ascii_lowercase().ends_with(ext))
}
```

> ZIP 的 `preview_file` 在此 MVP 简化为返回「未实现」；若需 ZIP 预览，后续 Task 补充 `zip` 按名读取（`ZipArchive::by_name`）。`.mwr` 预览已完整。

- [ ] **Step 4: 运行确认通过**

```bash
cd src-tauri && cargo test commands
```

Expected: PASS。

- [ ] **Step 5: 在 lib.rs 注册 command 与 dialog 插件**

```rust
mod commands;
mod crypto;
mod error;
mod fs;
mod mwr;
mod types;
mod zip;

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![
            commands::compress,
            commands::list_archive,
            commands::extract,
            commands::preview_file
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
```

（`src-tauri/Cargo.toml` 追加 `tauri-plugin-dialog = "2"`。脚手架生成的 `lib.rs` 若已有 `run`，覆盖之。）

- [ ] **Step 6: 提交**

```bash
git add src-tauri/src/commands.rs src-tauri/src/lib.rs src-tauri/Cargo.toml
git commit -m "feat: wire Tauri commands and progress events"
```

---

## Task 10: 前端类型与 command 封装

**Files:**
- Create: `src/lib/types.ts`
- Create: `src/lib/commands.ts`
- Create: `src/composables/useArchive.ts`
- Modify: `src/App.vue`（暂保持脚手架默认，后续 Task 替换）

**Interfaces:**
- Consumes: Rust command 名（Task 9）。
- Produces: TS 类型与 `compress`/`listArchive`/`extract`/`previewFile` 封装函数；`useArchive` 响应式状态。

- [ ] **Step 1: 写 types.ts**

```ts
export type CompressionLevel = 'Store' | 'Fast' | 'Best';

export interface ArchiveStats {
  entry_count: number;
  total_size: number;
  target_path: string;
}

export interface ArchiveEntry {
  name: string;
  size: number;
  is_dir: boolean;
  mtime: number;
  is_encrypted: boolean;
}

export interface PreviewResult {
  kind: 'text' | 'image' | 'binary';
  text: string | null;
  bytes: number[] | null;
}

export interface ProgressPayload {
  name: string;
  pct: number;
}
```

- [ ] **Step 2: 写 commands.ts（封装 invoke，含事件订阅）**

```ts
import { invoke } from '@tauri-apps/api/core';
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import type { ArchiveEntry, ArchiveStats, CompressionLevel, PreviewResult, ProgressPayload } from './types';

export function compress(paths: string[], format: 'zip' | 'mwr', level: CompressionLevel, password: string | null) {
  return invoke<ArchiveStats>('compress', { paths, format, level, password });
}
export function listArchive(path: string, password: string | null) {
  return invoke<ArchiveEntry[]>('list_archive', { path, password });
}
export function extract(path: string, targetDir: string, password: string | null, filter: string[] | null) {
  return invoke<ArchiveStats>('extract', { path, targetDir, password, filter });
}
export function previewFile(path: string, name: string, password: string | null) {
  return invoke<PreviewResult>('preview_file', { path, name, password });
}

export function onCompressProgress(cb: (p: ProgressPayload) => void): Promise<UnlistenFn> {
  return listen<ProgressPayload>('compress://progress', (e) => cb(e.payload));
}
export function onExtractProgress(cb: (p: ProgressPayload) => void): Promise<UnlistenFn> {
  return listen<ProgressPayload>('extract://progress', (e) => cb(e.payload));
}
```

- [ ] **Step 3: 写 useArchive.ts（全局响应式状态）**

```ts
import { reactive } from 'vue';

export interface ArchiveState {
  mode: 'filesystem' | 'archive';
  currentDir: string;
  dirEntries: { name: string; is_dir: boolean; size: number }[];
  archivePath: string;
  archiveEntries: import('../lib/types').ArchiveEntry[];
  selected: string[];
  progress: { active: boolean; name: string; pct: number };
}

const state = reactive<ArchiveState>({
  mode: 'filesystem',
  currentDir: '',
  dirEntries: [],
  archivePath: '',
  archiveEntries: [],
  selected: [],
  progress: { active: false, name: '', pct: 0 },
});

export function useArchive() {
  return state;
}
```

- [ ] **Step 4: 类型检查**

```bash
pnpm vue-tsc --noEmit
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/lib/ src/composables/
git commit -m "feat: add frontend types, command wrappers, and state"
```

---

## Task 11: App.vue 与 Toolbar.vue（布局骨架）

**Files:**
- Create: `src/components/Toolbar.vue`
- Modify: `src/App.vue`

**Interfaces:**
- Consumes: `useArchive`（Task 10）。
- Produces: 主窗口骨架：顶部工具栏 + 主区占位（FileBrowser / ArchiveList 后续任务填充）+ 底部状态栏。

- [ ] **Step 1: 写 Toolbar.vue**

```vue
<script setup lang="ts">
const emit = defineEmits<{
  compress: [];
  extract: [];
  openArchive: [];
  back: [];
}>();
</script>

<template>
  <div class="flex items-center gap-2 border-b px-3 py-2 bg-gray-50">
    <button class="px-3 py-1 rounded bg-blue-600 text-white text-sm" @click="emit('compress')">压缩</button>
    <button class="px-3 py-1 rounded border text-sm" @click="emit('extract')">解压</button>
    <button class="px-3 py-1 rounded border text-sm" @click="emit('openArchive')">打开归档</button>
    <button class="px-3 py-1 rounded border text-sm" @click="emit('back')">返回上级</button>
  </div>
</template>
```

- [ ] **Step 2: 写 App.vue（双模式布局）**

```vue
<script setup lang="ts">
import { ref } from 'vue';
import Toolbar from './components/Toolbar.vue';
import FileBrowser from './components/FileBrowser.vue';
import ArchiveList from './components/ArchiveList.vue';
import CompressDialog from './components/CompressDialog.vue';
import ExtractDialog from './components/ExtractDialog.vue';
import ProgressDialog from './components/ProgressDialog.vue';
import PreviewPane from './components/PreviewPane.vue';
import { useArchive } from './composables/useArchive';

const state = useArchive();
const showCompress = ref(false);
const showExtract = ref(false);
</script>

<template>
  <div class="h-screen flex flex-col">
    <Toolbar @compress="showCompress = true" @extract="showExtract = true" />
    <div class="flex-1 flex overflow-hidden">
      <div class="flex-1 overflow-auto">
        <FileBrowser v-if="state.mode === 'filesystem'" />
        <ArchiveList v-else />
      </div>
      <PreviewPane class="w-80 border-l" />
    </div>
    <div class="border-t px-3 py-1 text-xs text-gray-500">
      选中 {{ state.selected.length }} 项
    </div>
    <CompressDialog v-if="showCompress" @close="showCompress = false" />
    <ExtractDialog v-if="showExtract" @close="showExtract = false" />
    <ProgressDialog v-if="state.progress.active" />
  </div>
</template>
```

> 后续 Task 12–14 依次实现 `FileBrowser`、`CompressDialog`、`ExtractDialog`、`ProgressDialog`、`ArchiveList`、`PreviewPane`。本 Task 的 App.vue 引用了它们，编译需待这些组件就绪——因此本 Task 的 Step 3 只做「App.vue 与 Toolbar 就位」，组件占位文件用最小 `defineComponent` 空模板创建，后续 Task 填充实现。

- [ ] **Step 3: 创建占位组件（最小空模板，保证编译通过）**

对 `FileBrowser.vue`、`ArchiveList.vue`、`CompressDialog.vue`、`ExtractDialog.vue`、`ProgressDialog.vue`、`PreviewPane.vue` 各创建：

```vue
<template><div></div></template>
```

- [ ] **Step 4: 类型检查与构建**

```bash
pnpm vue-tsc --noEmit && pnpm build
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/App.vue src/components/
git commit -m "feat: add app layout and toolbar skeleton"
```

---

## Task 12: FileBrowser、CompressDialog、ExtractDialog

**Files:**
- Modify: `src/components/FileBrowser.vue`
- Modify: `src/components/CompressDialog.vue`
- Modify: `src/components/ExtractDialog.vue`

**Interfaces:**
- Consumes: `useArchive`、`compress`/`extract`（Task 10）、`@tauri-apps/plugin-dialog` 的 `open`。
- Produces: 文件系统浏览（列出目录、选中、双击进入）；压缩对话框（格式/级别/密码）；解压对话框（目标目录/密码）。

- [ ] **Step 1: 实现 FileBrowser.vue**

```vue
<script setup lang="ts">
import { onMounted } from 'vue';
import { readDir } from '@tauri-apps/plugin-fs';
import { useArchive } from '../composables/useArchive';

const state = useArchive();

async function load(dir?: string) {
  if (dir) state.currentDir = dir;
  if (!state.currentDir) return;
  const entries = await readDir(state.currentDir);
  state.dirEntries = entries.map((e) => ({ name: e.name ?? '', is_dir: e.isDirectory, size: 0 }));
}
function enter(name: string) {
  state.currentDir = state.currentDir.replace(/[\\/]$/, '') + '/' + name;
  load();
}
onMounted(() => load());
</script>

<template>
  <div>
    <div class="px-3 py-2 text-xs text-gray-600 border-b">{{ state.currentDir }}</div>
    <ul>
      <li v-for="e in state.dirEntries" :key="e.name"
          class="flex items-center gap-2 px-3 py-1 hover:bg-gray-100 cursor-pointer"
          @click="e.is_dir ? enter(e.name) : toggleSelect(e.name)">
        <span>{{ e.is_dir ? '📁' : '📄' }}</span>
        <span>{{ e.name }}</span>
      </li>
    </ul>
  </div>
</template>

<script lang="ts">
function toggleSelect(name: string) {
  const i = state.selected.indexOf(name);
  if (i >= 0) state.selected.splice(i, 1);
  else state.selected.push(name);
}
</script>
```

> 注意：`toggleSelect` 需访问 `state`，将其并入 `<script setup>`（上面第二个 `<script>` 块仅为示意，实际把 `toggleSelect` 放进 `setup` 内，并 import `state`）。执行时合并为单个 `<script setup>`。

- [ ] **Step 2: 实现 CompressDialog.vue**

```vue
<script setup lang="ts">
import { ref } from 'vue';
import { open } from '@tauri-apps/plugin-dialog';
import { compress } from '../lib/commands';
import { useArchive } from '../composables/useArchive';

const emit = defineEmits<{ close: [] }>();
const state = useArchive();
const format = ref<'zip' | 'mwr'>('zip');
const level = ref<'Store' | 'Fast' | 'Best'>('Fast');
const password = ref('');
const busy = ref(false);

async function doCompress() {
  busy.value = true;
  try {
    const paths = state.selected.map((n) => state.currentDir.replace(/[\\/]$/, '') + '/' + n);
    await compress(paths, format.value, level.value, password.value || null);
    emit('close');
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="fixed inset-0 bg-black/30 flex items-center justify-center">
    <div class="bg-white rounded p-4 w-80 space-y-3">
      <h2 class="font-semibold">压缩</h2>
      <label class="block text-sm">格式
        <select v-model="format" class="border rounded w-full"><option value="zip">ZIP</option><option value="mwr">MWR</option></select>
      </label>
      <label class="block text-sm">压缩级别
        <select v-model="level" class="border rounded w-full"><option>Store</option><option>Fast</option><option>Best</option></select>
      </label>
      <label class="block text-sm">密码（可选）
        <input v-model="password" type="password" class="border rounded w-full" />
      </label>
      <div class="flex justify-end gap-2">
        <button class="px-3 py-1 border rounded" @click="emit('close')">取消</button>
        <button class="px-3 py-1 bg-blue-600 text-white rounded" :disabled="busy" @click="doCompress">确定</button>
      </div>
    </div>
  </div>
</template>
```

- [ ] **Step 3: 实现 ExtractDialog.vue**

```vue
<script setup lang="ts">
import { ref } from 'vue';
import { open } from '@tauri-apps/plugin-dialog';
import { extract } from '../lib/commands';
import { useArchive } from '../composables/useArchive';

const emit = defineEmits<{ close: [] }>();
const state = useArchive();
const targetDir = ref('');
const password = ref('');
const busy = ref(false);

async function pickDir() {
  const d = await open({ directory: true });
  if (typeof d === 'string') targetDir.value = d;
}
async function doExtract() {
  busy.value = true;
  try {
    await extract(state.archivePath, targetDir.value, password.value || null, null);
    emit('close');
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="fixed inset-0 bg-black/30 flex items-center justify-center">
    <div class="bg-white rounded p-4 w-80 space-y-3">
      <h2 class="font-semibold">解压</h2>
      <label class="block text-sm">目标目录
        <div class="flex gap-2">
          <input v-model="targetDir" class="border rounded flex-1" />
          <button class="px-2 border rounded" @click="pickDir">浏览</button>
        </div>
      </label>
      <label class="block text-sm">密码（可选）
        <input v-model="password" type="password" class="border rounded w-full" />
      </label>
      <div class="flex justify-end gap-2">
        <button class="px-3 py-1 border rounded" @click="emit('close')">取消</button>
        <button class="px-3 py-1 bg-blue-600 text-white rounded" :disabled="busy" @click="doExtract">确定</button>
      </div>
    </div>
  </div>
</template>
```

- [ ] **Step 4: 类型检查**

```bash
pnpm vue-tsc --noEmit
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/components/FileBrowser.vue src/components/CompressDialog.vue src/components/ExtractDialog.vue
git commit -m "feat: add file browser and compress/extract dialogs"
```

---

## Task 13: ProgressDialog.vue（进度事件）

**Files:**
- Modify: `src/components/ProgressDialog.vue`

**Interfaces:**
- Consumes: `useArchive`（Task 10）、`onCompressProgress`/`onExtractProgress`（Task 10）。
- Produces: 进度条 UI，订阅进度事件并更新 `state.progress`。

- [ ] **Step 1: 实现 ProgressDialog.vue**

```vue
<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue';
import { onCompressProgress, onExtractProgress } from '../lib/commands';
import { useArchive } from '../composables/useArchive';

const state = useArchive();
let un: (() => void)[] = [];

onMounted(async () => {
  un.push(await onCompressProgress((p) => { state.progress.name = p.name; state.progress.pct = p.pct; }));
  un.push(await onExtractProgress((p) => { state.progress.name = p.name; state.progress.pct = p.pct; }));
});
onUnmounted(() => un.forEach((f) => f()));
</script>

<template>
  <div class="fixed inset-0 bg-black/30 flex items-center justify-center">
    <div class="bg-white rounded p-4 w-80 space-y-3">
      <h2 class="font-semibold">处理中…</h2>
      <div class="text-xs text-gray-600 truncate">{{ state.progress.name }}</div>
      <div class="h-2 bg-gray-200 rounded overflow-hidden">
        <div class="h-full bg-blue-600" :style="{ width: state.progress.pct + '%' }"></div>
      </div>
      <div class="text-right text-xs">{{ state.progress.pct }}%</div>
    </div>
  </div>
</template>
```

- [ ] **Step 2: 类型检查**

```bash
pnpm vue-tsc --noEmit
```

Expected: PASS。

- [ ] **Step 3: 提交**

```bash
git add src/components/ProgressDialog.vue
git commit -m "feat: add progress dialog with event subscription"
```

---

## Task 14: ArchiveList.vue 与 PreviewPane.vue（归档浏览 + 预览）

**Files:**
- Modify: `src/components/ArchiveList.vue`
- Modify: `src/components/PreviewPane.vue`

**Interfaces:**
- Consumes: `listArchive`/`previewFile`（Task 10）、`useArchive`（Task 10）。
- Produces: 打开归档后列出条目；点击条目触发文本预览或图片缩略图。

- [ ] **Step 1: 实现 ArchiveList.vue**

```vue
<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { listArchive } from '../lib/commands';
import { useArchive } from '../composables/useArchive';

const state = useArchive();
const password = ref('');
const error = ref('');

async function open() {
  error.value = '';
  try {
    state.archiveEntries = await listArchive(state.archivePath, password.value || null);
    state.mode = 'archive';
  } catch (e) {
    error.value = String(e);
  }
}
onMounted(open);
</script>

<template>
  <div>
    <div class="px-3 py-2 flex gap-2 border-b">
      <input v-model="password" type="password" placeholder="密码（加密归档）" class="border rounded px-2 flex-1" />
      <button class="px-3 py-1 bg-blue-600 text-white rounded" @click="open">打开</button>
    </div>
    <div v-if="error" class="px-3 py-2 text-red-600 text-sm">{{ error }}</div>
    <ul>
      <li v-for="(e, i) in state.archiveEntries" :key="e.name"
          class="flex items-center gap-2 px-3 py-1 hover:bg-gray-100 cursor-pointer"
          @click="preview(i)">
        <span>{{ e.is_dir ? '📁' : '📄' }}</span>
        <span class="flex-1">{{ e.name }}</span>
        <span class="text-xs text-gray-400">{{ e.size }}</span>
      </li>
    </ul>
  </div>
</template>

<script lang="ts">
import { previewFile } from '../lib/commands';
import { useArchive } from '../composables/useArchive';
const state = useArchive();
async function preview(i: number) {
  const e = state.archiveEntries[i];
  if (e.is_dir) return;
  const r = await previewFile(state.archivePath, e.name, null);
  if (r.kind === 'text') state.previewText = r.text ?? '';
  if (r.kind === 'image' && r.bytes) state.previewImage = r.bytes;
}
</script>
```

> 同上，`preview` 与 `state` 引用并入单个 `<script setup>`。`state` 需扩展 `previewText: string` 与 `previewImage: number[] | null` 字段（回改 Task 10 的 `useArchive.ts`）。

- [ ] **Step 2: 扩展 useArchive.ts（补 previewText / previewImage）**

在 `ArchiveState` 与初始对象中新增：

```ts
previewText: string;
previewImage: number[] | null;
```

- [ ] **Step 3: 实现 PreviewPane.vue**

```vue
<script setup lang="ts">
import { computed } from 'vue';
import { useArchive } from '../composables/useArchive';

const state = useArchive();
const imgSrc = computed(() => {
  if (!state.previewImage) return '';
  const bytes = new Uint8Array(state.previewImage);
  const blob = new Blob([bytes]);
  return URL.createObjectURL(blob);
});
</script>

<template>
  <div class="p-3 space-y-2">
    <h3 class="text-sm font-semibold">预览</h3>
    <pre v-if="state.previewText" class="text-xs whitespace-pre-wrap max-h-full overflow-auto">{{ state.previewText }}</pre>
    <img v-if="imgSrc" :src="imgSrc" class="max-w-full" />
    <div v-if="!state.previewText && !imgSrc" class="text-xs text-gray-400">选择文件以预览</div>
  </div>
</template>
```

- [ ] **Step 4: 类型检查**

```bash
pnpm vue-tsc --noEmit
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/components/ArchiveList.vue src/components/PreviewPane.vue src/composables/useArchive.ts
git commit -m "feat: add archive browsing and file preview"
```

---

## Task 15: 拖拽与端到端验证

**Files:**
- Modify: `src/App.vue`（或 `src-tauri/src/lib.rs`）——拖拽路径获取
- Modify: `src-tauri/tauri.conf.json`（如需要启用 `dragDropEnabled`）

**Interfaces:**
- Consumes: 全部前序任务。
- Produces: 拖入文件/目录 → 自动设为选中/打开；完整端到端流程可用。

- [ ] **Step 1: 在 App.vue 注册拖拽监听（Tauri 2 drag-drop 事件）**

```vue
<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue';
import { getCurrentWebviewWindow } from '@tauri-apps/api/webviewWindow';

let un: (() => void)[] = [];
onMounted(async () => {
  const w = getCurrentWebviewWindow();
  un.push(await w.onDragDropEvent((event) => {
    if (event.payload.type === 'drop') {
      for (const p of event.payload.paths) {
        state.currentDir = p; // 简化：拖入目录则进入，文件则选中
      }
    }
  }));
});
onUnmounted(() => un.forEach((f) => f()));
</script>
```

> `state` 从 `useArchive` 引入。拖入目录：`load` 该目录；拖入文件：加入 `selected`。执行时按此语义完善。

- [ ] **Step 2: 端到端手动验证清单**

```bash
pnpm tauri dev
```

逐项验证：
1. 浏览目录，选中文件 → 压缩为 zip（无密码）→ 用系统解压软件验证可打开。
2. 压缩为 mwr（有密码）→ 打开归档输入密码 → 列出 → 预览文本 → 解压 → 内容一致。
3. 解压时输错密码 → 提示「密码错误」。
4. 拖拽文件夹到窗口 → 自动进入。

- [ ] **Step 3: 全量测试与构建**

```bash
cd src-tauri && cargo test && cargo clippy -- -D warnings
cd .. && pnpm vue-tsc --noEmit && pnpm build
```

Expected: 全部 PASS。

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "feat: add drag-drop and complete end-to-end flow"
```

---

## Self-Review 结果

- **Spec 覆盖**：功能需求（压缩/解压/列出/加密/级别/进度/拖拽/预览）→ Task 6/8/9/12/13/14/15；非功能（密码不落盘→Task 3 zeroize；大文件→MVP 边界已注明）；明确不做（RAR/7z/追加/分卷）→ 未实现，符合范围界定。
- **占位符**：无 TBD/TODO；Task 6 `finish` 的占位代码已用明确重写块替代。
- **类型一致性**：`CompressionLevel`、`EntryMeta`、`ArchiveStats`、`ArchiveEntry`、`PreviewResult`、`ProgressPayload` 命名前后一致；command 名 `compress/list_archive/extract/preview_file` 与前端 `invoke` 参数（snake_case）对齐；`useArchive` 的 `previewText/previewImage` 在 Task 14 回改 Task 10，已注明。
- **已知简化**：`preview_file` 对 ZIP 返回「未实现」（Task 9 注明），`.mwr` 完整；拖拽语义需按「目录进入/文件选中」完善（Task 15 注明）。
