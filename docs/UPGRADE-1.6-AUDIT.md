# LG Dsk 1.6 增量升级审计

基线：`b6751d05e109ffb68868cfd14aa7b7a8d8853095`（1.5.0）。本次需求来源为用户截图、复现描述及附带的 2.0 方向任务书。2.0 是产品方向；本批次先交付可回滚的 1.6 候选，不把自动化验证等同真机验收。所有改动必须从这个 1.5.0 基线增量实施，保留现有液态玻璃渲染、Dock/启动台基本布局、安装身份、程序集兼容身份和用户数据格式。

## Phase 0 与实施顺序

截至 2026-10-21 的续接核对：Release 全解决方案构建为 0 警告、0 错误；Core 198/198、AppServices 425/425、Dock 两个定向纯逻辑过滤组分别为 49/49 和 47/47。第二组中修订后的布局/命中几何用例已单独通过 1/1；这不等于整个 Dock 测试程序集通过。原生捕获恢复项本次未运行，不报告通过。旧审计曾记录 Core 194、AppServices 417、Dock 521 和原生捕获 1/1；这些数字不代表当前续接版本，已由本段最新口径取代。NuGet 当前源检查的历史记录为 17 个项目未报告已知漏洞，本次未重跑。另以独立临时工作树干净检出 1.5.0 原始提交的历史结果为 Release 构建 0 警告/错误、Core 174 项、AppServices 389 项通过；不与本次增量计数混为一组。独立启动台 WPF 键盘验收历史结果为 10/10，验收程序 Debug 构建 0 警告/0 错误。代码为 WPF/.NET 10 分层项目；沿用 Win32、Core Audio、UIA、Inno Setup，不引入桌面框架迁移。

1. 任务栏开关：观察逻辑不能依赖 Island 是否启用；任务栏工作区变化必须重新观察全屏状态、恢复窗口位置并处理 Explorer 延迟发布的几何信息。
2. 更新分发：现状只有 GitHub Releases API 与单一下载链接；连接重试不解决 GitHub 不可达。增加签名目录、免费静态分块、并行下载、缓存续传和 GitHub 降级。
3. 视觉/交互：先统一语义颜色、控件状态与系统辅助功能，再分组应用清单、改善键盘导航。保留玻璃光学效果和已有应用布局。
4. 验证、安装包、候选发布：逐批回归；发布说明、升级与回滚、未验证矩阵随包交付。

## 架构审计与 1.5.0 兼容边界

本轮按 `GlassDesk.slnx`、各项目引用、运行入口、应用服务契约、数据存储入口、Windows 平台边界、WPF 表面及打包脚本核对。当前方案仍是原有分层 WPF/.NET 桌面程序，没有另起新壳或替换液态玻璃渲染器。

