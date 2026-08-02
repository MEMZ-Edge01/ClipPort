<div align="center">
  <img src="src/ClipPort/Assets/Icons/clipport-app-icon.png" alt="ClipPort" width="160" />
  <h1>ClipPort</h1>
  <p>面向摄影、视频与其他大批量文件场景的 Windows 拷卡、校验和任务管理工具。</p>
  <p>
    <a href="#主要功能">主要功能</a> ·
    <a href="#使用方法">使用方法</a> ·
    <a href="#构建与测试">构建与测试</a> ·
    <a href="#english-summary">English</a>
  </p>
</div>

## 项目简介

ClipPort 是一款使用 WinUI 3、C#、.NET 8 和 C++ 构建的 Windows x64 桌面应用。
它可以把存储卡或普通目录中的文件复制到指定位置，并按需使用 SHA-256 对源文件与目标文件进行逐文件校验。

应用不依赖账号、云服务或远程数据库。
设置、任务历史、日志和报告都保存在本机。

## 主要功能

### 复制与校验

- 复制完整目录结构，包括空目录。
- 默认使用顺序异步复制，并以 4 MiB 缓冲区处理文件。
- 可启用或关闭 SHA-256 校验。
- 支持只校验模式：不创建目标目录、不复制文件，只比较已有源文件与目标文件。
- 先写入随机命名的 .clipport-partial 临时文件，完整写入后再提交到正式目标路径。
- 取消或单文件失败时会清理临时文件，不会用半成品覆盖已有目标文件。
- 尽力保留源文件的最后修改时间；无法保留时记录警告，但不会中断整个任务。
- 复制前检查目标磁盘空间，并拒绝危险的源目录、目标目录重叠关系。
- 跳过目录联接和符号链接，并阻止目标路径穿过重解析点，避免意外递归或写入到非预期位置。

### 三种任务模式

| 模式 | 行为 |
| --- | --- |
| 复制并校验 | 复制文件后对源文件与目标文件计算 SHA-256，默认模式 |
| 仅复制 | 复制文件但不执行校验 |
| 只校验 | 不复制文件，仅校验目标目录中已经存在的对应文件 |

创建任务时至少需要启用复制或校验中的一项。

### 重复文件处理

目标位置已经存在同名文件时，可以选择：

- 询问：先继续处理其他文件，随后逐项或批量决定。
- 覆盖：安全写入完成后替换目标文件。
- 跳过：保留现有目标文件。
- 创建副本：自动生成不冲突的新文件名。

询问模式支持多选、全选和批量应用处理策略。

### 多任务与优先级

- 可以创建多个并发任务，并在“新任务”和“历史任务”区域中分别管理。
- 会阻止同时运行的任务使用相互冲突的源路径或目标路径。
- 优先任务可以并行执行；普通任务会在安全检查点等待，直到全部优先任务结束。
- 每个任务都可以独立暂停、继续、取消，并可选择在任务期间阻止电脑进入休眠。
- 失败、取消或意外中断的任务可以按原配置重新开始。
- 已完成复制的任务可以再次启动只校验任务。

### 失败恢复

- 单个文件发生 I/O 或权限错误时，任务会继续处理其他文件。
- 任务结束前可以勾选失败文件并选择重试或跳过。
- 对 SHA-256 不一致的文件可以选择重新覆盖并再次校验。
- 任务报告会保留失败原因、警告、重复文件处理结果和最终状态。

### 实时进度与吞吐图

- 显示当前阶段、当前文件、完成百分比、文件数量、数据量、速度和耗时。
- 分别记录复制与校验阶段的字节速度和项目数速度。
- 提供实时波形、当前值、最高值、最低值和动态刻度。
- 复制图表与校验图表都可以在并排布局和纵向堆叠布局之间切换。
- 吞吐采样会随任务历史保存，重新选择历史任务时仍可查看。

### 历史、报告与批量操作

- 任务历史以 JSON 保存在本机，最多保留 200 条非活动记录。
- 活动任务不会因为历史数量达到上限而被删除。
- 单条损坏的历史记录会被隔离，不影响其他有效记录。
- 支持删除单条记录或批量删除已结束的记录，删除记录不会删除源文件和目标文件。
- 支持导出单个报告，也可以为多个任务批量生成报告。
- 报告内容跟随应用语言，并包含复制、校验、失败、警告和重复项处理明细。

### 外观与语言

- 支持跟随系统、浅色和深色三种外观模式。
- 支持 Windows 系统强调色，以及海沫绿、亮玫红、黄金色、浅薄荷色和紫影色五种预设颜色。
- 支持简体中文、English 和文言三种界面语言。
- 切换语言后可以立即安排应用安全重启，也可以稍后手动重启。
- 可以自定义日志和报告的保存目录。

## 使用方法

