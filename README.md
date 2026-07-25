# EZ DIT

一个使用 C#、WinUI 3 和 Fluent Design 构建的 Windows 自动拷卡工具。

## 功能

- 选择源目录（存储卡）与目标目录
- 两个目录选择完成后自动开始，也可关闭“自动开始”后手动启动
- 保持目录结构进行异步流式复制，包括空目录
- 新建任务时可选用 FastCopy 风格的有界读写流水线，并在校验阶段并行读取源文件与目标文件
- 实时显示总进度、速度、文件数、当前文件和耗时
- 支持暂停、继续与取消
- 复制完成后逐文件执行 SHA-256 完整性校验
- 左侧保存本机历史任务，点击可查看任意任务的时间、大小、路径、进度和结果
- 完成页显示明确的成功或失败状态，可删除单条历史记录
- 导出包含每个文件哈希值与验证结果的文本报告
- 开始前检查目标磁盘可用空间
- 防止源目录与目标目录相同、或目标位于源目录内部
- 使用 `.ezdit-partial` 临时文件；完整写入后才替换目标文件，取消或失败不会破坏已有目标文件
- 跳过目录联接和符号链接，避免意外递归复制

## 使用

1. 点击“源目录 / 存储卡”右侧的文件夹按钮，选择存储卡或素材目录。
2. 点击“拷卡目的地”右侧的文件夹按钮，选择目标目录。
3. 默认会自动开始；也可以先关闭“自动开始”，再点击“开始拷卡”。
4. 复制阶段和 SHA-256 校验阶段都可以暂停、继续或取消。
5. 点击左侧任意历史任务，可重新查看其时间、大小、路径与执行结果。
6. 点击“创建报告”可导出所选历史任务的本地报告；“删除记录”不会删除素材。

目标目录中同名文件只会在新文件完整写入后被替换。取消任务时，已经完成的文件会保留，当前未完成的临时文件会自动清理。

历史任务和报告完全保存在本机 `%LOCALAPPDATA%\EZDIT`，不使用账号、云服务或网络数据库。

## 原生 FastCopy 引擎

启用“使用 FastCopy 算法”后，程序优先调用独立开发的 EZDIT.NativeCopy.dll：

- 使用两个原生工作线程重叠执行读取和写入
- 使用 4 × 4 MiB 有界环形缓冲控制内存和背压
- 使用 Win32 Overlapped I/O，并支持 CancelIoEx 快速取消
- 大于 32 MiB 的文件会尝试无缓冲 Direct I/O；不满足对齐或设备不支持时自动回退
- Direct I/O 最后的非扇区对齐部分会切换到普通缓冲 I/O
- DLL 不存在或 API 版本不匹配时，自动使用托管 FastCopy 风格流水线

原生实现是根据公开的 Windows I/O 能力独立编写的，没有复制 FastCopy-M 的 GPLv3 源码。

## 构建要求

- Windows 10 1809 或更高版本（推荐 Windows 11）
- Visual Studio 2022 17.8 或更高版本
- 安装“.NET 桌面开发”“Windows 应用 SDK C# 模板”和“使用 C++ 的桌面开发”工作负载
- .NET 8 SDK

用 Visual Studio 打开 `EZDIT.sln`，选择 `x64` 后还原 NuGet 包并运行。项目采用非打包、自包含 Windows App SDK 配置。

命令行构建：

```powershell
# 在 Visual Studio Developer PowerShell 中执行
msbuild .\EZDIT.sln -restore -m -p:Configuration=Release -p:Platform=x64
```

生成无需预装 .NET 的 x64 发布目录：

```powershell
dotnet publish .\src\EZDIT\EZDIT.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false
```

## 核心流程测试

测试覆盖正常复制与哈希一致性、暂停/继续、取消安全、目标篡改检测、空目录、空卡处理和本地历史持久化：

```powershell
dotnet run --project .\tests\EZDIT.CoreTests\EZDIT.CoreTests.csproj -c Release
```