| 层 / 项目 | 主责与入口 | 依赖方向和边界 | 本轮变化 / 审计关注点 |
| --- | --- | --- | --- |
| 可执行宿主 `GlassDesk.App` | `App.xaml.cs`、`AppHost` 及其 `Interaction`、`Maintenance`、`Performance`、`Updates` partial；协调单实例、迁移、运行租约、数据加载、窗口、托盘、快捷键、更新与释放 | 组合 Core、AppServices、Platform、UI、Dock、Launcher、Settings；不应把产品决策散落到多个窗口 | 新增更新源调用与生命周期协调；重点检查关闭竞态、取消下载和事件订阅释放 |
| 领域模型 / 数据 `GlassDesk.Core` | 应用条目、布局、设置、岛状态机、搜索/导入契约、原子 JSON 存储、备份与恢复 | `net10.0`；不引用 WPF 或平台项目；用户数据路径集中于 `AppPaths` | 新增启动台焦点导航和迁移所需路径边界；JSON 兼容格式与旧数据保护必须保持 |
| 应用用例 `GlassDesk.AppServices` | `AppServiceHost` 汇聚导入、启动、搜索、应用库维护、运行窗口跟踪、图标缓存、更新与迁移 | 依赖 Core 与 Platform；窗口使用服务入口，不直接写注册表、存储或访问 Shell | 多源签名更新及安装版首次迁移；测试覆盖协议/冲突策略，真实安装升级仍待验收 |
| Windows 集成 `GlassDesk.Platform` | Win32/DWM、窗口/显示器枚举、任务栏区域、全屏观察、全局快捷键、托盘、启动注册、Shell 启动与快捷方式 | 原生系统调用集中在此层；平台能力不可用时由上层策略降级 | Dock 顶层窗口监听工作区、显示分辨率与 DPI 消息后延迟重定位，并保留 750 ms 状态轮询及 Explorer 几何 settle；自动隐藏切换、Explorer 重启和多屏实际行为未验收 |
| 视觉与窗口 `GlassDesk.UI` | `ThemeService`、`GlassPalette`、`GlassWindow`、`LiquidGlassSurface`、玻璃背景捕获/恢复、动效与共享控件 | WPF 层依赖 Core/Platform；窗口视觉状态应通过主题服务统一解析 | `LiquidGlassSurface.cs` 相对 1.5.0 的 Git blob 完全一致；Dock 保留单一玻璃背板，新增系统图标仅作为透明前景素材。设置配色属于窗口表面主题，不替换 Dock 光学渲染 |
| Dock 与应用岛 `GlassDesk.Dock` | Dock 窗口、呈现策略、固定项/运行窗口/后台项/可访问托盘项、应用切换与玻璃内容层 | 依赖 AppServices/Core/UI；策略可单测，Shell 查询通过平台服务 | 在原 Dock 内容层加 Finder、设置、下载、回收站入口，保留原 `LiquidGlassSurface`；任务栏及岛状态分别验证 |
| 启动台 `GlassDesk.Launcher` | 应用网格、搜索、分组、拖动/排序、键盘焦点导航 | 依赖 Core、UI、AppServices；布局写入经共享服务完成 | 增补方向键、Enter、Esc 及焦点反馈；现有独立 WPF 验收只覆盖记录的 10 条键盘路径 |
| 设置中心 `GlassDesk.Settings` | 外观、Dock/岛、快捷键、性能、更新、启动项与备份恢复设置 | 依赖 Core 与 UI；状态变更经宿主/主题/设置服务传播 | Claude 衍生暖白色属于可调设置表面；不是 Anthropic 官方素材，也不重画玻璃控件 |
| 构建、安装与分发 | `build/package.ps1`、Inno Setup、`packaging/release.json`、静态更新目录 | 安装继续使用既有 `GlassDesk.Independent` AppId、用户级路径与内部程序集身份；更新签名私钥不得入库 | 本地 1.6.0 安装器/ZIP 已按候选流程编译和校验；尚无远端 CI、签名或发布；不改九霄站点/DNS |
| 自动化验证 | Core/AppServices/Dock 单元测试；UI、Launcher、Interaction、Desktop、Update 等独立验收项目 | 有副作用的完整宿主验收必须使用独立便携目录、独立数据和隔离桌面标记 | 原生捕获恢复项本次续接未运行；完整宿主交互验收仍未运行，未触碰已安装程序 |

`LauncherWindow.Dialogs.cs` 的文件选择器已设置 `Multiselect = true`、`DereferenceLinks = false`，把 `.lnk` 原路径交给 `ImportService`；`ImportServiceTests` 已覆盖一次导入两个程序快捷方式。此证据证明批量导入逻辑存在，不证明 Windows 原生文件对话框在用户机器上可用。截图中的“Codex 灾难性故障”尚未能在隔离桌面复现，因此不能宣称该对话框问题已修复。

### 运行生命周期与数据流

`AppHost.Start` 先获取新旧协议单实例租约，再确定安装/便携数据目录并执行仅复制、不删除旧目录的迁移；随后取得用户数据租约、确认显示器、初始化主题和数据、创建共享应用服务、复查缺失目标、启动运行窗口跟踪、创建托盘和窗口，最后安装快捷键/性能协调并启动更新检查。关闭时先关更新与命令入口，再解除事件/快捷键/监视器，释放托盘、共享服务、消息窗口、数据租约和实例租约。

