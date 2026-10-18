# LG Dsk 1.5.0

1.5.0 是应用岛能力升级版本。它保留 1.4.x 的液态玻璃 Dock、启动台布局、暖白 Claude 衍生设置风格、应用岛位置与既有 Windows 用户级安装身份，在其上增加本机活动反馈、优先级恢复与快捷倒计时。没有将产品迁移为 IslandBridge，也没有改用 MSIX。

## 变更

- **明确的应用岛状态管理：**把隐藏、紧凑、悬停展开和固定展开从动画尺寸中解耦，由独立状态机处理上下文、悬停宽限期、固定与收起。键盘快捷切换条仍使用原入口。
- **应用快照原子更新：**窗口、固定项、已匹配后台进程和可访问托盘条目先组成完整清单，再发出单次集合 Reset 通知。扫描期间重复刷新合并为一次后续扫描；可恢复的 COM、Win32 和超时错误保留最后一份完整快照，避免中途清空列表。
- **音量活动反馈：**在 MTA 工作线程观察 Windows 默认多媒体输出端点的主音量和静音变化。只有真实音量/静音通知才展示约 2.4 秒的活动；首次读取状态和端点重连不会误报。相同音量事件键合并续期。设备拓扑变化后重新连接默认端点。监听只读，不调节或静音系统音量，不以高频轮询采集。
- **电源状态：**显示 Windows 公共电源 API 能提供的外接电源、电池百分比和充电状态。无系统电池、电池状态未知、电量未知和 API 不可用分别处理，不从设备型号推断。
- **快捷倒计时：**应用条提供 5、15、25、45 分钟预设，最多 8 个计时器；支持全部暂停、继续和清除。计时完成显示高优先级短时提醒；提醒到期后恢复尚未结束的活动。时间按单调时钟差计算，界面一秒更新一次。
- **活动优先级与恢复：**计时摘要、音量反馈和计时结束提醒由统一活动协调器排序。同键事件更新已有活动，不重复堆叠；短时提醒结束后恢复仍有效的较低优先级活动，活动事件不直接改变窗口布局。
- **电源与设备边界修正：**系统电池标志 `255` 表示未知，不再误判为无电池；`128` 才表示无系统电池。
- **兼容性：**保留 `GlassDesk.*` 内部程序集、数据目录、单实例身份、安装 AppId、自包含 .NET 10 和 Inno Setup 用户级安装方式。

## 当前未包含的能力

- **系统媒体会话卡片：**微软的 `GlobalSystemMediaTransportControlsSessionManager` 文档列有 `globalMediaControl` 应用能力。当前 LG Dsk 仍是 Inno Setup 安装的未打包桌面程序，没有该 package capability 声明；本版本没有声称可访问或控制其他应用的媒体会话。改变为 MSIX 会改变部署、升级与兼容面，需另行设计和真机验证。
- **退出后恢复计时器：**倒计时只保存在当前 LG Dsk 进程中，关闭或重启后不会恢复；它不是后台闹钟。
- **未暴露的系统项目：**任意第三方托盘图标、Windows 通知、所有浏览器媒体会话、逐应用音量和受保护全屏仍受 Windows API、应用能力或目标程序实现影响。

## 安装资产

- `LG-Dsk-1.5.0-win-x64-Setup.exe`：Windows x64 用户级安装包。
- `LG-Dsk-1.5.0-win-x64.zip`：自包含便携版，运行 `LGDsk.exe`。
- `SHA256SUMS.json`：发行文件大小与 SHA-256 清单。

便携版与安装版的数据目录相互独立。安装不会自动启用登录启动；安装前建议使用应用内备份。

## 本地验证

| 检查 | 结果 |
| --- | --- |
| Core 自动化测试 | 174/174 通过；含活动优先级、事件合并、单调时间和计时暂停/恢复 |
| AppServices 自动化测试 | 389/389 通过；含更新器、电源编码和音量状态标签测试 |
| Dock 自动化测试 | 503/503 通过；含 WPF Dock 窗口与应用岛策略测试 |
| 测试合计 | 1066/1066 通过；`dotnet test GlassDesk.slnx -c Release --no-restore` |
| 全解决方案 Release 构建 | 0 警告、0 错误；.NET SDK 10.0.401 |
| 自包含 win-x64 发布及发布程序 `--selftest` | 通过；发布脚本确认可执行文件启动并完成自检 |
| Inno Setup 安装器与便携 ZIP | Inno Setup 7.1.0 编译成功；最终资产经 `verify-package.ps1` 校验 |
| SHA-256、载荷清单、版本元数据、许可证和版本说明 | `verify-package.ps1` 全部通过 |
| NuGet 漏洞扫描 | `dotnet list GlassDesk.slnx package --vulnerable --include-transitive`：当前 NuGet 源下所有项目均未发现易受攻击依赖 |
| 默认音频设备、耳机/蓝牙热切换、实际音量按键反馈 | 未在目标用户桌面做交互验收 |
| Windows 电源与电池实际读数 | 单元测试覆盖编码转换；未在 RTX 5090 笔记本做硬件核验 |
| 任务栏自动隐藏、多屏 DPI、Clawd 遮挡和液态玻璃黑闪 | 未在用户目标桌面实机验收 |
| 独占全屏、保护画面及安全桌面 | 不保证普通桌面岛层能够覆盖 |
| Authenticode 签名 | 未签名 |

## 技术参考

- [IAudioEndpointVolumeCallback：回调非阻塞及注销/释放约束](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolumecallback)
- [SYSTEM_POWER_STATUS：未知电池标志、电量标志与公开字段](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status)
- [GlobalSystemMediaTransportControlsSessionManager：Windows 媒体会话 API 与 capability](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager?view=winrt-26100)
