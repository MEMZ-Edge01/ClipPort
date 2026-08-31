<div align="center">
  <img src="src/ClipPort/Assets/Icons/clipport-app-icon.png" alt="ClipPort" width="160" />
  <h1>ClipPort</h1>
  <p>面向摄影、视频与大批量文件场景的 Windows / fnOS 拷卡、完整性校验和任务管理工具。</p>
  <p>
    <a href="#功能概览">功能概览</a> ·
    <a href="#校验算法">校验算法</a> ·
    <a href="#windows-11-快速开启">快速开启</a> ·
    <a href="#构建与测试">构建与测试</a> ·
    <a href="#english-summary">English</a>
  </p>
  <p>
    <a href="https://github.com/MEMZ-Edge01/ClipPort/actions/workflows/ci.yml"><img src="https://github.com/MEMZ-Edge01/ClipPort/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPL--3.0-blue.svg" alt="GPL-3.0" /></a>
  </p>
</div>

## 项目简介

ClipPort 提供 Windows x64 桌面版和 x86_64 fnOS 原生版。Windows 界面使用 WinUI 3，fnOS 界面使用 React，两端共同引用 .NET 8 跨平台复制与校验核心。
它可以把存储卡或普通目录中的文件安全复制到指定位置，并按需重新读取源文件与目标文件，使用所选算法逐文件比较摘要。

应用不依赖账号、云服务或远程数据库。
设置、任务历史、日志和报告均保存在本机。

## 功能概览

### 安全复制与三种任务模式

| 模式 | 行为 |
| --- | --- |
| 复制并校验 | 使用所选算法重新读取并比较源文件与目标文件；默认在复制全部完成后校验，也可启用“拷贝时校验” |
| 仅复制 | 复制文件，但不计算校验摘要 |
| 只校验 | 不创建目标目录、不复制文件，仅校验目标目录中已有的对应文件 |

创建任务时至少需要启用复制或校验中的一项。

- 保留完整目录结构，包括空目录。
- 默认使用顺序异步复制，以 4 MiB 缓冲区流式处理文件。
- 先写入随机命名的 `.clipport-partial` 临时文件，完整写入后再提交到正式目标路径。
- 取消或单文件失败时清理临时文件，避免用半成品覆盖现有目标文件。
- 尽力保留源文件的最后修改时间；无法保留时记录警告，但不中断整个任务。
- 复制前检查目标磁盘空间，并拒绝危险的源目录、目标目录重叠关系。
- 跳过目录联接和符号链接，并阻止目标路径穿过重解析点，避免意外递归或写入非预期位置。
- 源目录采用后台流式枚举；扫描、目标目录预处理、重复项探测和容量检查不会占住界面线程，并实时显示已发现的目录数、文件数、数据量和当前路径。

### 可选校验算法

ClipPort 现在支持 `SHA-256`、`SHA-512`、`SHA-1`、`MD5` 和 `xxHash64`。
所选算法会贯穿普通任务、只校验、失败重试、覆盖后重新校验、历史记录和导出报告；旧历史记录或无效算法值会回退到 SHA-256。

所有算法均以 4 MiB 缓冲区流式读取文件，不会把整个文件一次性载入内存。
校验报告会标明实际算法，并记录源文件与目标文件的摘要。

开启“拷贝时校验”后，ClipPort 会用单个低 I/O 优先级后台工作器校验已经完整写入并提交的文件，复制始终具有更高优先级。
后台队列不会反向阻塞复制；队列繁忙或系统不支持后台优先级时，相关文件会在复制结束后继续校验。
后台工作器暂时没有可校验文件时，校验速度会立即显示为零，不再保留上一段校验速度。
该选项默认关闭，仅在同时启用复制和文件校验时生效；只校验任务仍按原有顺序执行。

### 重复文件处理

目标位置已经存在同名文件时，可以选择：

- 询问：先继续处理其他文件，随后逐项或批量决定。
- 覆盖：安全写入完成后替换目标文件。
- 跳过：保留现有目标文件。
- 创建副本：自动生成不冲突的新文件名。

询问模式支持多选、全选和批量应用处理策略。