桌面/窗口数据链为：`UI 命令 → AppServices 用例 → Core 仓储或 Platform Shell → 结构化结果 → UI 更新`。设置链为：`SettingsStore → ThemeService / DockSettingsHost → Dock、Launcher、Settings`。更新链为：`签名目录 → 验签/版本策略 → 分块 HTTPS 下载与 SHA-256 校验 → 原子落盘 → 用户确认后调用既有 Inno Setup 更新路径`。实际打开的程序、托盘可访问性、全屏覆盖能力和网络路由依赖 Windows、目标应用及用户网络；这些不能由本地模拟测试推定。

### 静态审计中保留的约束

- 当前运行版本、程序集身份和用户数据格式仍处于 1.5.0 兼容线上；只有新版本正式候选通过审查后才统一更新版本元数据和发行说明。
- `LiquidGlassSurface.cs` 未被修改，Dock 的玻璃表面仍为原有控件。Dock XAML 改动用于排列入口、前景图像和保留原材质表面；不得把窗口暖色安全首帧背景误当作玻璃背板替换。
- `Directory.Packages.props` 的第三方包版本仅供测试项目使用；产品源码项目没有 `PackageReference`。不为了新 UI 或更新链引入常驻后台服务、付费 API 或新的框架迁移。
- 自动化可证明已覆盖的纯逻辑与隔离 WPF 路径；目前仍缺 RTX 5090 真机视觉、Shell 切换、多屏/DPI/HDR、高刷新率、宠物遮挡、睡眠恢复、长稳以及安装升级验收。

## 截止 2026-10-21 的状态与证据边界

当前分支为 `codex/lg-dsk-1.6.0-completion`，`HEAD` 仍为 1.5.0 基线 `b6751d05e109ffb68868cfd14aa7b7a8d8853095`；本地增量未提交。1.6.0 版本元数据和本地候选包已生成。1.5.0 发布说明确认该版本保留液态玻璃 Dock。当前 `DockWindow.xaml` 仍使用 `LiquidGlassSurface`；其渲染实现未被替换。本轮新增的 Finder、设置、下载、回收站图标以内嵌透明前景素材叠加，不作为第二块 Dock 背板。

