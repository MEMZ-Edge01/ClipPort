# 为 ClipPort 贡献

感谢你愿意改进 ClipPort。

## 开始之前

1. 先搜索现有 Issue 和 Pull Request，避免重复工作。
2. 对较大的功能或行为变更，请先创建 Issue 讨论目标、用户体验和兼容性。
3. 安全漏洞不要创建公开 Issue，请遵循 [SECURITY.md](SECURITY.md)。

## 开发环境

- Windows 10 1809 或更高版本。
- .NET 8 SDK。
- Visual Studio 2022，以及 C++ x64 桌面生成工具和 Windows SDK。

核心测试命令：

```powershell
dotnet run --project .\tests\ClipPort.CoreTests\ClipPort.CoreTests.csproj -c Release
```

完整 Release 验证命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1
```

解决方案包含原生 C++ 项目，因此请不要使用 `dotnet build ClipPort.sln` 代替完整发布流程。

## 提交修改

- 每个 Pull Request 只解决一个明确问题。
- 保留现有行为和本地化结构，新增用户可见文本时同步维护简体中文、English 和文言资源。
- 不要提交生成目录、本地媒体、日志、证书私钥或编辑器配置。
- 为行为变更添加或更新核心测试，并在 Pull Request 中写明实际运行的验证命令。
- 只有在测试和 Release 构建通过后，修改才具备合并条件。

## Pull Request 内容

请说明修改内容、修改原因、用户影响和验证结果，并附上相关 Issue。
界面修改应提供运行中界面的截图或录屏；仅有编译成功不足以证明交互正确。

## 许可证

提交贡献即表示你同意按本仓库的 [GPL-3.0](LICENSE) 许可证提供该贡献。

---

## English summary

Before contributing, search existing issues, discuss substantial changes first, and never disclose security vulnerabilities in public issues.
Run the core tests and the full Release publish command above, keep all three localizations in sync, and describe the real validation performed in the pull request.
Contributions are provided under the repository's GPL-3.0 license.
