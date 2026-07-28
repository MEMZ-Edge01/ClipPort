# EZ DIT

<div align="center">

**[English](#english)** &nbsp;|&nbsp; **[中文](#chinese)**

一个使用 WinUI 3 + C# + C++ 构建的 Windows 自动拷卡工具 —— 安全、快速、可校验

</div>

---

<a name="chinese"></a>

## 🇨🇳 中文

### 简介

**EZ DIT**（Easy Digital Imaging Transfer）是一款 Windows 桌面应用，专为摄影师和视频工作者设计，用于将存储卡中的素材安全、高效地拷贝到本地磁盘。支持完整的 SHA-256 完整性校验，确保每一个字节都被正确复制。

### 特性

- 🚀 **快速拷贝**：原生 C++ FastCopy 引擎，支持有界环形缓冲、Overlapped I/O 和 Direct I/O
- ✅ **SHA-256 校验**：逐文件执行 SHA-256 哈希校验，确保数据完整性
- 📂 **保持目录结构**：完整复制源目录结构，包括空目录
- ⏯️ **暂停/继续/取消**：随时暂停拷贝或校验，取消不会损坏已有目标文件
- 🔄 **并发任务队列**：支持同时配置多个任务，优先级任务可插队执行
- 📋 **历史记录**：自动保存任务历史，数据仅存储在本地
- 📄 **报告导出**：导出包含每个文件哈希值及验证结果的文本报告
- 🌗 **深色/浅色主题**：支持跟随系统 + 6 种强调色
- 🌐 **中英双语**：完整的中文和英文界面
- 🛡️ **安全写入**：使用 `.ezdit-partial` 临时文件 + 原子替换，取消/失败不破坏已有文件
- 🔗 **符号链接保护**：跳过目录联接和符号链接，避免意外递归

### 使用

1. 点击「源目录 / 存储卡」选择素材所在目录（如 SD 卡）。
2. 点击「拷卡目的地」选择目标目录。
3. 默认自动开始；可关闭「自动开始」再手动点击「开始拷卡」。
4. 支持暂停 / 继续 / 取消，取消不会影响已完成的文件。
5. 左侧历史面板可查看任意历史任务详情或导出报告。
6. 可在设置中切换语言、主题和强调色。

> 所有历史数据和报告仅保存在本机 `%LOCALAPPDATA%\EZDIT`，无云服务依赖。

### 技术架构

```
EZ DIT
├── src/EZDIT/                  # C# WinUI 3 主程序
│   ├── Models/                 # 数据模型
│   ├── Services/               # 核心服务层
│   │   ├── FileCopyService.cs  # 文件拷贝与校验
│   │   ├── NativeCopyEngine.cs # 原生引擎 P/Invoke 封装
│   │   ├── CopyJobScheduler.cs # 并发任务调度器
│   │   └── ThemeManager.cs     # 主题与强调色管理
│   ├── Views/                  # XAML 视图
│   ├── Converters/             # 绑定转换器
│   └── Strings/                # 多语言资源 (.resw)
├── src/EZDIT.NativeCopy/       # C++ 原生 FastCopy 引擎
│   ├── native_copy.h           # 公开 API 头文件
│   └── native_copy.cpp         # 实现
└── tests/EZDIT.CoreTests/      # 核心流程测试（17 个用例）
```

### 拷贝引擎

EZ DIT 提供三种拷贝模式，根据配置自动选择：

| 模式 | 说明 | 适用场景 |
|------|------|----------|
| **标准顺序复制** | `FileStream` 异步读写，4 MiB 缓冲 | 小文件、默认模式 |
| **托管流水线** | `Channel<T>` 实现的生产者-消费者流水线，4 个 4 MiB 缓冲区 | 大文件、无原生 DLL 时 |
| **原生 FastCopy** | C++ 实现，Win32 Overlapped I/O、Direct I/O（>32 MiB 文件） | 最大性能 |

原生引擎特性：
- 两个原生工作线程重叠执行读取和写入
- 4 × 4 MiB 有界环形缓冲控制内存和背压
- 大于 32 MiB 的文件尝试 Direct I/O，不满足对齐要求时自动回退
- 支持 `CancelIoEx` 快速取消
- DLL 不存在或 API 版本不匹配时自动降级

> 原生实现是根据公开的 Windows I/O 能力独立编写的，没有复制 FastCopy-M 的 GPLv3 源码。

### 构建

**环境要求：**

- Windows 10 1809+（推荐 Windows 11）
- Visual Studio 2022 17.8+
- .NET 8 SDK
- 工作负载：`.NET 桌面开发`、`Windows 应用 SDK C# 模板`、`使用 C++ 的桌面开发`

**构建步骤：**

```powershell
# 1. 克隆仓库
git clone https://github.com/MEMZ-Edge01/EZ-DIT.git
cd EZ-DIT

# 2. 用 Visual Studio 打开 EZDIT.sln，选择 x64，生成

# 3. 或用命令行构建
msbuild .\EZDIT.sln -restore -m -p:Configuration=Release -p:Platform=x64

# 4. 发布自包含 x64 包
dotnet publish .\src\EZDIT\EZDIT.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false
```

### 测试

```powershell
dotnet run --project .\tests\EZDIT.CoreTests\EZDIT.CoreTests.csproj -c Release
```

测试覆盖 17 个场景：正常复制与哈希一致性、暂停/继续、取消安全、损坏检测、文件失败恢复、空目录处理、FastCopy 流水线、重复文件逐策略处理、历史持久化、优先级调度等。

### 数据存储

所有任务历史和报告完全保存在本机 `%LOCALAPPDATA%\EZDIT`，不使用账号、云服务或网络数据库。默认报告输出目录为用户文档下的 `EZ DIT` 文件夹。

### 许可证

本项目目前未声明开源许可证，所有权利保留。

### 致谢

- [Windows App SDK / WinUI 3](https://github.com/microsoft/microsoft-ui-xaml) — UI 框架
- [FastCopy](https://fastcopy.jp/) — 灵感来源（独立实现，未使用其源码）

---

<a name="english"></a>

## 🇬🇧 English

### Overview

**EZ DIT** (Easy Digital Imaging Transfer) is a Windows desktop application designed for photographers and videographers to safely and efficiently copy media from memory cards to local storage. It features full SHA-256 integrity verification to ensure every byte is copied correctly.

### Features

- 🚀 **Fast Copy** — Native C++ engine with bounded ring buffers, Overlapped I/O, and Direct I/O
- ✅ **SHA-256 Verification** — Per-file hash verification for data integrity
- 📂 **Preserves Structure** — Full directory tree including empty folders
- ⏯️ **Pause/Resume/Cancel** — Safe cancellation with `.ezdit-partial` atomic writes
- 🔄 **Concurrent Queue** — Multiple jobs with priority scheduling
- 📋 **History** — Local-only task history with no cloud dependencies
- 📄 **Reports** — Export per-file hash verification reports
- 🌗 **Themes** — Light/dark with system follow + 6 accent colors
- 🌐 **i18n** — Full Chinese and English localization
- 🛡️ **Safe Writes** — Temporary files + atomic replacement; cancelling never corrupts existing files
- 🔗 **Symlink Safe** — Skips junctions and symbolic links

### Architecture

- **Frontend**: WinUI 3 (Windows App SDK), C#, .NET 8
- **Core Logic**: `FileCopyService` — async streaming copy with SHA-256 verification
- **Native Engine**: C++/Win32 — ring buffer pipeline with Direct I/O fallback
- **Scheduler**: Custom priority-gated concurrent job scheduler
- **Persistence**: JSON-based local history and settings
- **Testing**: 17 core scenario tests covering copy, verify, pause, cancel, and recovery

### Build & Test

Same commands as the Chinese section above.

### License

No open-source license has been declared for this project. All rights reserved.