1. 点击“创建任务”。
2. 选择源目录或存储卡。
3. 如果启用复制，选择目标目录；可以额外填写目标子文件夹名。
4. 选择复制、SHA-256 校验、重复文件策略、休眠防止和优先执行选项。
5. 点击“开始任务”，在主界面查看实时进度与吞吐波形。
6. 如果出现重复文件或失败文件，按界面提示逐项或批量处理。
7. 任务结束后可以导出报告、重新开始或启动只校验任务。

## 本地数据

| 内容 | 默认位置 |
| --- | --- |
| 设置 | %LOCALAPPDATA%\ClipPort\settings.json |
| 任务历史 | %LOCALAPPDATA%\ClipPort\history.json |
| 日志 | 用户文档目录\ClipPort\ClipPort.log |
| 自动报告 | 用户文档目录\ClipPort |

日志和报告目录可以在设置中更改。
日志达到 5 MiB 后会轮换为 ClipPort.old.log。

## 当前限制

- 当前发布目标仅为 Windows x64。
- 当前提供自包含目录发布，不是单文件程序，也没有 MSIX 安装包。
- C++ 原生复制引擎会随 Release 包构建和发布，但对应开关目前在界面中隐藏并禁用。
- 普通用户当前使用默认顺序复制路径；原生引擎和托管流水线仅用于代码级实验与回归测试，不应视为稳定的公开功能。

## 项目结构

~~~text
ClipPort
├── ClipPort.sln
├── src
│   ├── ClipPort
│   │   ├── Models
│   │   ├── Services
│   │   ├── Strings
│   │   ├── Views
│   │   └── Assets
│   └── ClipPort.NativeCopy
├── tests
│   └── ClipPort.CoreTests
└── scripts
    └── publish-beta.ps1
~~~

## 构建与测试

### 环境要求

- Windows 10 1809 或更高版本。
- .NET 8 SDK。
- Visual Studio，并安装 MSBuild 与 C++ x64 桌面生成工具。

原生 C++ 项目需要 Visual Studio MSBuild。
不要使用 dotnet build ClipPort.sln 代替完整发布流程，否则可能因为无法解析 C++ targets 而失败。

### 获取代码

~~~powershell
git clone https://github.com/MEMZ-Edge01/ClipPort.git
cd ClipPort
~~~

### 运行核心测试

~~~powershell
dotnet run --project .\tests\ClipPort.CoreTests\ClipPort.CoreTests.csproj -c Release
~~~

当前核心测试共 28 项，覆盖：

- 本地化资源完整性与语言查找。
- Windows 系统强调色预览。
- 复制、SHA-256 校验和只校验模式。
- 校验不一致覆盖、暂停、继续和取消安全。
- 重复文件的询问、覆盖、跳过和创建副本。
- 单文件失败继续执行与失败重试。
- 空目录、路径安全和符号链接保护。
- 吞吐波形采样与显示格式。
- 设置容错、本地历史、损坏记录隔离和历史保留。
- 多语言报告、警告保留和优先任务调度。
- Release 包中的原生 DLL 可用性。

### 构建 Release 自包含包

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1
~~~

脚本会：

1. 使用 Visual Studio MSBuild 构建 Release x64 解决方案和 C++ 原生 DLL。
2. 检查 App.xbf、MainWindow.xbf、TraeWorkTheme.xbf 和 SettingsView.xbf。
3. 发布 win-x64 自包含运行时。
4. 验证 ClipPort.exe、ClipPort.dll、ClipPort.NativeCopy.dll、resources.pri 和语言资源。
5. 以安全的暂存与替换流程写入 artifacts 目录。

默认输出位置：

~~~text
artifacts\ClipPort-1.0.0-beta-win-x64
~~~

## 技术栈

- .NET 8
- C# 12
- WinUI 3 / Windows App SDK
- C++20 / Win32
- SHA-256
- JSON 本地持久化

## 许可证

本仓库目前未声明开源许可证。
除非仓库所有者另行授权，否则不应假定代码可以自由复制、修改或重新分发。

---

## English Summary

ClipPort is a Windows x64 desktop application for reliable media-card and directory transfers.
It supports safe file copying, optional per-file SHA-256 verification, verification-only jobs, duplicate-file policies, concurrent and priority tasks, pause and cancellation, failure recovery, local history, localized reports, and real-time byte/item throughput charts.

The application stores its settings and history locally and does not require an account or cloud service.

The native C++ copy engine is built and packaged for engineering validation, but its UI switch is currently hidden and disabled.
The default user-facing copy path is the sequential asynchronous implementation.

Run the 28 core tests:

~~~powershell
dotnet run --project .\tests\ClipPort.CoreTests\ClipPort.CoreTests.csproj -c Release
~~~

Build the self-contained Release package:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1
~~~
