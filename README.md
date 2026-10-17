# LG Dsk

刘佑林的本地定制版，保留 GlassDesk 原有液态玻璃 Dock、启动台与设置界面，只更换品牌图标并加入应用前台策略和运行应用切换条。

[上游公开源码](https://github.com/everlosttime/glassdesk-open-source) · [上游更新说明](https://github.com/everlosttime/glassdesk-updates)

> 版本说明：本仓库以公开的 GlassDesk 1.3.1 源码为基线，并根据公开的 1.4.0 更新说明重建部分行为。上游没有公开 1.4.0 对应源码，因此 LG Dsk 1.4.0 不是上游 1.4.0 源码的逐行合并版本。具体来源与移植边界见 [LG Dsk 1.4.0 移植记录](docs/LG-Dsk-1.4.0.md)。

## LG Dsk 定制

- 原液态玻璃材质、深色外观、布局和现有设置均保留。
- 将 Z 图标换成浮雕 LG 字标，并加入低饱和陶土橙星芒。星芒是受用户 Claude 桌面风格启发的定制衍生图形，不是 Claude 或 Anthropic 官方标志。
- 普通应用成为前台时 Dock 收起，鼠标移到底边不会使它弹出；回到桌面后恢复 Dock。
- `Ctrl+Alt+Space` 显示或隐藏运行应用切换条；全屏应用时不覆盖画面。点选图标会激活应用并收起切换条。
- 启动台拖动应用到另一应用上停留 500 ms 后创建文件夹；提前松开则按普通排序处理。
- 不包含自愿赞助入口；本地定制版不连接上游自动更新源。
- 保持原有 16 ms 动画帧间隔和 100 ms 静止轮询，适配高 DPI 与 4K 桌面而不按 240 Hz 空转轮询。实际联想拯救者 RTX 5090 设备验收尚未进行。

## 上游版本与许可证

- v1.2.2：历史源码快照，包含启动台子图标 DPI 事件引发背景重建的修复。
- v1.3.1：本地代码基线，包含可独立开关的启动台、此电脑、桌面图标显隐入口、外观与后台资源管理改进，以及 Dock 在窗口最大化/还原后刷新玻璃背景的修复。

每个标签是对应版本的独立完整源码快照。旧开发历史、本机用户数据、截图、日志和验收记录不包含在这个仓库中。原始版本来源见 SOURCE-PROVENANCE.json。

## 许可证

上游 GlassDesk 原创代码采用 Apache License 2.0。LG Dsk 保留原版权、许可证和第三方声明，并记录本地修改。完整条款见 LICENSE；第三方代码与资源保留自身许可，详见 NOTICE 和 docs/third-party。

LG Dsk 不打包用户导入应用的图标，项目许可不授予第三方商标权利，也不代表与 Anthropic/Claude 或其他产品存在官方关联。

## 环境

- Windows 11 x64；源项目目标 SDK 为 Windows 10.0.26100.0，运行兼容下限为 Windows 11 Build 22000。
- .NET 10 SDK，global.json 固定工具链版本 10.0.401 并允许 latestFeature 滚动策略。
- 高级实时玻璃使用部分非公开 DWM 接口，并按能力提供兼容回退。各版本的现场验证不代表所有 Windows Build、显卡、HDR、多屏环境均已验证。

## 编译和运行

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet run --project src/GlassDesk.App/GlassDesk.App.csproj -c Release
```

程序数据位于实际运行目录旁的 Data。源码构建版也使用独立的输出目录数据。正式版按用户会话维持单实例，启动开发版前应先从托盘退出已安装版。

```powershell
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release
```

部分 Windows/WPF 验收测试会创建窗口并检查真实桌面服务，应在可交互的 Windows 会话中运行。

## 从源码生成便携版

```powershell
$version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
$version = @($version | Where-Object { $_ })[0]
dotnet publish src/GlassDesk.App/GlassDesk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true "-p:InformationalVersion=$version" -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/publish
Copy-Item LICENSE artifacts/publish/LICENSE.txt
Copy-Item NOTICE artifacts/publish/NOTICE.txt
Copy-Item docs/third-party artifacts/publish/third-party -Recurse
```

artifacts/publish 中的 GlassDesk.exe 可直接运行。不要把自己的 Data 目录放入分发包。框架与第三方资源的许可文件应随分发包保留。

## 从源码生成安装包

另安装 Inno Setup 7，并将 ISCC.exe 所在目录加入当前终端的 PATH。先执行上面的便携版发布命令，再执行：

```powershell
$env:GLASSDESK_PACKAGE_STAGE = (Resolve-Path artifacts/publish).Path
    ISCC /DAppIdValue=GlassDesk.Independent "/DProductVersion=$version" "/DFileVersion=$version.0" /DWindows11Compatibility=1 "/DPackageBaseName=LG-Dsk-$version-win-x64" /Oartifacts packaging/GlassDesk-Capture.iss
```

安装包支持选择目录。安装更新保留设置、应用库、布局和图标缓存，卸载也保留 Data。此源码打包流程不要求私有仓库的旧安装载荷。

## 修改着色器

TransitionGlass.hlsl 和编译后的 TransitionGlass.ps 均在 src/GlassDesk.UI/Effects 中。修改 HLSL 后执行 build/compile-transition-shader.ps1，再重新编译。

历史 GlassDesk.UiAcceptance 工程的目标框架低于正式项目，直接构建整个旧解决方案会报 NU1201。请按上述 App 项目构建；此问题不影响正式程序的源码构建。本次保留历史测试源码，未改写历史产品代码。