### 多任务、优先级与恢复

- 可以创建多个任务并加入执行队列；同一时刻只让一个任务实际读写，避免并发执行导致性能骤降或崩溃，任务会在“新任务”和“历史任务”区域中分别管理。
- 会阻止同时运行的任务使用相互冲突的源路径或目标路径。
- 创建“优先执行”任务后，当前普通任务会先在安全检查点确认停稳，再把执行权交给优先任务；优先任务暂停或全部完成后，原普通任务自动继续，多个优先任务按创建顺序执行。
- 每个任务都可以独立暂停、继续、取消；被优先任务暂停的普通任务也可点击“继续”强制执行，此时其他优先任务会转为暂停状态。
- 任务标题右侧会用灰色“优先任务”标识区分优先任务，并可选择在任务期间阻止电脑进入休眠。
- 失败、取消或意外中断的任务可以按原配置重新开始。
- 已完成复制的任务可以再次启动只校验任务，并沿用原任务选择的校验算法。
- 单个文件发生 I/O、权限或校验错误时，任务会继续处理其他文件。
- 任务结束前可以勾选失败文件并选择重试或跳过；摘要不一致的文件可以覆盖后重新校验。

### 实时进度与吞吐图

- 显示当前阶段、当前文件、完成百分比、文件数量、数据量、速度和实际处理耗时；复制、校验及失败重试均使用独立的一秒单调时钟持续刷新，即使首次校验或校验大文件时暂时没有新的完成事件，耗时也会连续增加；等待用户选择重复项或失败处理方案时，相应阶段计时暂停；任务进入完成态后，速度栏立即改为对应阶段的平均速度。
- 扫描阶段显示不定总量进度和已发现的数据，不会把尚未完成的扫描误报为 100%。
- 分别记录复制与校验阶段的字节速度和项目数速度。
- 提供实时波形、当前值、最高值、最低值和动态刻度；横轴按可见采样时间线始终铺满图表，不再按任务完成百分比挤压采样点。刷新时横纵坐标都会从当前屏幕位置使用三次缓出动画连续过渡，新采样从上一帧尾点进入，避免坐标瞬移造成整条历史颤抖。长任务超过采样上限后会保留首尾与峰谷的多尺度摘要，避免历史文件和内存无限增长；拷贝时后台校验小文件的短暂队列间隙会保留最近一次有效速度和组合状态，不再反复跳到零速与“正在拷贝”。
- 只复制或只校验的任务仅显示对应的一组两张波形图；同时启用时显示两组图表，复制图表与校验图表都可以在并排布局和纵向堆叠布局之间切换。
- 吞吐采样会随任务历史保存，重新选择历史任务时仍可查看。

### 历史、报告与批量操作

- 任务历史以 JSON 保存在本机，最多保留 200 条非活动记录。
- 活动任务不会因为历史数量达到上限而被删除。
- 单条损坏的历史记录会被隔离，不影响其他有效记录。
- 支持删除单条记录或批量删除已结束的记录；删除记录不会删除源文件和目标文件。
- 支持导出单个报告，也可以为多个任务批量生成报告。
- 报告内容跟随应用语言，并包含任务模式、实际校验算法、摘要、失败、警告和重复项处理明细。

### 外观、语言与本地设置

- 支持跟随系统、浅色和深色三种外观模式。
- 支持 Windows 系统强调色，以及海沫绿、亮玫红、黄金色、浅薄荷色和紫影色五种预设颜色。
- 支持简体中文、English 和文言三种界面语言。
- 切换语言后可以立即安排应用安全重启，也可以稍后手动重启。
- 可以自定义日志和报告的保存目录。

### 多渠道任务通知

