# ClipPort fnOS 视觉一致性设计 QA

## Source visual truth

本轮视觉基准为用户提供的七张 Windows 截图：

- `CLIPS/PixPin_2026-08-12_20-00-40.png`（浅色设置·关于）
- `CLIPS/PixPin_2026-08-12_20-01-35.png`（浅色设置·外观）
- `CLIPS/PixPin_2026-08-12_20-02-03.png`（深色任务主页）
- `CLIPS/PixPin_2026-08-12_20-04-20.png`（深色设置·关于）
- `CLIPS/PixPin_2026-08-12_20-25-39.png`（拷贝吞吐波形）
- `CLIPS/PixPin_2026-08-12_20-26-03.png`（校验吞吐波形）
- `CLIPS/PixPin_2026-08-20_19-56-45.png`（新建拷卡任务弹窗，算法菜单展开）

## Implementation evidence

- 任务主页：`artifacts/fnos-task-home-dark-final-v4.png`
- 设置深色：`artifacts/fnos-settings-dark-final-v4.png`
- 设置浅色：`artifacts/fnos-settings-light-final-v2.png`
- 关于页深色：`artifacts/fnos-about-dark-final-v4.png`
- 授权目录：`artifacts/fnos-authorization-dark-final.png`
- 新建任务弹窗（当前开发预览）：`artifacts/fnos-new-task-dialog-current.png`
- 双列波形（当前开发预览）：`artifacts/fnos-waveform-two-column-current.png`
- 四组同输入并排对照：`artifacts/qa/task-comparison.png`、`artifacts/qa/appearance-comparison.png`、`artifacts/qa/about-comparison.png`、`artifacts/qa/dialog-comparison.png`

## Viewport and normalization

- 任务、设置、关于和授权页面均以 1155×781 CSS px、deviceScaleFactor 1 捕获。
- 新建任务弹窗以 1250×783 CSS px、deviceScaleFactor 1 捕获；双列波形以 1440×900 CSS px、deviceScaleFactor 1 捕获，并额外在 900×900 窄视口复核响应式布局。
- 用户截图中的 2598×1758 像素 @216 DPI 按 2.25 倍密度归一化为 1155×781；弹窗源图本身为 1250×783，未再缩放宽度。
- 对照前将源图和实现图放入同一张并排输入，避免把独立截图误当作对照结果。

## State and interactions

- 深色与浅色主题、空任务主页、统一设置五项导航、授权列表部分降级、关于页、波形并排/纵向切换均已捕获或通过测试覆盖。
- 弹窗开发预览处于“复制和校验开启、优先关闭”状态；开发环境 `?preview=dialog` 仅用于视觉夹具，生产构建不暴露该分支。
- 已验证“新建任务”重置草稿、“开始拷卡”保留草稿、弹窗取消不提交、原生路径选择、授权同步重试、三种任务模式联动、“拷贝时校验”枚举映射、任务操作、设置自动保存、三语言资源同步和通知表单测试。
- 浏览器渲染流程未发现页面运行时错误；Vite 的动态导入提示不影响产物。

## Full-view comparison evidence

并排对照确认任务主页的 292 px 侧栏、44 px 标题区、卡片层级、青色/金色波形区块和设置壳层比例稳定；浅色、深色和窄视口下菜单与任务操作均保留。设置基准截图原本只有四项菜单，而本计划要求将“快速开启”映射为“授权目录”并追加“通知”，该信息架构差异属于已确认的产品映射，不是视觉回归。

## Focused region comparison evidence

