using Microsoft.Win32;
using GlassDesk.Platform.Startup;

namespace GlassDesk.AppServices.Tests;

public sealed class StartupRegistrationTests : IDisposable
{
    private const string ValueName = "GlassDesk.Tests.Startup";
    private readonly string _registryPath = @"Software\GlassDesk.Tests\StartupRegistration\" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-Startup-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _runKey;
    private readonly string _executablePath;

    public StartupRegistrationTests()
    {
        Directory.CreateDirectory(_root);
        _executablePath = Path.Combine(_root, "GlassDesk Startup 测试.exe");
        File.WriteAllText(_executablePath, "test executable placeholder");
        _runKey = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true)!;
    }

    [Fact]
    public void 识别原有GlassDesk启动登记并且启停只管理本程序()
    {
        const string productName = "GlassDesk.Independent";
        const string captureName = "GlassDesk.Capture";
        const string captureCommand = "\"D:\\GlassDesk-Capture\\GlassDesk.exe\" --autostart";
        var productCommand = $"\"{Path.GetFullPath(_executablePath)}\" --autostart";
        _runKey.SetValue(productName, productCommand, RegistryValueKind.String);
        _runKey.SetValue(captureName, captureCommand, RegistryValueKind.String);
        var registration = new StartupRegistration(_runKey, StartupRegistration.ValueName);

        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_executablePath).State);
        Assert.True(registration.SetEnabled(_executablePath, enabled: true).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_executablePath).State);
        Assert.Equal(productCommand, _runKey.GetValue(productName));
        Assert.Equal(captureCommand, _runKey.GetValue(captureName));
        Assert.True(registration.SetEnabled(_executablePath, enabled: false).Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, registration.Query(_executablePath).State);
        Assert.Null(_runKey.GetValue(productName));
        Assert.Equal(captureCommand, _runKey.GetValue(captureName));
    }

    [Fact]
    public void 缺少值时查询为Missing()
    {
        var status = CreateRegistration().Query(_executablePath);

        Assert.Equal(StartupRegistrationState.Missing, status.State);
        Assert.Null(status.Error);
    }

    [Fact]
    public void 启用写入规范化完整命令并重复启用保持Owned()
    {
        var registration = CreateRegistration();

        var first = registration.SetEnabled(_executablePath, enabled: true);
        var repeated = registration.SetEnabled(_executablePath, enabled: true);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Equal(StartupRegistrationState.Owned, repeated.Status.State);
        Assert.Equal($"\"{Path.GetFullPath(_executablePath)}\" --autostart", _runKey.GetValue(ValueName));
        Assert.Equal(RegistryValueKind.String, _runKey.GetValueKind(ValueName));
    }

    [Fact]
    public void 禁用只删除本实例拥有的值且重复禁用幂等()
    {
        var registration = CreateRegistration();
        Assert.True(registration.SetEnabled(_executablePath, enabled: true).Succeeded);

        var first = registration.SetEnabled(_executablePath, enabled: false);
        var repeated = registration.SetEnabled(_executablePath, enabled: false);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Equal(StartupRegistrationState.Missing, repeated.Status.State);
        Assert.Null(_runKey.GetValue(ValueName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OtherTarget不被覆盖或删除(bool enabled)
    {
        var otherPath = Path.Combine(_root, "Other Copy", "GlassDesk.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.WriteAllText(otherPath, "other installation");
        var other = $"\"{otherPath}\" --autostart";
        _runKey.SetValue(ValueName, other, RegistryValueKind.String);

        var result = CreateRegistration().SetEnabled(_executablePath, enabled);

        Assert.False(result.Succeeded);
        Assert.Equal(StartupRegistrationState.OtherTarget, result.Status.State);
        Assert.Equal(other, _runKey.GetValue(ValueName));
    }

    [Fact]
    public void 外部修改后查询重新报告OtherTarget()
    {
        var registration = CreateRegistration();
        Assert.True(registration.SetEnabled(_executablePath, enabled: true).Succeeded);
        var otherPath = Path.Combine(_root, "Moved Copy", "GlassDesk.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.WriteAllText(otherPath, "other installation");
        var other = $"\"{otherPath}\" --autostart";
        _runKey.SetValue(ValueName, other, RegistryValueKind.String);

        Assert.Equal(StartupRegistrationState.OtherTarget, registration.Query(_executablePath).State);
    }

    [Fact]
    public void 已删除的旧版程序启动路径可以由用户显式替换()
    {
        var registration = CreateRegistration();
        var oldDirectory = Path.Combine(_root, "LG Dsk");
        Directory.CreateDirectory(oldDirectory);
        var oldPath = Path.Combine(oldDirectory, "GlassDesk.exe");
        var oldCommand = $"\"{oldPath}\" --autostart";
        _runKey.SetValue(ValueName, oldCommand, RegistryValueKind.String);

        var stale = registration.Query(_executablePath);
        var replaced = registration.SetEnabled(_executablePath, enabled: true);

        Assert.Equal(StartupRegistrationState.StaleProductTarget, stale.State);
        Assert.Equal(oldPath, stale.TargetPath);
        Assert.True(replaced.Succeeded, replaced.Error);
        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_executablePath).State);
        Assert.Equal($"\"{Path.GetFullPath(_executablePath)}\" --autostart", _runKey.GetValue(ValueName));
    }

    [Fact]
    public void 旧版未带参数的LGDsk启动命令仍识别为产品启动项并可显式切换()
    {
        var registration = CreateRegistration();
        var oldDirectory = Path.Combine(_root, "LG Dsk");
        Directory.CreateDirectory(oldDirectory);
        var oldPath = Path.Combine(oldDirectory, "GlassDesk.exe");
        File.WriteAllText(oldPath, "legacy product executable");
        var oldCommand = $"\"{oldPath}\"";
        _runKey.SetValue(ValueName, oldCommand, RegistryValueKind.String);

        var status = registration.Query(_executablePath);
        var replaced = registration.SetEnabled(_executablePath, enabled: true, replaceProductTarget: true);

        Assert.Equal(StartupRegistrationState.OtherProductTarget, status.State);
        Assert.Equal(oldPath, status.TargetPath);
        Assert.True(replaced.Succeeded, replaced.Error);
        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_executablePath).State);
        Assert.True(File.Exists(oldPath));
    }

    [Fact]
    public void 已删除的旧版程序启动路径可由用户显式清除()
    {
        var registration = CreateRegistration();
        var oldDirectory = Path.Combine(_root, "LG Dsk");
        Directory.CreateDirectory(oldDirectory);
        var oldPath = Path.Combine(oldDirectory, "LGDsk.exe");
        _runKey.SetValue(ValueName, $"\"{oldPath}\" --autostart", RegistryValueKind.String);

        var removed = registration.SetEnabled(_executablePath, enabled: false);

        Assert.True(removed.Succeeded, removed.Error);
        Assert.Equal(StartupRegistrationState.Missing, removed.Status.State);
        Assert.Null(_runKey.GetValue(ValueName));
    }

    [Fact]
    public void 另一份现存LGDsk安装仅在明确确认后切换登录启动目标()
    {
        var registration = CreateRegistration();
        var otherDirectory = Path.Combine(_root, "LG Dsk");
        Directory.CreateDirectory(otherDirectory);
        var otherPath = Path.Combine(otherDirectory, "LGDsk.exe");
        File.WriteAllText(otherPath, "other LG Dsk installation");
        var otherCommand = $"\"{otherPath}\" --autostart";
        _runKey.SetValue(ValueName, otherCommand, RegistryValueKind.String);

        Assert.Equal(StartupRegistrationState.OtherProductTarget, registration.Query(_executablePath).State);

        var withoutConfirmation = registration.SetEnabled(_executablePath, enabled: true);
        Assert.False(withoutConfirmation.Succeeded);
        Assert.Equal(StartupRegistrationState.OtherProductTarget, withoutConfirmation.Status.State);
        Assert.Equal(otherCommand, _runKey.GetValue(ValueName));

        var confirmedSwitch = registration.SetEnabled(_executablePath, enabled: true, replaceProductTarget: true);
        Assert.True(confirmedSwitch.Succeeded, confirmedSwitch.Error);
        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_executablePath).State);
        Assert.Equal($"\"{Path.GetFullPath(_executablePath)}\" --autostart", _runKey.GetValue(ValueName));
        Assert.True(File.Exists(otherPath));
        Assert.Equal("other LG Dsk installation", File.ReadAllText(otherPath));
    }

    [Fact]
    public void 不存在的其它程序路径仍受保护()
    {
        var otherPath = Path.Combine(_root, "Other App.exe");
        _runKey.SetValue(ValueName, $"\"{otherPath}\" --autostart", RegistryValueKind.String);

        var status = CreateRegistration().Query(_executablePath);

        Assert.Equal(StartupRegistrationState.OtherTarget, status.State);
    }

    [Fact]
    public void 无效可执行路径不写入注册表()
    {
        var missing = CreateRegistration().SetEnabled(Path.Combine(_root, "missing.exe"), enabled: true);
        var nonExecutable = Path.Combine(_root, "notes.txt");
        File.WriteAllText(nonExecutable, "not an executable");
        var wrongExtension = CreateRegistration().SetEnabled(nonExecutable, enabled: true);

        Assert.False(missing.Succeeded);
        Assert.False(wrongExtension.Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, CreateRegistration().Query(_executablePath).State);
        Assert.Empty(_runKey.GetValueNames());
    }

    [Fact]
    public void 超过Run命令限制时拒绝且不写入()
    {
        var longPath = @"C:\" + new string('x', 280) + ".exe";

        var result = CreateRegistration().SetEnabled(longPath, enabled: true);

        Assert.False(result.Succeeded);
        Assert.Contains("260", result.Error, StringComparison.Ordinal);
        Assert.Empty(_runKey.GetValueNames());
    }

    [Fact]
    public void 注册表拒绝写入时返回错误且保持Missing()
    {
        using var readOnly = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false)!;
        var registration = new StartupRegistration(readOnly, ValueName);

        var result = registration.SetEnabled(_executablePath, enabled: true);

        Assert.False(result.Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, result.Status.State);
        Assert.NotNull(result.Error);
        Assert.Null(_runKey.GetValue(ValueName));
    }

    [Fact]
    public void 非REGSZ目标作为OtherTarget保留()
    {
        _runKey.SetValue(ValueName, 12, RegistryValueKind.DWord);

        var status = CreateRegistration().Query(_executablePath);

        Assert.Equal(StartupRegistrationState.OtherTarget, status.State);
        Assert.Equal(12, _runKey.GetValue(ValueName));
    }

    private StartupRegistration CreateRegistration() => new(_runKey, ValueName);

    public void Dispose()
    {
        _runKey.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_registryPath, throwOnMissingSubKey: false);
        Directory.Delete(_root, recursive: true);
    }
}