- “设置 → 通知”可以同时添加并独立启用多个企业微信、钉钉、飞书、Bark 或 SMTP 邮件渠道，也支持添加多个同类型渠道。
- 企业微信、钉钉和飞书填写机器人 HTTP/HTTPS Webhook；Bark 填写包含设备 Key 的 HTTP/HTTPS 推送地址。它们不是 WebSocket 服务，因此不接受 `ws://` 或 `wss://` 地址。
- SMTP 支持服务器、端口、登录账号、密码或授权码、发件人和多个收件人配置，连接会自动选择 SSL/TLS 或 STARTTLS。
- 每个渠道卡片底部都有“测试推送”，可以在正式启用前验证地址、凭据和服务响应。
- 可以分别选择“任务完成”和“任务失败”场景。失败场景包括部分完成、校验失败和执行失败；主动取消或应用中断默认不发送失败通知。
- 通知发送发生在任务结果、历史和报告保存之后；通知失败只写入日志，不会改变任务结果。多个已启用渠道会并行发送。
- Webhook/Bark 地址中的 Token 和 SMTP 密码使用 Windows DPAPI 按当前用户加密后写入设置文件；把设置复制给其他 Windows 用户时，这些凭据无法解密，需要重新填写。

### 自动更新

- “设置 → 关于 → 检查更新”会从 GitHub Releases 获取最新版本（预发布版本也会纳入检查）。
- 发现新版本后可以查看发布说明；确认后自动下载并校验 SHA-256，再重启应用完成更新。
- 更新采用便携目录整体替换，不覆盖设置、任务历史、日志和报告。

## 校验算法

| 算法 | 速度与摘要 | 兼容性与抗碰撞能力 | 建议用途 |
| --- | --- | --- | --- |
| SHA-256 | 速度与安全余量较均衡，256 位摘要 | 广泛支持，具备现代密码学抗碰撞能力 | 默认选择；重要素材交付和长期归档 |
| SHA-512 | 512 位摘要，计算开销通常更高 | 广泛支持，抗碰撞安全余量更高 | 对长期保存要求较高的归档 |
| SHA-1 | 通常较快，160 位摘要 | 便于兼容旧清单，但已不适合作为安全或防篡改证明 | 只用于必须兼容 SHA-1 的旧流程 |
| MD5 | 开销较低，128 位摘要 | 兼容性很高，但已不具备可靠的抗碰撞安全性 | 旧系统、旧清单或非安全兼容场景 |
| xxHash64 | 吞吐量优先，64 位摘要 | 非密码学算法，不用于防篡改证明 | 本机大批量快速完整性检查 |

> 校验摘要用于发现复制错误或文件变化，不等同于数字签名。
> 如果没有明确的兼容或吞吐量要求，建议保留默认的 SHA-256。

## fnOS 原生版

fnOS 版是自包含的 Linux x64 原生应用，不使用 Docker，也不要求设备另行安装 .NET 或 Node.js。应用通过统一网关 `/app/clipport/` 和应用目录中的 Unix Socket 提供页面、API 与 WebSocket，不开放独立网络端口。

### 系统要求与安装

- 设备架构：x86_64。
- fnOS：`1.2.0401` 或更高版本。
- fnOS App：`1.34.0` 或更高版本，以使用原生共享目录选择器。
- 只有管理员可以看到和使用应用入口。

本地构建后会生成：

```text
artifacts/ClipPort-1.0.0-beta-fnos-x86.fpk
artifacts/ClipPort-1.0.0-beta-fnos-x86.fpk.sha256
```

可在 fnOS 应用中心的手动安装入口选择 `.fpk`，或在测试设备上执行：

```bash
appcenter-cli install-fpk ClipPort-1.0.0-beta-fnos-x86.fpk
```

### 目录授权与安全边界