- 弹窗对照聚焦标题、开关、输入框、重复项单选、算法弹层、优先开关和底部双按钮，`artifacts/qa/dialog-comparison.png` 显示面板、输入框、弹层和按钮边界保持约 2 CSS px 内；文字基线仅受浏览器字体栅格影响，弹层覆盖关系与源图一致。
- 波形对照聚焦网格线、四级刻度、峰谷填充、发光线和单位标签，`Waveform.test.tsx` 与 `WaveformMath.ts` 夹具验证空数据、单点、长序列抽样压缩、坐标连续性及 180 ms EaseOutCubic 过渡。浏览器几何验收确认 1440 px 下两张 445 px 卡片的 SVG 与 50 px 刻度均未越界，900 px 下自动切换为单列且仍未越界。
- 关于页聚焦仓库图标与副标题，使用仓库真实 `Github.svg` 资源并在深色主题反相，避免自绘或占位图标。

## Findings

- [P1][已修复] 默认强调色曾沿用系统蓝色，和 Windows ClipPort 青绿色不一致。修复为统一设计令牌 `--accent: #00B294`，并以任务主页、设置页对照图复核。
- [P2][已修复] 深色关于页 GitHub 图标对比度不足。修复为真实仓库资源加 `--icon-filter: invert(1)`，并以 `fnos-about-dark-final-v4.png` 复核。
- [P2][已修复] 未检查更新时关于页动作按钮曾靠左且同时显示应用中心入口。调整为 Windows 同款右对齐单按钮，检测到 FPK 更新后才展示下载与应用中心操作。
- [P2][已修复] 原生 HTML select 无法复现 Windows 算法菜单展开状态。替换为可键盘操作的 `SelectMenu`，同状态截图已纳入 `dialog-comparison.png`。
- [P1][已修复] 两列波形的 `1000` 宽 SVG 曾按固有宽度侵入相邻卡片。图表网格改为可收缩列，并在卡片、图表区域和 SVG 三层裁剪溢出。
- [P1][已修复] 左上“新建任务”曾只清除选择但不打开弹窗。两个任务入口现共用同一弹窗，并用独立草稿实现重置、保留和取消语义。
- [P2][已修复] fnOS 曾把“多选”误用为“全选”，并自建“校验时机”下拉框。共享控件现直接采用 Windows `.resw`，界面改为 Windows 同款“拷贝时校验”开关。
- [P3][已接受] Windows 旧截图展示四项设置导航，fnOS 按计划在同一位置显示五项（外观、常规、授权目录、通知、关于）；这是平台功能映射，保留统一间距和选中态。

## Comparison history

1. 初始对照发现默认强调色偏蓝（P1），修改全局 CSS 令牌后重新捕获任务主页与设置页，青绿色和禁用态一致。
2. 深色关于页发现 GitHub 图标偏暗（P2），改用仓库真实 SVG 并反相后重新捕获，图标与卡片对比度恢复。
3. 关于页更新操作区曾显示多余入口且未右对齐（P2），收敛默认态并重新捕获右侧按钮位置。
4. 初始弹窗使用原生 select，展开态无法与 Windows 对齐（P2），改为 Fluent 风格 `SelectMenu`，调整弹层锚点、行高和 z-index 后重新捕获算法菜单展开状态。
5. 最终并排对照未发现可操作的 P0、P1 或 P2 视觉差异；仅保留已确认的五项导航映射 P3 记录。

## Implementation checklist

- [x] 统一任务壳层、设置壳层、主题令牌、Fluent 图标和响应式断点。
- [x] 原生目录选择取消静默、成功保留路径并提供授权同步重试及降级状态。
- [x] 两个新建任务入口、临时弹窗草稿、三种模式、优先级、重复项与“拷贝时校验”控件联动。
- [x] SVG 波形、抽样压缩、刻度、峰谷和动画测试。
- [x] Windows `.resw` 三语言共享资源生成、缺失/重复键校验和 fnOS 平台专属文案边界。
- [x] fnOS FPK 构建、内容审计和 SHA-256 校验文件生成。
- [x] 前端 lint、类型检查、28 项前端测试、Core 60 项和 fnOS 39 项测试。
- [ ] 真机原生选择器、授权撤销和安装升级验收，待设备接受临时 SSH 公钥后执行。

final result: passed