| 项目 | 当前可确认状态 | 证据等级 |
| --- | --- | --- |
| 1.5.0 兼容基线 | `HEAD`=`b6751d05…`；未改写基线提交；不覆盖既有工作树改动 | Code Reviewed |
| 液态玻璃 | Dock XAML 保留 `LiquidGlassSurface`；本轮图标为透明前景 | Code Reviewed |
| 自动化测试 | 最新已记录：Core 198/198、AppServices 425/425、Dock 两组定向纯逻辑测试 49/49 与 47/47；第二组修订后的布局/命中几何用例单独通过 1/1。未运行完整 Dock 测试程序集，也未运行原生捕获恢复项。旧审计中的 521/521、1/1 和 1133 汇总均不适用于当前续接状态。启动台 WPF 键盘验收 10/10 是既有隔离验收结果，不计入本次 | Partial; filtered tests only |
| 全解决方案 Release 构建 | 最近一次源代码构建 `dotnet build GlassDesk.slnx --configuration Release --no-restore --maxcpucount:1`：0 警告、0 错误；修正将 `GlassDesk.CompatibilityProbe` 纳入 `GlassDesk.slnx`，避免被引用时落到 Debug 输出目录。Release 下捕获协议与隔离传输定向验收 11/11 为此前记录，未在本次续接重跑 | Verified by Build; targeted test history |
| 多选快捷方式导入 | 启动台选择器支持多选并保留 `.lnk` 路径；AppServices 测试覆盖一次导入两个应用快捷方式 | Code Reviewed + Automated Test; native dialog issue pending |
| 静态更新源 | `updates.chronokalamos.com/stable.json` 本轮 HTTP 200；使用仓库公钥验证 RSA-PSS 签名成功，1.5.0 stable 目录列出的 8 个分块总长与 60,280,449 字节安装包大小相等；未改 DNS、Worker、Pages 或九霄项目 | Verified on current network; no regional guarantee |
| 零新增服务费方案 | Cloudflare 官方文档说明不调用 Functions 的静态资源请求免费且不限量；仍须避免引入 Worker 逻辑、KV、R2 或付费计划 | Code Reviewed + official pricing docs |
| Windows Shell/任务栏 | Dock 顶层窗口对 `WM_SETTINGCHANGE/SPI_SETWORKAREA`、`WM_DISPLAYCHANGE`、`WM_DPICHANGED` 做延后重定位；保留 750 ms 自动隐藏状态轮询与 Explorer 几何 settle。消息筛选与工作区几何定向自动测试 10/10；未在目标设备实测自动隐藏开关、Explorer 重启或全屏恢复 | Code Reviewed + Verified by Targeted Automated Test; device test pending |
| 用户数据迁移 | 安装版启动时复制旧程序目录 `Data` 至 `%LOCALAPPDATA%\LG Dsk\Data`，校验哈希并保留旧目录；冲突时停止、不覆盖；便携版用自身程序目录 `Data` 和按目录派生的单实例身份，不转交给安装版实例 | Code Reviewed + Automated Test; real 1.5.0 upgrade pending |
| Dock 系统图标 | 已从用户给的参考图提取 Finder、设置、下载、废纸篓图像并分开叠加；启动台、此电脑、桌面图标切换仍是现有自绘图形，尚未达到用户要求的苹果图标 1:1 验收 | Code Reviewed; visual acceptance pending |
| 安装包/新版本 | 本地 1.6.0 候选已通过 `verify-package.ps1`。独立便携副本解压后 279 个载荷文件的长度和 SHA-256 与 payload manifest 一致，含 `portable.flag` 与独立空 `Data`。打包自检使用隔离临时数据根运行非界面 `--selftest`；未运行安装器或体验副本。安装器未签名且未发布 | Verified local candidate; not a public release |
| 依赖漏洞扫描 | 2026-10-20 使用当前 NuGet 源执行 `dotnet list GlassDesk.slnx package --vulnerable --include-transitive`；命令输出中所有已检查项目均未报告已知易受攻击 NuGet 包 | Verified by NuGet advisory scan; not a full security audit |
| 完整宿主 GUI 验收安全边界 | 除隔离包标记、`portable.flag`、包内 `Data`、数据目录隔离和重解析点检查外，新增原生桌面名检查：当前线程桌面必须以 `LGDsk-Acceptance-` 开头且与 Windows 输入桌面完全相同；无法读取桌面名时失败关闭。构建脚本要求同一保护并兼容 Windows PowerShell 5.1。用 Windows PowerShell 5.1 在 `GLASSDESK_ISOLATED_DESKTOP=1` 下启动 `-DesktopAcceptance`，当前普通桌面仍被拒绝并返回 2；完整宿主 GUI 没有运行 | Verified by Automated Guard Check; interactive acceptance not run |
| 目标设备与 GUI | 尚未完成目标 Windows 设备上的窗口视觉、HDR/高 DPI/刷新率、多屏、睡眠恢复、桌面覆盖、长稳或实际安装升级验收；会操作系统桌面或真实输入的完整宿主验收只允许在隔离桌面运行 | Not Verified |

Windows 消息依据：[`WM_SETTINGCHANGE`](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-settingchange) 可通知顶层窗口系统参数变更；[`SPI_SETWORKAREA`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfoa) 表示工作区变更；Microsoft 的[任务栏文档](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)还说明任务栏状态可经 `ABN_STATECHANGE` 通知注册的 AppBar。LG Dsk 不注册 AppBar、不修改系统任务栏；现有轮询继续负责自动隐藏状态的最终一致性。

自动化测试只证明对应代码路径和环境中的行为；目前不能证明中国大陆或全球所有网络都可达，也不能代替用户设备上的 GUI 和性能验收。

## 已识别风险