- 点击源目录或目标目录按钮时，fnOS 宿主内直接调用官方 `pickSharedFile`；源目录禁止新建文件夹，目标目录允许新建文件夹。
- 关闭原生选择器视为取消，不显示错误。选择成功后页面立即保留 fnOS 返回的真实路径，并根据源目录名生成安全的目标子目录；授权列表会独立刷新并做短暂重试，若暂未出现只提示“目录已选择，授权状态仍在同步”，提供“重新同步”且不会丢掉本次选择。
- 启动时授权目录使用独立加载状态；网关暂不可用只降级授权功能并提供“重试”，任务主页和设置仍可使用。无结构的网关通用错误会显示稳定的当前语言提示，未知后端异常只在服务端日志保留诊断信息。
- 独立浏览器使用 `openAppAuth` 打开授权窗口，并通过同源 `callback.html`、随机 `state` 与 `postMessage` 返回结果；页面始终保留“刷新授权目录”按钮。
- 每次创建、重新开始或再次校验任务时，后端都会重新查询应用共享授权根目录，并用当前管理员 UID 检查源目录可读、目标目录可读或可写。
- fnOS 设置页与 Windows 保持同一侧边栏顺序：外观、常规、授权目录（映射 Windows 的“快速开启”）、通知、关于；可通过原生选择器新增授权、刷新当前授权、撤销未被活动任务占用的授权，并在调用 `openFileManager` 前重新检查当前管理员 ACL。
- fnOS Open API 客户端同时检查 HTTP 状态和业务 `code/msg/reqId`，并区分超时、传输失败和非法 JSON。日志只记录操作名、状态码、业务码和请求编号，不记录 Token 或授权路径内容。
- 应用以专用 `clipport` 包用户运行。`TRIM_API_TOKEN` 只在调用 fnOS 系统 Unix Socket 时从当前进程环境读取，不写入历史、日志、报告或前端响应。
- Linux 路径区分大小写；拒绝源目标重叠、`..` 穿越和符号链接。临时目标以 `O_NOFOLLOW`、排他创建和随机 `.clipport-partial` 名称写入，完整写入后原子提交。
- 崩溃恢复只清理任务日志中逐项记录的 ClipPort 临时文件，不扫描或删除其他文件。卸载与删除历史也不会删除源文件和目标文件。

### 与 Windows 版的功能映射

| 能力 | Windows | fnOS |
| --- | --- | --- |
| 三种任务模式、五种算法、重复项与失败项处理 | 支持 | 支持 |
| 单执行队列、优先任务、安全检查点让权、暂停、恢复、取消和重试 | 支持 | 支持 |
| 运行任务、200 条历史、详情、批量删除与批量报告 | 支持 | 支持 |
| 实时及历史复制/校验字节与文件速率波形 | 支持 | 支持 |
| 网页关闭后继续后台任务 | 不适用 | 支持 |
| 文件资源管理器右键菜单 | 支持 | 映射为 fnOS 授权目录管理 |
| 路径定位 | 文件资源管理器 | fnOS `openFileManager` |
| 阻止系统休眠 | 支持 | 不显示；NAS 后台服务不使用该开关 |
| 快速复制实验开关 | 代码级实验 | 始终关闭 |
| 企业微信、钉钉、飞书、Bark 与 SMTP 通知 | 支持 | 支持，网页关闭后仍由后台发送 |
| 跟随系统/浅色/深色、强调色 | 支持 | 支持 |
| 界面语言 | 简中、英文、文言 | 简中、英文、文言；共享文本由 Windows `.resw` 生成 |
| 更新 | 下载并替换 Windows 包 | 检查对应 x86 FPK，下载后引导到应用中心升级 |

fnOS 暂停任务会在安全检查点释放单任务 I/O 执行权；恢复时重新进入 FIFO 队列。服务重启后，未完成任务会标记为“已中断”，管理员可重新开始。

fnOS 左上“新建任务”会退出当前选择、清空路径与选项并打开任务弹窗；主页“开始拷卡”则保留已选路径和选项后打开同一弹窗。弹窗使用可取消的临时草稿，并通过 fnOS 原生选择器设置数据源和拷卡目的地；“拷贝时校验”关闭时提交 `afterCopy`，开启时提交 `opportunisticDuringCopy`，不显示 fnOS 不支持的 FastCopy 或 Windows 防休眠设置。

任务主页、侧栏、任务弹窗、重复项处理、任务操作、波形、通用设置和状态等共享文案在每次前端开发、测试或生产构建前，从 `src/ClipPort/Strings/{zh-CN,en-US,lzh}/Resources.resw` 生成 TypeScript 资源表。生成器会拒绝三种语言中缺失或重复的共享键；fnOS 仅在前端资源中维护授权目录、FPK 更新和应用中心等平台专属文案。生成文件不得手工编辑。

