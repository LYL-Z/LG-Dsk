# LG Dsk

**LG Dsk 是面向 Windows 11 的液态玻璃桌面 Dock 与启动台定制版。**它保留 GlassDesk 原有的窗口材质、圆角、布局和交互基础，在此基础上替换产品标识，修正前台应用时 Dock 误弹的问题，并为自动隐藏 Windows 任务栏的场景增加手动应用切换入口。

本项目按用户的联想拯救者 3840 × 2400、240 Hz 屏幕环境作为适配参考；这不等于已在该 RTX 5090 设备上完成现场验收。

## 获取和安装

GitHub 发布页：<https://github.com/LYL-Z/LG-Dsk/releases/latest>

每个版本提供 Windows x64 安装程序，以及适合临时运行或自行解压的便携包：

- `LG-Dsk-1.4.0-win-x64-Setup.exe`：用户级安装程序，可选择安装目录，并可选创建桌面快捷方式。
- `LG-Dsk-1.4.0-win-x64.zip`：自包含便携包，不需要单独安装 .NET 运行时。
- `.sha256` 文件：用于核对 ZIP 包完整性。

安装包面向 Windows 11 x64，最低版本为 Build 22000。安装前请先从系统托盘退出仍在运行的 GlassDesk，以免旧进程的单实例机制阻止新版本启动。升级同一安装位置时会保留已有的 `Data` 用户数据；安装包本身不包含个人数据。便携版会在程序目录旁创建自己的 `Data`，不会自动导入已安装版本的设置。

LG Dsk 目前没有使用可信发布者证书进行 Authenticode 签名。Windows 可能显示未知发布者或 SmartScreen 提示；下载后可先按发布页提供的 SHA-256 校验文件完整性。

## 项目定位与设计

LG Dsk 的目标是对现有产品做小范围的个人定制，不重做界面：

- 保留原有深色液态玻璃表面、Dock 与启动台的结构、圆角和设置方式。
- 将原 Z 标记替换为浮雕 LG 字标，并加入少量陶土橙星芒，呼应用户桌面暖纸色 Claude 风格。图形为衍生创作，不使用 Claude 或 Anthropic 官方标志，也不代表双方存在关联。
- 让 Dock 在普通应用处于前台时收起，鼠标移到屏幕底边也不会将它唤出；回到桌面时 Dock 再显示。
- 新增 `Ctrl+Alt+Space` 运行应用切换条，减少 Windows 自动隐藏任务栏后切换程序的步骤。切换条复用现有玻璃窗口，检测到全屏应用时不覆盖画面。
- 保留启动台悬停建文件夹等有明确依据且已完成的 1.4.0 行为；移除自愿赞助入口，并关闭上游自动更新源，避免上游安装包覆盖本地定制版。

### 与上游 1.4.0 的关系

上游公开源码仓库提供到 1.3.1 的源码快照。上游 1.4.0 发布说明提到版本从 1.3.6 继续更新，并包含没有公开源码的 1.3.7 私有改动。因此，LG Dsk 1.4.0 以公开 1.3.1 源码为基础，并按公开说明重建了选定行为；它不是上游 1.4.0 源码的逐行合并，也没有声称复现未公开的改动。已移植、未移植和原因见[版本移植记录](docs/LG-Dsk-1.4.0.md)与[`SOURCE-PROVENANCE.json`](SOURCE-PROVENANCE.json)。

## 交互说明

1. 在桌面显示 Dock；打开普通应用后 Dock 自动收起。
2. 使用应用时按 `Ctrl+Alt+Space` 显示运行应用切换条，再点选目标窗口。再次按快捷键或点击条外区域可以收起它。
3. 全屏应用期间不显示切换条；退出全屏后可再次呼出。
4. 启动台中把一个应用拖到另一个应用上停留约 500 ms，可创建文件夹；等待期间松开则仍按普通排序处理。

## 技术结构

项目使用 WPF 与 .NET 10，按职责拆分为多个库；`src/GlassDesk.App` 是唯一随产品发布的可执行程序。