| 风险 | 处理与证据边界 |
| --- | --- |
| 声称全球网络必达 | 免费 CDN + GitHub 属于两个服务路径，不能保证所有网络；中国等地区需真实网络实测 |
| 免费配额/隐性费用 | 仅使用 Workers Static Assets 无运行脚本，无 R2/KV/付费计划；发布前核对配置 |
| 第三方镜像替换安装包 | 不采用公共代理；目录 RSA 签名、分块 SHA-256 和最终安装包 SHA-256；私钥不入库 |
| 工作区变化和休眠状态不同步 | 重新观察而非修改 Windows 任务栏；延迟协调带代际编号，旧请求不能覆盖新状态 |
| 主题硬编码/辅助功能不生效 | 原 Normalize 强制深色并关闭 ReduceTransparency；逐步解除，测试兼容性 |
| 多屏、Clawd 残影、240Hz | 自动化只覆盖逻辑与部分真实 WPF 窗口；手工/硬件验收单独记录 |
| 数据位置迁移 | 已加入安装版首次启动迁移：只从程序目录旁旧 `Data` 复制到 `%LOCALAPPDATA%\LG Dsk\Data`；校验 SHA-256、拒绝路径重叠/重解析点/并发占用/已有目标冲突，旧目录保留用于回滚。便携版仍使用自身 `Data`。自动化迁移测试已覆盖成功、冲突、占用、幂等及重叠路径；真实 1.5.0 安装升级尚未在用户设备上验收。 |

## 需求追踪

| 用户需求 | 主要落点 | 验证方式 |
| --- | --- | --- |
| 批量添加应用快捷方式 | `LauncherWindow.Dialogs`、`ImportService`、`AppEntryFactory` | 已有多选与两条快捷方式导入自动化用例；截图中的原生文件对话框错误尚待隔离桌面复现 |
| 中国等网络更新 | 签名更新目录/静态分发、UpdateClient | 离线协议测试 + 远程文件校验；地域实测另列 |
| 下载慢/断线 | 小文件并发、内容寻址缓存、超时和备用路径；静态 CDN 对旧断点返回 HTTP 416 时删除对应未验证分块并从零重取 | 缓存复用、续传/完整性测试；HTTP 416 恢复用例有测试覆盖；最新已记录 AppServices 回归为 425/425 |
| 自动隐藏开关后 Dock 消失 | AppHost.Performance、DockWindow | WPF/状态回归；真实系统切换另列 |
| 登录时启动无法启用 | `StartupRegistration`、设置页启动项控件 | 已识别另一份已知 LG Dsk 安装时允许复选框操作，并在覆盖产品专属启动项前要求确认；未知程序启动项保持保护。隔离注册表用例覆盖拒绝、确认切换、失效旧路径与无关程序保护，定向 15 项及最新已记录 AppServices 425 项通过；设置窗口真实交互仍待隔离桌面验收 |
| 桌面简洁和信息分层 | DesignTokens、Island、Settings | 主题/分组/键盘测试与 GUI 验收 |
| 不影响九霄、预算0 | 独立静态项目与 updates.chronokalamos.com | DNS/域名路由前后核对，不改九霄工程 |

## 任务书 P0/P1 追踪矩阵

状态定义：**已验证**表示有当前自动化或静态证据；**部分完成**表示有代码/测试，但验收条件未闭环；**待完成**表示尚无足够交付或证据。测试通过不能替代 Windows 设备验收。