### 设置、通知、报告与更新

- 设置以带版本号的 `settings.json` 原子保存；升级只补默认值，不覆盖任务、报告或已有设置。
- 通知的 Webhook/Bark 地址和 SMTP 密码使用 ASP.NET Core Data Protection 加密。密钥目录权限为应用专用用户 `0700`，设置文件为 `0600`；设置读取与保存 API 不回传已保存的密钥，只返回“已保存”标记。
- 测试发送和任务完成/失败通知都使用同一套五类提供商校验。任务在后台运行时，即使网页已关闭也会发送；发送失败只记录不含密钥的任务警告，不改变文件操作结果。
- 报告默认导出目录必须通过原生选择器授权。每次批量导出都会重新查询共享授权和当前管理员写权限，不信任历史保存的路径权限。
- “关于”只读取 GitHub Release 元数据并筛选 `fnos-x86_64` 或 `fnos-x86` 的 `.fpk`；应用不会绕过 fnOS 包管理器自行安装，而是引导到应用中心完成升级。任务主页、新建任务弹窗、设置和吞吐波形共用 Windows 的布局比例、主题令牌与三语言文本。两列波形的卡片、SVG、填充、光晕和刻度均在各自边界内裁剪，窄屏自动切换为单列。

### 构建 fnOS FPK

需要 .NET 8 SDK、Node.js 22 和 PowerShell 7：

```powershell
pwsh -NoProfile -File ./scripts/build-fnos.ps1
```

脚本会执行前端 Lint、类型检查和测试，运行 Windows 核心及 fnOS 后端测试，构建 React 页面，发布 `linux-x64` 自包含单文件后端，生成各尺寸品牌图标，审计 FPK 暂存目录，并下载官方 `fnpack 1.2.3`。Windows 与 Linux 版本的 fnpack 都使用脚本内固定的 SHA-256 校验后才会执行。

Ubuntu CI 会重复执行后端、Linux 路径、前端、生命周期和 FPK 内容审计，并上传 `.fpk` 与 `.sha256`；现有 Windows Release 流程不会自动发布 fnOS 工件到 GitHub Release。

### fnOS 真机验收

代码仓库内的模拟测试不能替代真实 fnOS 网关和 ACL。发布前应在 x86_64 测试设备上逐项检查：安装与启动、源与目标原生目录选择、取消选择、授权刷新失败提示、授权撤销、三种任务模式、优先任务让权、只读目标拒绝、复制校验、主动损坏检测、暂停与取消、逐项/批量重复文件处理、失败项重试或跳过、实时及历史波形、多选删除与批量报告、三种语言、五类通知测试、更新引导、关闭网页后继续执行、服务重启后的中断状态以及卸载不影响用户文件。

升级真机前必须先确认没有活动任务，核对正在运行进程的可执行文件和包目录，备份应用数据目录后再安装新 FPK。存在活动任务时不要强制停止服务；等待任务完成或重新取得明确授权。

验收结束后检查 API 响应、`clipport.log`、任务报告和静态前端文件，确认其中不含 `TRIM_API_TOKEN`、Webhook/Bark 密钥或 SMTP 密码。

测试设备地址和凭据只在验收时临时提供，不得写入仓库、脚本、日志或 CI。

## 文件资源管理器快速开启

ClipPort 可以通过文件资源管理器右键菜单快速创建任务。

- 右击单个文件夹或文件夹空白处，打开“新建 ClipPort 任务”。
- 选择“作为源目录”或“作为目标目录”，ClipPort 会打开并预填新任务窗口。
- 如果 ClipPort 已在运行，请求会通过当前用户的本地进程协调通道转交给现有实例并恢复窗口；普通启动与稀疏包身份启动共用同一个主实例，不会让新进程把活动任务误判为中断。
- 新式菜单在多选或选择非目录项目时不会显示该命令；传统菜单只注册到文件夹与文件夹空白处。
- 菜单标题跟随简体中文、English 或文言界面语言。

### 在发布包中启用

“设置 → 快速开启”提供两个互相独立的入口：

