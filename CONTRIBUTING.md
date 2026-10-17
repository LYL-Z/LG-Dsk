# 贡献指南

LG Dsk 面向 Windows 11 x64，主要由 C#、WPF、.NET 10 和 Windows API 构成。贡献前请先阅读 [README](README.md)、[行为准则](CODE_OF_CONDUCT.md) 与[安全政策](SECURITY.md)。

## 提交 Issue

- 使用仓库提供的缺陷或功能请求模板。
- 描述 Windows 版本、显示缩放、显示器数量与刷新率、GPU/驱动版本，以及可复现步骤。
- 截图或日志提交前，删除用户名、完整文件路径、应用列表及其他个人信息。
- 安全漏洞不要公开提交 Issue，按 `SECURITY.md` 联系维护者。

## 本地开发

- Windows 11 x64。
- .NET SDK 10.0.401（由 `global.json` 固定）。
- 修改安装程序时使用 Inno Setup 7.x。

```powershell
dotnet build GlassDesk.slnx -c Release
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release
```

桌面验收项目会创建或激活窗口；请只在隔离的 Windows 桌面环境中运行。当前没有配置 GitHub Actions，因此本地构建结果不等于远端 CI 通过。

## Pull Request

1. 为每个问题创建独立分支；提交标题使用简洁前缀，例如 `fix:`、`docs:` 或 `refactor:`。
2. 尽量提交小而可审查的变更，说明原因、实现方式、用户可见影响和验证结果。
3. 不提交 `Data/`、用户图标、桌面截图、日志、安装包缓存或私人配置。
4. 触及存储格式、安装身份、用户数据迁移、窗口输入或安全边界时，附上对应测试或可复现证据。
5. 保留 Apache-2.0 与第三方许可证/NOTICE；新增第三方依赖时说明用途和许可证。
6. 维护者会审阅正确性、兼容性、数据安全和 Windows 桌面行为；项目不承诺固定审查或响应时限。

提交 PR 即表示你有权提交相关代码，并同意其按仓库 Apache-2.0 许可证发布。
