using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using GlassDesk.App.Updates;
using GlassDesk.AppServices.Updates;
using GlassDesk.Platform.Installation;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly HttpClient _updateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private DispatcherTimer? _updateCheckTimer;
    private UpdateWindow? _updateWindow;
    private UpdateRelease? _availableRelease;
    private UpdateClient? _availableUpdateClient;
    private bool _checkingUpdate;

    internal static Version ProductVersion => Version.TryParse(
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0].Split('-')[0], out var version)
        ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : new Version(1, 4, 2);

    private void InitializeUpdates()
    {
        _settings!.CheckForUpdatesRequested += OnCheckForUpdatesRequested;
        _settings.AutomaticUpdateCheckChanged += OnAutomaticUpdateCheckChanged;
        var enabled = _data!.Settings.Current.Updates.CheckAutomatically;
        _settings.ShowUpdateStatus(ProductVersion.ToString(), enabled
            ? "已启用自动检查；优先连接签名的静态正式版目录，必要时回退到 GitHub。下载与安装前仍会征求确认。"
            : "自动检查已关闭；可随时手动检查 LG Dsk 正式版本。");
        ConfigureAutomaticUpdateChecks(enabled, checkImmediately: true);
    }

    private void OnCheckForUpdatesRequested(object? sender, EventArgs e) => _ = CheckUpdatesAsync(manual: true);

    private void OnAutomaticUpdateCheckChanged(bool enabled) => ConfigureAutomaticUpdateChecks(enabled, checkImmediately: enabled);

    private void ConfigureAutomaticUpdateChecks(bool enabled, bool checkImmediately)
    {
        _updateCheckTimer ??= new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateCheckTimer.Stop();
        _updateCheckTimer.Tick -= OnAutomaticUpdateTimerTick;
        if (!enabled || _shuttingDown) return;
        _updateCheckTimer.Tick += OnAutomaticUpdateTimerTick;
        _updateCheckTimer.Start();
        if (checkImmediately)
            _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => _ = CheckUpdatesAsync(manual: false)));
    }

    private void OnAutomaticUpdateTimerTick(object? sender, EventArgs e) => _ = CheckUpdatesAsync(manual: false);

    private UpdateClient CreateUpdateClient()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "update-source.json");
        if (!File.Exists(file)) throw new InvalidDataException("LG Dsk 本地定制版未配置在线更新源。");
        if (new FileInfo(file).Length > 8192) throw new InvalidDataException("更新源配置过大。");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var source = document.RootElement.GetProperty("releasesApi").GetString();
        if (string.IsNullOrWhiteSpace(source)
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("LG Dsk 本地定制版未配置有效的 HTTPS 更新源。");
        return new UpdateClient(_updateHttp, uri, useStaticFeed: true);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_shuttingDown || _checkingUpdate) return;
        if (_updateWindow is not null) { if (manual) _updateWindow.Activate(); return; }
        if (manual && _availableRelease is not null && _availableUpdateClient is not null)
        {
            ShowUpdatePrompt(_availableUpdateClient, _availableRelease);
            return;
        }
        _checkingUpdate = true;
        _settings?.ShowUpdateStatus(ProductVersion.ToString(), "正在检查更新…", checking: true);
        try
        {
            var client = CreateUpdateClient();
            var release = await client.CheckAsync(ProductVersion, _updateLifetime.Token);
            if (_shuttingDown || _updateLifetime.IsCancellationRequested) return;
            if (release is null)
            {
                _availableRelease = null;
                _availableUpdateClient = null;
                _settings?.ShowUpdateStatus(ProductVersion.ToString(), "当前已是最新正式版本。", actionText: "重新检查");
                return;
            }
            _availableRelease = release;
            _availableUpdateClient = client;
            _settings?.ShowUpdateStatus(ProductVersion.ToString(),
                $"发现新版本 {release.Version}。点击下方按钮查看更新说明并选择是否下载安装。",
                actionText: $"下载并安装 {release.Version}");
            if (manual) ShowUpdatePrompt(client, release);
        }
        catch (OperationCanceledException)
        {
            if (!_shuttingDown) _settings?.ShowUpdateStatus(ProductVersion.ToString(), "检查更新已取消或超时，请稍后重试。");
        }
        catch (Exception ex)
        {
            if (!_shuttingDown) _settings?.ShowUpdateStatus(ProductVersion.ToString(), "无法检查更新，请确认网络连接后重试。" + (manual ? "\n" + ex.Message : string.Empty));
        }
        finally { _checkingUpdate = false; }
    }

    private void ShowUpdatePrompt(UpdateClient client, UpdateRelease release)
    {
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        var installation = new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty);
        _updateWindow = new UpdateWindow(release, installation.State == InstallationState.VerifiedInstalled,
            (progress, token) => InstallUpdateAsync(client, release, progress, token));
        _updateWindow.Icon = _settings?.Icon;
        _updateWindow.Closed += (_, _) =>
        {
            _updateWindow = null;
            if (_availableRelease is not null)
                _settings?.ShowUpdateStatus(ProductVersion.ToString(),
                    $"新版本 {_availableRelease.Version} 已就绪；可稍后继续。",
                    actionText: $"下载并安装 {_availableRelease.Version}");
        };
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private async Task InstallUpdateAsync(UpdateClient client, UpdateRelease release, IProgress<UpdateDownloadProgress> progress, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _updateLifetime.Token);
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LG Dsk", "Updates");
        var file = await client.DownloadAsync(release, cache, progress, cancellation.Token);
        var installation = new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty);
        var start = await UpdateInstaller.PrepareAsync(release, file, installation, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("安装程序未能启动。");
        _updateWindow?.MarkInstalling();
        ExitApplication();
    }

    private void DisposeUpdates()
    {
        if (_settings is not null)
        {
            _settings.CheckForUpdatesRequested -= OnCheckForUpdatesRequested;
            _settings.AutomaticUpdateCheckChanged -= OnAutomaticUpdateCheckChanged;
        }
        if (_updateCheckTimer is not null)
        {
            _updateCheckTimer.Stop();
            _updateCheckTimer.Tick -= OnAutomaticUpdateTimerTick;
            _updateCheckTimer = null;
        }
        _updateLifetime.Cancel();
        _updateWindow?.Close();
        _updateHttp.Dispose();
        _updateLifetime.Dispose();
    }
}