- **传统右键菜单（无需证书）**：直接注册到当前用户，无需管理员权限、签名证书或 MSIX。Windows 11 中从“显示更多选项”进入，也可用于 Windows 10。
- **Windows 11 新式右键菜单**：直接显示在首层右键菜单中，仅支持 Windows 11 版本 22000 或更高，由单独的稀疏 MSIX 组件提供。

如果只需要传统菜单，将完整发布目录放到固定位置，运行 `ClipPort.exe` 后打开“设置 → 快速开启”，仅开启“传统右键菜单（无需证书）”即可；下列证书与组件步骤都不需要执行。

若要启用 Windows 11 新式菜单：

1. 将完整发布目录放到固定位置，再运行 `ClipPort.exe`。
2. 打开“设置 → 快速开启”。
3. 如果是使用开发证书签名的测试版，先核对证书来源和指纹，再按页面指引将公开证书安装到“受信任人”。
4. 点击“安装组件”，等待页面确认注册成功。
5. 打开“Windows 11 新式右键菜单”开关。
6. 新开一个文件资源管理器窗口，右击文件夹或空白处进行测试。

发布目录需要同时包含 `ClipPort.ShellExtension.dll`、`ClipPort.ShellIntegration.msix` 和签名包所对应的 `ClipPort.ShellIntegration.cer`。
安装右键菜单组件后不要移动或拆分发布目录，因为稀疏包会引用该目录中的主程序和扩展 DLL。
构建与开发注册脚本会从应用原图生成清单规定的 50×50、44×44 和 150×150 PNG，避免包身份启动时任务栏退回占位图标。

## 使用方法

1. 点击“创建任务”，或通过文件资源管理器右键菜单预填源目录或目标目录。
2. 选择源目录或存储卡。
3. 如果启用复制，选择目标目录；可以额外填写目标子文件夹名。
4. 选择任务模式、校验算法、是否拷贝时校验、重复文件策略、休眠防止和优先执行选项。
5. 点击“开始任务”，在主界面查看实时进度与吞吐波形。
6. 如果出现重复文件或失败文件，按界面提示逐项或批量处理。
7. 任务结束后可以导出报告、重新开始或启动只校验任务。

## 本地数据

| 内容 | 默认位置 |
| --- | --- |
| 设置 | `%LOCALAPPDATA%\ClipPort\settings.json` |
| 任务历史 | `%LOCALAPPDATA%\ClipPort\history.json` |
| 日志 | `用户文档目录\ClipPort\ClipPort.log` |
| 自动报告 | `用户文档目录\ClipPort` |
| 更新缓存与更新器日志 | `%LOCALAPPDATA%\ClipPort\Updates` |
| fnOS 任务历史、日志、临时文件清单和报告 | fnOS 管理的 `TRIM_PKGVAR` 应用数据目录 |

日志和报告目录可以在设置中更改。
日志达到 5 MiB 后会轮换为 `ClipPort.old.log`。
通知渠道的非敏感字段保存在设置文件中；Webhook/Bark 地址与 SMTP 密码以当前 Windows 用户可解密的 DPAPI 密文保存。

## 当前限制

- 当前发布目标为 Windows x64 和 fnOS x86_64；fnOS ARM 尚未支持。
- 主程序是自包含目录发布，不是单文件程序，也不是完整应用 MSIX 安装包。
- Windows 11 右键菜单使用独立的稀疏 MSIX；测试版签名可能需要用户手动信任公开证书。
- 自动更新依赖 GitHub 网络访问；更新过程中应用会自动重启。
- C++ 原生复制引擎会随 Release 包构建和发布，但对应开关目前在界面中隐藏并禁用。
- 普通用户当前使用默认顺序复制路径；原生引擎和托管流水线仅用于代码级实验与回归测试，不应视为稳定的公开功能。

## 项目结构