| 优先级 / 要求 | 当前增量与证据 | 状态 | 未闭环事项 |
| --- | --- | --- | --- |
| P0 完整仓库审计 | 本审计新增项目依赖图、主要模块职责、运行生命周期、数据流、更新/安装边界与 1.5.0 兼容约束；本次审查备份、托盘、快捷键、安装脚本和启动台导入路径；备份恢复新增重解析点/联接拒绝，Explorer 重启后重注册托盘图标；旧快捷键注销失败时保留禁用映射并在后续设置变更重试 | 部分完成 | 备份部分写入后的回滚、安装器 smoke test、启动台原生文件选择器异常及完整第三方/安全审查仍待闭环 |
| P0 基线构建与测试 | 精确 1.5.0 基线原始 Core 174、AppServices 389 通过；1.6 候选最近一次 Release 全解决方案构建 0 警告/错误。当前已记录 Core 198/198、AppServices 425/425、Dock 两组定向测试 49/49 与 47/47；原生恢复项未运行 | 部分完成 | 完整 Dock 回归、目标设备全宿主 GUI、多屏、DPI/HDR 和安装升级验收仍待完成 |
| P0 状态机规范统一 | `DockPresentationPolicy` 使用强类型 shell/任务栏/全屏/请求上下文；Dock 和 Island 保留独立状态机；新增 `docs/INTERACTION-STATE-MODEL.md` 记录实际决策顺序、转移与证据边界 | 部分完成 | 继续覆盖快捷键、窗口焦点和 Island 收起竞态；完整宿主交互仍需隔离桌面验收 |
| P0 焦点、遮挡和误弹 | 策略拒绝 secure/unknown desktop 与未知全屏状态；被动 Island 在已知独占全屏中保持隐藏，用户明确触发的应用切换器可在有可切换内容时显示；快捷键可唤醒该显式切换流程 | 部分完成 | 普通窗口、视频、独占全屏、Alt+Tab 及热键冲突的目标设备验收；独占全屏窗口管理器/显卡行为仍须实机确认 |
| P0 统一设计令牌 | `GlassPalette` 与 `ThemeService` 提供系统主题、高对比度、透明度与动效偏好 | 部分完成 | 完成 Dock、Island、Launcher、设置、菜单全表面令牌清点及明/暗/系统主题截图审核 |
| P0 玻璃对比度和可读性 | 保留 1.5.0 `LiquidGlassSurface`；不透明安全材质、前景色和高对比度路径存在 | 部分完成 | 以真实壁纸/内容背景测对比度；完成系统关闭透明效果与高对比度设备验收 |
| P0 窗口生命周期与事件竞态 | 自动隐藏状态轮询、顶层工作区/显示器/DPI 消息触发重定位；Shell 延迟位置协调使用 generation guard；收到 Explorer `TaskbarCreated` 后重新注册托盘；消息筛选与工作区几何定向测试 10/10，注册消息范围测试通过 | 部分完成 | Explorer 实际重启、快速反复显隐、宠物覆盖需设备复测 |
| P0 多显示器与 DPI | 使用 monitor/work-area 查询、DPI 上下文和重定位路径；有逻辑测试 | 部分完成 | 100/125/150/200% DPI、混合缩放及拔插显示器验收 |
| P0 数据迁移安全 | 安装版迁移先复制、校验 SHA-256、冲突即停且保留旧数据；便携版仍用自身数据目录 | 已验证（自动化） | 对真实 1.5.0 安装执行备份后升级/回滚演练 |
| P0 基础性能测量 | 有性能协调、休眠与切换相关逻辑测试 | 待完成 | 建立空闲 CPU/内存、动画帧时、显隐延迟基线；记录设备/刷新率/采样方法及优化前后数据 |
| P1 Dock 动效 | 图标悬停放大与固定入口布局保留；下载和回收站悬停状态独立；滑动补间改为订阅 `CompositionTarget.Rendering`，以经过时间计算步长，且只在动效进行时订阅；纯逻辑测试对照 60/120/240 Hz 下同一经过时间的进度。用户参考图中的四枚 PNG 素材逐张复查，无额外黑色描边；整机画面尚未验收 | 部分完成 | 目标设备实际帧时间、Dock 整体视觉、休眠/唤醒长稳仍需体验验收 |
| P1 Island 分层 | 自动隐藏任务栏时顶部中央紧凑入口、悬停展开与可访问托盘项目开关已接入；独占全屏不自动覆盖，但设置的快捷键可显示完整应用切换器 | 部分完成 | 完成信息分组和交互状态验收；隐藏托盘图标受 Windows UIA 可访问性限制；目标设备实测显卡全屏行为 |
| P1 应用切换器信息架构 | 运行窗口与可访问托盘项目由现有清单呈现，仍沿用 1.5.0 Dock/Glass 基础 | 部分完成 | 验证固定/运行/后台/托盘分组、同应用多窗口、不可切换后台进程和键盘切换 |
| P1 启动台搜索 | 保留 1.5.0 搜索实现；增补键盘入口与结果焦点导航；隔离 WPF 验收确认搜索框 Down 可将焦点移至首个磁贴 | 部分完成 | 对照源码并覆盖实际输入后的过滤、空结果、Enter 启动/多结果聚焦和清空后的焦点恢复 |
| P1 设置中心视觉 | 暖纸色 Claude 衍生设置配色、主题/透明度/减少动画与 Island/Dock 控件保留 | 部分完成 | 多主题、滚动裁切、键盘焦点、缩放和高对比度需要 GUI 检查 |
| P1 右键菜单统一 | LiquidGlass 主题提供共享 `ContextMenu`/`MenuItem` 样式；Dock 打开菜单时抑制自动隐藏，Launcher 追踪打开菜单避免窗口误关闭，托盘菜单沿用共享主题 | 部分完成 | 各菜单视觉、键盘顺序、UIA 读屏与多主题下的真实桌面验收仍待完成 |
| P1 键盘导航 | 启动台磁贴可聚焦，方向键按网格移动；Enter 打开分组，Esc 逐层返回；焦点轮廓显示主题强调色；隔离 WPF 验收 10/10 通过 | 部分完成 | 验收 Dock/Island/设置的 Tab 顺序、快捷键切换、应用 Enter 启动、无鼠标启动和 UIA 读屏语义 |
| P1 高对比度与减少动画 | 设置可保留用户主题和 ReduceMotion/ReduceTransparency；ThemeService 识别 Windows 高对比度及动画偏好 | 部分完成 | 全表面颜色/焦点可见性、系统偏好实时切换及动画减少验收 |
| P1 自动化构建与回归 | Release 构建通过；Core 198/198、AppServices 425/425、Dock 两组过滤分别为 49/49 与 47/47；当前便携候选 `verify-package.ps1` 通过并已隔离提取 | 部分完成 | 未推送分支，无远端 exact-SHA CI 结果；无代码签名；完整 Dock 回归、实际安装/升级 smoke 和 GUI 回归待完成 |

