# LG Dsk Dock 与应用岛状态模型

本文记录当前 1.5.0 基线上的增量实现。它描述的是现有源码行为，不代表所有 2.0 任务书验收已经完成。Dock 继续使用原有 `LiquidGlassSurface`；本文只说明窗口何时呈现、呈现哪一层以及应用岛如何展开。

## 状态分层

LG Dsk 将桌面环境、Dock 表面和应用岛交互状态分别管理。它们通过一次环境快照组合决策，避免用一个布尔值同时代表前台应用、任务栏、全屏和鼠标状态。

| 维度 | 当前源码状态 | 作用 |
| --- | --- | --- |
| Shell 环境 | `DockForegroundKind`：`ShellDesktop`、`ExternalApplication`、`SecureDesktop`、`Unknown` | 标识前台窗口由桌面、普通应用还是不可安全覆盖的桌面持有 |
| 任务栏 | `DockTaskbarKind`：`Visible`、`AutoHidden`、`Unknown` | 识别自动隐藏状态；当前策略只把 `AutoHidden` 用作紧凑应用岛的可用条件 |
| 全屏 | `DockFullscreenKind`：`NotExclusive`、`Exclusive`、`Unknown` | 对已知独占全屏抑制被动浮层；未知状态采取隐藏策略 |
| 用户请求 | `DockAwarenessRequest`：`None`、`CompactIsland`、`Switcher` | 区分系统被动提示与用户明确打开的应用切换器 |
| Dock 表面 | `DesktopDock`、`RunningAppSwitcher`、`RunningAppIsland`、`Hidden` | 选择现有 Dock 内容、应用切换器、紧凑岛或隐藏窗口内容 |
| 应用岛交互 | `Hidden`、`Compact`、`HoverExpanded`、`PinnedExpanded` | 只管理紧凑应用岛的可见与展开状态，不决定 Shell 是否允许它出现 |
| 自动隐藏 | `AutoHideController` 的 `Shown`、`Hidden`、过渡/抑制状态 | 负责 Dock 自身沿屏幕边缘的显隐和位置，不负责全屏安全策略 |

## Dock 呈现决策

`DockPresentationPolicy.Resolve` 是纯决策函数。平台查询和窗口定位在 Dock/AppHost，策略接收 `DockPresentationContext` 后返回一个表面模式。

决策顺序如下：

1. 设置窗口可见、前台为安全桌面/未知桌面，或全屏状态未知时，返回 `Hidden`。
2. 用户明确请求 `Switcher`，前台是普通外部应用且存在可切换内容时，返回 `RunningAppSwitcher`。此项允许策略在已知独占全屏上响应显式请求；Windows、显卡驱动或目标程序仍可能限制覆盖和激活。
3. 其余已知独占全屏情况返回 `Hidden`，不被动弹出 Dock 或应用岛。
4. 启动台处于活动状态或 Windows 桌面在前台时，返回 `DesktopDock`。
5. 非外部应用前台且不属于上述桌面情形时返回 `Hidden`。
6. 普通外部应用前台、任务栏自动隐藏、请求为 `CompactIsland` 时返回 `RunningAppIsland`。
7. 其他情况返回 `Hidden`。

实际运行中，`DockWindow.OnAutoHideFrame` 先收集 Shell、全屏、启动台和设置窗口状态，再构造快照并调用策略。`ApplyPresentationMode` 只切换现有内容层的可见性，并触发显示器定位、活动窗口清单和电源状态刷新。`UpdateAutoHideState` 根据呈现模式单独更新 Dock 滑出状态。

## 应用岛交互转移

应用岛状态机由 `AppIslandStateMachine` 管理；WPF 控件不会从像素尺寸或动画进度反推状态。

| 当前状态 / 事件 | 条件 | 下一状态 |
| --- | --- | --- |
| `Hidden` / 环境允许 | 用户启用应用岛，自动隐藏任务栏，普通外部应用前台，且不是设置/启动台/独占全屏/不安全桌面 | `Compact` |
| 任意可见状态 / 环境失效 | 任务栏恢复显示、前台返回桌面、启动台/设置打开、独占全屏或 Shell 状态不安全 | `Hidden`；清除悬停与延迟收起状态 |
| `Compact` / 鼠标进入 | 启用“鼠标经过展开” | `HoverExpanded` |
| `HoverExpanded` / 鼠标离开 | 指针离开 | 启动单调时钟 700 ms 收起计时；尚不立即改变状态 |
| `HoverExpanded` / 延迟到期 | 指针仍在外且状态仍是悬停展开 | `Compact` |
| `Compact` / 点击 | 环境仍允许 | `PinnedExpanded` |
| `PinnedExpanded` / 再次点击 | 环境仍允许 | `Compact` |
| 任一展开状态 / 显式关闭 | 环境仍允许 | `Compact` |

用户明确打开的 `Switcher` 与被动应用岛相互独立：切换器打开时取消应用岛的悬停展开；切换器关闭后，Shell 快照重新决定是否显示紧凑应用岛。展开面板聚合固定 Dock 项、可切换运行窗口、后台应用条目和 Windows UI Automation 可读取的通知区域项目。Windows 不保证所有托盘图标都能被 UI Automation 访问，因此托盘列表不是完整任务管理器清单。

## 环境观察与恢复

- AppHost 以 750 ms 低频一致性检查重新读取任务栏自动隐藏和 Shell 状态；任务栏状态改变时重新计算全屏状态并要求 Dock 重新定位。
- Dock 顶层窗口另监听工作区、显示器和 DPI 变化，延迟重定位以等待 Explorer 发布稳定几何信息。
- Explorer 重启后，Dock 顶层窗口收到注册的 `TaskbarCreated` 广播时，AppHost 让 `TrayIcon` 再次执行通知区域添加操作。该逻辑通过编译和消息标识单元测试；尚未在用户桌面实际重启 Explorer 验收。
- Dock 滑动只在位置目标变化时订阅 WPF `CompositionTarget.Rendering`；静止时解除订阅。插值以经过时间计算，时间过长时限幅。测试比较了 60/120/240 Hz 假想节拍下相同经过时间的数值进度，**没有测量目标 GPU 的真实帧率、功耗或帧时间**。
- 设置面板和减少动画状态采用即时定位；背景采集、游戏休眠和窗口关闭通过既有生命周期入口停止相应计时器或渲染订阅。

## 自动化证据与未覆盖项

相关纯策略测试位于 `tests/GlassDesk.Dock.Tests/DockPresentationPolicyTests.cs`、`DockSlideInterpolatorTests.cs` 和 `DockTaskbarGeometryTests.cs`。Shell 广播消息恢复的消息注册范围测试位于 `tests/GlassDesk.AppServices.Tests/TrayIconRecoveryTests.cs`。这些测试不启动完整桌面宿主，也不模拟真实 Explorer、DirectX 独占全屏或系统任务栏输入。

仍需在隔离的 Windows 桌面及目标设备上检查：自动隐藏任务栏反复切换、Explorer 重启、全屏视频与独占全屏游戏、Alt+Tab、混合 DPI/多屏、HDR/高刷新率、睡眠恢复、桌面覆盖、窗口焦点、通知区域完整性和长稳资源趋势。完整宿主验收必须使用仓库中的隔离标记与便携测试数据，不应连接或操作用户现有桌面数据。