```text
ClipPort
├── ClipPort.sln
├── packaging
│   ├── fnos
│   └── ShellIntegration
├── scripts
│   ├── build-fnos.ps1
│   ├── build-shell-package.ps1
│   ├── publish-beta.ps1
│   ├── register-shell-package-for-development.ps1
│   └── test-fnos-lifecycle.sh
├── src
│   ├── ClipPort.Core
│   ├── ClipPort.FnOS.Server
│   ├── ClipPort.FnOS.Web
│   ├── ClipPort
│   │   ├── Assets
│   │   ├── Models
│   │   ├── Services
│   │   ├── Strings
│   │   └── Views
│   ├── ClipPort.NativeCopy
│   └── ClipPort.ShellExtension
└── tests
    ├── ClipPort.CoreTests
    └── ClipPort.FnOS.Tests
```

## 构建与测试

### 环境要求

- Windows 10 1809 或更高版本。
- .NET 8 SDK。
- Visual Studio 2022，并安装 MSBuild 与 C++ x64 桌面生成工具。
- 如需构建右键菜单包，还需要包含 `MakeAppx.exe` 与 `SignTool.exe` 的 Windows SDK。

原生 C++ 项目需要 Visual Studio MSBuild。
不要使用 `dotnet build ClipPort.sln` 代替完整发布流程，否则可能因为无法解析 C++ targets 而失败。

### 获取代码

```powershell
git clone https://github.com/MEMZ-Edge01/ClipPort.git
cd ClipPort
```

### 运行核心测试

```powershell
dotnet run --project .\tests\ClipPort.CoreTests\ClipPort.CoreTests.csproj -c Release
```

当前 Windows 核心回归共 60 项，另有 fnOS API、授权、安全文件与跨平台复制测试，覆盖：

- 本地化资源完整性与语言查找。
- Windows 系统强调色预览。
- 五种校验算法的复制后校验、只校验和损坏检测。
- 校验不一致覆盖、暂停、继续和取消安全。
- 重复文件的询问、覆盖、跳过和创建副本。
- 单文件失败继续执行与失败重试。
- 空目录、路径安全和符号链接保护。
- 吞吐波形采样与显示格式。
- 后台流式扫描响应性、1.6 TB 元数据统计、拷贝时校验与超宽窗口布局。
- 设置容错、本地历史、损坏记录隔离和历史保留。
- 多语言报告、警告保留和优先任务调度。
- 快速开启请求的参数解析与目录预填逻辑，以及打包原生 DLL 的接口可用性。

### 构建 Release 自包含包

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1
```

脚本会：

1. 使用 Visual Studio MSBuild 构建 Release x64 解决方案、C++ 原生 DLL 和 Shell 扩展。
2. 检查 `App.xbf`、`MainWindow.xbf`、`TraeWorkTheme.xbf` 和 `SettingsView.xbf`。
3. 发布 `win-x64` 自包含运行时。
4. 发布独立的 `ClipPort.Updater.exe` 自包含更新器并放入发布目录。
5. 验证 `ClipPort.exe`、`ClipPort.dll`、`ClipPort.NativeCopy.dll`、`ClipPort.ShellExtension.dll`、`ClipPort.Updater.exe`、`resources.pri` 和语言资源。
6. 以安全的暂存与替换流程写入 `artifacts` 目录。
7. 传入 `-CreateZip` 时，额外生成 `ClipPort-{版本}-win-x64.zip` 与其 `.sha256` 校验文件，供 GitHub Release 上传。

默认输出位置：

```text
artifacts\ClipPort-1.0.0-beta-win-x64
```

未提供签名参数时，普通发布仍会成功，但会跳过 `ClipPort.ShellIntegration.msix`。

发布 GitHub Release（例如打 tag `v1.0.0-beta` 或手动触发 `release.yml`）时，CI 会执行上述脚本并上传 zip 与 SHA-256 文件。

### 构建签名的右键菜单组件

签名证书需要位于当前用户或本地计算机的 `My` 证书存储中，Publisher 必须与证书主题匹配。

```powershell
$env:CLIPPORT_SHELL_PACKAGE_PUBLISHER = "CN=Your Publisher"
$env:CLIPPORT_SHELL_PACKAGE_CERTIFICATE_THUMBPRINT = "YOUR_CERTIFICATE_THUMBPRINT"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1
```

脚本会构建并验证 `ClipPort.ShellIntegration.msix`，并把不含私钥的公开证书导出为 `ClipPort.ShellIntegration.cer`。

开发环境也可以在完成普通发布后使用松散清单注册，不需要生成可分发 MSIX：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\register-shell-package-for-development.ps1 `
  -ExternalContentDirectory .\artifacts\ClipPort-1.0.0-beta-win-x64