## 任务书最终交付门槛

本地 1.6.0 候选已完成版本元数据、Release 说明、README、固定 SHA 的 Windows CI 配置、安装器/ZIP 构建和包校验；独立便携副本按目录隔离单实例身份与 `Data`，用于用户体验。完整 Dock 回归和原生捕获恢复项尚未运行。静态更新服务仍未发布 1.6.0，GitHub 工作流没有远端运行，安装器未签名、未安装到真实用户环境。目标设备上的外观/性能、多屏/DPI/HDR、高刷新率、Explorer 重启、全屏、睡眠恢复、长稳和真实升级/回滚仍未闭环。因此当前是 1.6.0 本地候选，不是完整 2.0、稳定版或已公开发布版；最终矩阵继续明确保留这些边界。

## 外部研究

- [Apple Dock 操作](https://support.apple.com/guide/mac-help/use-the-dock-mh35859/mac)：固定与运行入口、上下文菜单和空间层级。
- [Microsoft ABM_GETSTATE](https://learn.microsoft.com/en-us/windows/win32/shell/abm-getstate)：查询任务栏状态；本项目不通过 ABM_SETSTATE 修改系统。
- [Velopack 更新源](https://docs.velopack.io/integrating/update-sources)：可独立托管更新目录，不必将发现和文件传输绑定于 GitHub API。保留 Inno 安装身份，不直接迁移安装框架。
- [Seelen UI](https://github.com/eythaann/Seelen-UI)：参考模块职责与 per-monitor 设置，不复制其他项目源码或许可证约束资产。
- [Cloudflare 静态资源计费](https://developers.cloudflare.com/workers/static-assets/billing-and-limitations/)：静态资源请求免费且无限制、存储无额外费用；动态 Worker 调用另计，本方案不配置运行脚本。

外部资料访问于 2026-10-19。参考截图包含第三方 macOS 扩展，并非全部属于原生 macOS。