| 模块 | 职责 |
| --- | --- |
| `GlassDesk.App` | 程序入口、单实例协调、托盘、数据目录与窗口生命周期 |
| `GlassDesk.Core` | 共享模型、接口、设置与基础数据逻辑；不依赖 WPF 或 Win32 |
| `GlassDesk.AppServices` | 应用导入、图标缓存和启动服务；使用 Windows 能力但不依赖 WPF |
| `GlassDesk.Platform` | Windows 窗口、进程、DPI、显示器及系统交互封装 |
| `GlassDesk.UI` | 统一的液态玻璃窗口基类、主题、调色板与玻璃着色器 |
| `GlassDesk.Dock` | Dock 呈现、前台窗口策略和应用切换条 |
| `GlassDesk.Launcher` | 启动台、应用排序与文件夹交互 |
| `GlassDesk.Settings` | 外观、启动台、桌面与交互设置 |

窗口按显示器 DPI 处理。现有玻璃使用 DWM 能力并提供兼容回退；活跃动画约每 16 ms 更新，静止时约每 100 ms 轮询，不为 240 Hz 屏幕增加同频后台轮询。对不同 Windows 版本、显卡驱动、HDR、多屏配置和游戏全屏的完整现场表现仍需在目标机器上验收。

## 构建环境

- Windows 11 x64；应用运行最低要求为 Build 22000。
- .NET SDK 10.0.401（仓库的 `global.json` 允许同一功能带内滚动）。
- 生成 `.exe` 安装程序时另需 Inno Setup 7 x64 编译器 `ISCC.exe`。

从源码编译和运行：

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet run --project src/GlassDesk.App/GlassDesk.App.csproj -c Release
```

运行测试项目：

```powershell
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release
```

部分 WPF 与桌面交互测试需要可交互的 Windows 会话。运行源码构建版前，也应退出托盘中仍运行的安装版本，避免单实例冲突。

## 从源码生成发布包

便携版使用自包含 x64 发布，不要求用户另装 .NET Runtime：

```powershell
$version = '1.4.0'
$stage = "artifacts\LG-Dsk-$version-win-x64"
dotnet publish src/GlassDesk.App/GlassDesk.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true `
  "-p:InformationalVersion=$version" `
  -p:IncludeSourceRevisionInInformationalVersion=false `
  -o $stage
Copy-Item LICENSE "$stage\LICENSE.txt"
Copy-Item NOTICE "$stage\NOTICE.txt"
Copy-Item docs/third-party "$stage\third-party" -Recurse
Compress-Archive -Path "$stage\*" -DestinationPath "$stage.zip" -Force
Get-FileHash "$stage.zip" -Algorithm SHA256
```

使用 Inno Setup 7 生成用户级安装程序：

```powershell
$env:GLASSDESK_PACKAGE_STAGE = (Resolve-Path $stage).Path
ISCC.exe `
  /DAppIdValue=GlassDesk.Independent `
  "/DProductVersion=$version" `
  "/DFileVersion=$version.0" `
  "/DPackageBaseName=LG-Dsk-$version-win-x64" `
  /Oartifacts packaging/GlassDesk-Capture.iss
Remove-Item Env:GLASSDESK_PACKAGE_STAGE
```

用户数据不要放入发布目录或 ZIP。安装注册身份、默认程序目录与可执行文件名为兼容旧版而保留 GlassDesk；界面名称、快捷方式和产品元数据显示 LG Dsk。第三方代码与资源遵循各自许可，随包保留对应许可说明。

## 许可证与来源

原 GlassDesk 代码遵循 Apache License 2.0。仓库保留原许可证、版权和第三方声明，并在 `NOTICE` 与 `docs/third-party` 中记录来源。项目许可不授予第三方商标权利。详细源码来源见[`SOURCE-PROVENANCE.json`](SOURCE-PROVENANCE.json)。

- [上游公开源码](https://github.com/everlosttime/glassdesk-open-source)
- [上游发布与更新说明](https://github.com/everlosttime/glassdesk-updates)
- [本项目 1.4.0 移植记录](docs/LG-Dsk-1.4.0.md)