```

## 技术栈

- .NET 8 / C# 12
- WinUI 3 / Windows App SDK
- C++20 / Win32 / `IExplorerCommand`
- `System.Security.Cryptography` 与 `System.IO.Hashing`
- JSON 本地持久化

## 许可证

Copyright (C) 2026 MEMZ-Edge01

本项目采用 [GNU General Public License v3.0](LICENSE) 授权。
你可以依照 GPL-3.0 使用、研究、修改和重新分发本项目；分发修改版或衍生作品时，需要继续采用 GPL-3.0、保留相应版权与许可证声明，并按许可证要求提供对应源代码。
本项目不提供任何明示或默示担保，完整条款请参阅 [LICENSE](LICENSE)。

## 参与贡献与安全

- 提交代码或文档前，请阅读 [贡献指南](CONTRIBUTING.md) 和 [行为准则](CODE_OF_CONDUCT.md)。
- 普通缺陷和功能建议请使用仓库的 Issue 模板。
- 安全漏洞不要提交公开 Issue，请按照 [安全策略](SECURITY.md) 通过 GitHub 的私密漏洞报告入口提交。

---

## English Summary

ClipPort provides a Windows x64 desktop application and a native x86_64 fnOS package for reliable media-card and directory transfers.
It supports responsive streaming source scans, safe file copying, verification-only jobs, duplicate-file policies, a serial task queue with priority ordering, pause and cancellation, failure recovery, local history, localized reports, and real-time byte/item throughput charts.

File verification can use SHA-256, SHA-512, SHA-1, MD5, or xxHash64.
SHA-256 is the default, and the selected algorithm is preserved across retries, re-verification, history, and reports.
An optional, default-off “verify while copying” mode verifies fully committed files on one low-priority background worker without allowing queue pressure to block copying; any backlog is completed after the copy phase. Brief queue gaps between small files retain the latest useful verification rate and combined copy/verification status instead of flickering to zero and copy-only status.

On Windows 11, an optional sparse-MSIX shell component adds modern File Explorer commands for using a folder as the source or destination of a new ClipPort task.
Activation is redirected through a per-user local channel, so packaged and unpackaged launches share the existing ClipPort process without rewriting active jobs as interrupted.
Completed tasks replace their live transfer rates with the average copy and verification rates immediately.
Settings can hold multiple WeCom, DingTalk, Feishu, Bark, and SMTP notification channels. HTTP providers use their native webhook payloads, SMTP negotiates SSL/TLS or STARTTLS, and completion/failure scenes can be selected independently. Provider secrets are protected with per-user Windows DPAPI before being written to disk.

The native C++ copy engine is built and packaged for engineering validation, but its UI switch is currently hidden and disabled.
The default user-facing copy path is the sequential asynchronous implementation.

The fnOS edition is a self-contained .NET 8 Linux service with a React micro-app. It uses the fnOS shared-folder picker, administrator-only unified gateway authentication, Unix sockets, per-request authorization and ACL checks, and does not expose a standalone TCP port or use Docker. It requires fnOS 1.2.0401 and App 1.34.0 or later.

Run the 60 Windows core regression tests:

```powershell
dotnet run --project .\tests\ClipPort.CoreTests\ClipPort.CoreTests.csproj -c Release
```

Build the self-contained Release package:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1
```

Build and audit the native fnOS FPK:

```powershell
pwsh -NoProfile -File ./scripts/build-fnos.ps1
```

## License

Copyright (C) 2026 MEMZ-Edge01

ClipPort is licensed under the [GNU General Public License v3.0](LICENSE).
You may use, study, modify, and redistribute the project under GPL-3.0.
Distributed modified versions and derivative works must remain under GPL-3.0, retain the applicable notices, and provide the corresponding source code as required by the license.
