using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Startup;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Settings;

/// <summary>
/// 设置中心。
/// </summary>
/// <remarks>
/// 阶段 1 展示页面结构与实时环境报告。这份报告不是
/// 装饰：在具体某台机器上，它是确认解析出哪个主题、
/// 系统材质是否被接受、数据写在哪里最快的方式——而这些
/// 阶段 2 都必须记录。
/// </remarks>
public partial class SettingsWindow : GlassWindow
{
    protected override bool UseOpaquePanel => true;
    protected override GlassPalette ResolveWindowPalette(GlassPalette inheritedPalette) =>
        ThemeService.Current is { IsHighContrast: true } theme
            ? theme.Palette
            : GlassPalette.CreateClaudeInspiredSettings();

    public static readonly DependencyProperty PlannedPagesProperty = DependencyProperty.Register(
        nameof(PlannedPages),
        typeof(IReadOnlyList<string>),
        typeof(SettingsWindow),
        new PropertyMetadata(Array.Empty<string>()));

    private GlassDeskDataStore? _dataStore;
    private StartupRegistration? _startupRegistration;
    private string _startupExecutablePath = string.Empty;
    private bool _loadingStartupRegistration;
    private bool _loadingDockControls;
    private bool _loadingLauncherControls;
    private bool _loadingAppearanceControls;
    private bool _dockValuesOutOfRange;
    private bool _launcherValuesOutOfRange;
    private readonly FrameworkElement[] _sectionHeadings;
    private bool _syncingCategoryFromScroll;
    private bool _sectionNavigationPending;
    private int _sectionNavigationVersion;
    private ThemeService? _settingsTheme;

    public event EventHandler<LauncherSettings>? LauncherSettingsChanged;

    public SettingsWindow()
    {
        _loadingAppearanceControls = true;
        try
        {
            InitializeComponent();
            AutostartCheck.IsEnabled = false;
        }
        finally
        {
            _loadingAppearanceControls = false;
        }

        _sectionHeadings = [AppearanceSectionHeading, DockSectionHeading, IslandSectionHeading, LauncherSectionHeading,
            ShortcutsSectionHeading, PerformanceSectionHeading, DesktopSectionHeading, BackupSectionHeading, AboutSectionHeading];
        PlannedPages =
        [
            "外观",
            "Dock",
            "应用岛",
            "启动台",
            "快捷操作",
            "性能与兼容",
            "桌面",
            "备份恢复",
            "关于",
        ];
        ResetPageButton.IsEnabled = false;
    }

    private void OnSettingsWindowLoaded(object sender, RoutedEventArgs e) => SyncCategoryFromScroll();

    private bool CanResetSelectedPage => SettingsPageList.SelectedItem is string page
        && page is "外观" or "Dock" or "应用岛" or "启动台" or "快捷操作" or "性能与兼容" or "桌面";

    private void UpdateResetPageButton() => ResetPageButton.IsEnabled = SettingsPageList.IsEnabled
        && _dataStore is not null && CanResetSelectedPage;

    /// <summary>计划书为设置中心固定的页面名称。</summary>
    public IReadOnlyList<string> PlannedPages
    {
        get => (IReadOnlyList<string>)GetValue(PlannedPagesProperty);
        private set => SetValue(PlannedPagesProperty, value);
    }

    /// <summary>设置中心沿用启动台的圆角半径，与其面板尺寸保持一致。</summary>
    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return 28;
    }

    /// <summary>把数据存储交给窗口，让它能报告数据存放的位置。</summary>
    public void AttachDataStore(GlassDeskDataStore dataStore, DataLoadReport? loadReport)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        _dataStore = dataStore;
        LoadDockControls();
        LoadLauncherControls();
        LoadAppearanceControls();
        LoadInteractionControls();
        LoadPerformanceControls();
        LoadUpdateControls();
        LoadDesktopStatus();
        RefreshDiagnostics(loadReport);
    }

    /// <summary>连接真实当前用户登录项；界面只显示当前进程路径对应的注册状态。</summary>
    public void AttachStartupRegistration(StartupRegistration registration, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(executablePath);

        _startupRegistration = registration;
        _startupExecutablePath = executablePath;
        AutostartExecutableText.Text = "当前程序：" + executablePath;
        RefreshStartupRegistration();
    }

    /// <summary>重新读取注册表，不使用设置文件中的缓存值。</summary>
    public void RefreshStartupRegistration()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshStartupRegistration);
            return;
        }

        _loadingStartupRegistration = true;
        try
        {
            if (_startupRegistration is null)
            {
                AutostartCheck.IsChecked = false;
                AutostartCheck.IsEnabled = false;
                AutostartStatus.Text = "当前程序的启动注册状态尚不可用。";
                return;
            }

            var status = _startupRegistration.Query(_startupExecutablePath);
            AutostartCheck.IsChecked = status.State == StartupRegistrationState.Owned;
            AutostartCheck.IsEnabled = status.State is StartupRegistrationState.Missing
                or StartupRegistrationState.Owned or StartupRegistrationState.OtherProductTarget
                or StartupRegistrationState.StaleProductTarget;
            AutostartStatus.Text = status.State switch
            {
                StartupRegistrationState.Missing => "未注册；默认关闭。",
                StartupRegistrationState.Owned => "已注册到当前用户登录项。Windows 的“启动应用”设置仍可禁用它。",
                StartupRegistrationState.StaleProductTarget => $"发现失效的旧版启动路径：{status.TargetPath}。勾选后会替换为当前 LG Dsk，不会影响其它启动项。",
                StartupRegistrationState.OtherProductTarget => $"登录启动项目前指向另一份 LG Dsk：{status.TargetPath}。勾选后会询问是否将此启动项切换到当前程序；另一份程序和它的数据不会被删除，但它将不再通过这个启动项自动运行。",
                StartupRegistrationState.OtherTarget => "同名启动项指向其它位置；为保护其它副本，此处不会覆盖或删除它。",
                _ => "无法读取或校验当前用户登录项：" + (status.Error ?? "原因未知。"),
            };
        }
        finally
        {
            _loadingStartupRegistration = false;
        }
    }

    public void PositionOnPrimary() => CenterOnPrimaryWorkArea();

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        if (_loadingStartupRegistration || _startupRegistration is null) return;

        var enabled = AutostartCheck.IsChecked == true;
        var replaceProductTarget = false;
        if (enabled)
        {
            var current = _startupRegistration.Query(_startupExecutablePath);
            if (current.State == StartupRegistrationState.OtherProductTarget)
            {
                var answer = MessageBox.Show(this,
                    $"当前用户的 LG Dsk 登录启动项指向另一份安装：\n{current.TargetPath}\n\n是否将这个启动项切换到当前正在运行的程序？另一份程序及其数据会保留，但它将不再由这个启动项自动启动。",
                    "切换 LG Dsk 登录启动项",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    RefreshStartupRegistration();
                    return;
                }
                replaceProductTarget = true;
            }
        }

        var result = _startupRegistration.SetEnabled(_startupExecutablePath, enabled, replaceProductTarget);
        RefreshStartupRegistration();
        if (!result.Succeeded)
        {
            AutostartStatus.Text = "未能更改登录启动项：" + (result.Error ?? "未知错误。")
                + Environment.NewLine + "当前实际状态：" + AutostartStatus.Text;
        }
    }

    private void LoadDockControls()
    {
        if (_dataStore is null) return;
        _loadingDockControls = true;
        try
        {
            var current = _dataStore.Settings.Current;
            var dock = current.Dock;
            ShowLauncherEntryCheckBox.IsChecked = dock.ShowLauncherEntry;
            ShowThisPcEntryCheckBox.IsChecked = dock.ShowThisPcEntry;
            ShowRecentApplicationsCheckBox.IsChecked = dock.ShowRecentApplications;
            RecentApplicationsLimitSlider.Value = dock.RecentApplicationsLimit;
            DockAutoHideCheck.IsChecked = dock.AutoHide;
            DockEdgeRevealCheck.IsChecked = dock.EdgeRevealEnabled;
            DockAutoHideDelaySlider.Value = dock.AutoHideLeaveDelayMs;
            DockScaleSlider.Value = dock.SizeScale;
            DockIconSizeSlider.Value = dock.IconSize;
            DockSpacingSlider.Value = dock.IconSpacing;
            DockPeakSlider.Value = dock.MagnificationPeak;
            DockScaleReadout.Text = $"整体 {dock.SizeScale:P0} · 当前图标 {dock.IconSize * dock.SizeScale:0} DIP";
            DockIconSizeReadout.Text = $"基础 {dock.IconSize:0} DIP · 实际 {dock.IconSize * dock.SizeScale:0} DIP";
            DockSpacingReadout.Text = $"基础间距 {dock.IconSpacing:0} DIP · 实际 {dock.IconSpacing * dock.SizeScale:0} DIP";
            DockPeakReadout.Text = $"{dock.MagnificationPeak:0.00}×";
            DockAutoHideDelayReadout.Text = $"{dock.AutoHideLeaveDelayMs:0} 毫秒";
            DockPlacementCombo.SelectedIndex = (int)dock.Placement;
            var display = PrimaryDisplaySession is { IsAvailable: true } session
                ? session.Display
                : PrimaryDisplaySession is null ? DisplayService.Primary() : null;
            var availableWidthDip = display is null
                ? DockSettings.MaximumFrameLengthDip
                : display.ToDips((int)display.WorkArea.Width) - 32;
            var maximum = Math.Min(DockSettings.MaximumFrameLengthDip, Math.Max(64, availableWidthDip));
            var leftCount = 2 + (dock.ShowLauncherEntry ? 1 : 0) + (dock.ShowThisPcEntry ? 1 : 0);
            var dividerCount = dock.ShowRecentApplications && dock.RecentApplicationsLimit > 0 ? 2 : 1;
            var iconSize = dock.IconSize * dock.SizeScale;
            var spacing = dock.IconSpacing * dock.SizeScale;
            var peakExtra = iconSize * (dock.MagnificationPeak - 1) * 1.5;
            var leadingLength = leftCount * iconSize + (leftCount - 1) * spacing + peakExtra;
            var trailingCount = 2;
            var trailingLength = trailingCount * iconSize + (trailingCount - 1) * spacing + peakExtra;
            var pinnedMinimum = _dataStore.Layouts.Dock.PinnedItemIds.Count > 0
                ? iconSize + peakExtra
                : 0;
            var minimumContent = leadingLength + trailingLength
                + dividerCount * 17 * dock.SizeScale + pinnedMinimum;
            if (dock.ShowRecentApplications && dock.RecentApplicationsLimit > 0)
                minimumContent += iconSize + peakExtra;
            minimumContent += 20 * dock.SizeScale;
            var minimum = Math.Min(Math.Max(DockSettings.MinimumFrameLengthDip, minimumContent), maximum);
            DockFrameLengthSlider.Minimum = minimum;
            DockFrameLengthSlider.Maximum = maximum;
            var frameLengthValid = dock.FrameLengthDip == 0
                || Within(dock.FrameLengthDip, 64, DockSettings.MaximumFrameLengthDip);
            DockFrameLengthSlider.Value = dock.FrameLengthDip > 0
                ? Math.Clamp(dock.FrameLengthDip, minimum, maximum)
                : minimum;
            DockFrameLengthReadout.Text = dock.FrameLengthDip > 0
                ? $"固定外框：{DockFrameLengthSlider.Value:0} DIP"
                : "当前随图标内容自动调整；拖动滑块后固定外框长度。";
            _dockValuesOutOfRange = !Enum.IsDefined(dock.Placement)
                || !Within(dock.OffsetX, -200, 200) || !Within(dock.OffsetY, -200, 200)
                || !Within(dock.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale)
                || !Within(dock.IconSize, 32, 96) || !Within(dock.IconSpacing, 0, 24)
                || !Within(dock.MagnificationPeak, 1, 2) || !Within(dock.RecentApplicationsLimit, 0, 3)
                || !Within(dock.AutoHideLeaveDelayMs, 0, 3000)
                || !frameLengthValid;
            DockOffsetXSlider.Value = Coerce(dock.OffsetX, -200, 200);
            DockOffsetYSlider.Value = Coerce(dock.OffsetY, -200, 200);
            DockSettingsStatus.Text = _dockValuesOutOfRange
                ? "配置文件中有超出支持范围的数值，已显示为最近的可用值；请先恢复当前页默认，避免覆盖原数据。"
                : dock.AutoHide && !dock.EdgeRevealEnabled
                    ? "自动隐藏需要边缘唤出；请启用边缘唤出，避免 Dock 隐藏后无法直接显示。"
                    : "外框长度、最近应用、图标尺寸、间距和放大效果会立即保存并应用。";
        }
        finally { _loadingDockControls = false; }
    }

    private static bool Within(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    private static double Coerce(double value, double minimum, double maximum) => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;

    private void LoadLauncherControls()
    {
        if (_dataStore is null) return;
        _loadingLauncherControls = true;
        try
        {
            var settings = _dataStore.Settings.Current.Launcher;
            _launcherValuesOutOfRange = settings.Rows is < 2 or > 10 || settings.Columns is < 2 or > 12;
            LauncherRowsSlider.Value = Math.Clamp(settings.Rows, 2, 10);
            LauncherColumnsSlider.Value = Math.Clamp(settings.Columns, 2, 12);
            LauncherFocusSearchCheck.IsChecked = settings.FocusSearchOnOpen;
            LauncherRememberPageCheck.IsChecked = settings.RememberLastPage;
            LauncherSettingsStatus.Text = _launcherValuesOutOfRange
                ? "配置文件中的网格数值超出支持范围，已显示为最近的可用值；请先恢复当前页默认，避免覆盖原数据。"
                : "修改后立即保存并应用到启动台。";
        }
        finally { _loadingLauncherControls = false; }
    }

    private void OnLauncherControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherControls || _dataStore is null) return;
        if (_launcherValuesOutOfRange) { LoadLauncherControls(); return; }
        var current = _dataStore.Settings.Current;
        var old = current.Launcher;
        var next = new LauncherSettings
        {
            Rows = (int)LauncherRowsSlider.Value,
            Columns = (int)LauncherColumnsSlider.Value,
            WindowWorkAreaRatio = old.WindowWorkAreaRatio,
            FullscreenMode = false,
            FocusSearchOnOpen = LauncherFocusSearchCheck.IsChecked == true,
            RememberLastPage = LauncherRememberPageCheck.IsChecked == true,
        };
        var candidate = SettingsPageEditor.Clone(current);
        candidate.Launcher = next;
        var validationError = SettingsPageEditor.Validate(candidate);
        if (validationError is not null)
        {
            LoadLauncherControls();
            LauncherSettingsStatus.Text = "设置无效，未保存：" + validationError;
            return;
        }
        if (old.Rows == next.Rows && old.Columns == next.Columns && old.FullscreenMode == next.FullscreenMode
            && old.FocusSearchOnOpen == next.FocusSearchOnOpen && old.RememberLastPage == next.RememberLastPage) return;
        try
        {
            current.Launcher = next;
            _dataStore.Settings.Save();
            LauncherSettingsChanged?.Invoke(this, next);
            LauncherSettingsStatus.Text = $"已保存：{next.Rows} 行 × {next.Columns} 列。";
        }
        catch (Exception ex)
        {
            current.Launcher = old;
            LoadLauncherControls();
            LauncherSettingsStatus.Text = "保存失败，已恢复原设置：" + ex.Message;
        }
    }

    private void OnSettingsPageSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateResetPageButton();
        if (_syncingCategoryFromScroll || SettingsPageList.SelectedIndex < 0) return;
        ScrollToSection(_sectionHeadings[SettingsPageList.SelectedIndex]);
    }

    private void OnSettingsScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (!IsLoaded || _sectionNavigationPending || !ReferenceEquals(e.OriginalSource, SettingsScroll)) return;
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            SyncCategoryFromScroll();
    }

    private void SyncCategoryFromScroll()
    {
        var index = 0;
        for (var i = 1; i < _sectionHeadings.Length; i++)
        {
            if (_sectionHeadings[i].TranslatePoint(new Point(), SettingsScroll).Y > 13) break;
            index = i;
        }
        if (SettingsScroll.ScrollableHeight > 0
            && SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - .5)
            index = _sectionHeadings.Length - 1;
        _syncingCategoryFromScroll = true;
        try { SettingsPageList.SelectedIndex = index; }
        finally { _syncingCategoryFromScroll = false; }
        UpdateResetPageButton();
    }

    private void ScrollToSection(FrameworkElement heading)
    {
        var version = ++_sectionNavigationVersion;
        _sectionNavigationPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (version != _sectionNavigationVersion) return;
            var relative = heading.TranslatePoint(new Point(0, 0), SettingsScroll);
            SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset + relative.Y - 12);
            // ScrollViewer processes the queued offset during layout. Keep that event from
            // changing an explicitly selected category when its heading is near the bottom.
            Dispatcher.BeginInvoke(() =>
            {
                if (version == _sectionNavigationVersion) _sectionNavigationPending = false;
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnDockControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingDockControls || _dataStore is null || DockPlacementCombo.SelectedIndex < 0) return;
        if (_dockValuesOutOfRange) { LoadDockControls(); return; }

        var loadingDependency = _loadingDockControls;
        _loadingDockControls = true;
        try
        {
            if (ReferenceEquals(sender, DockAutoHideCheck) && DockAutoHideCheck.IsChecked == true)
                DockEdgeRevealCheck.IsChecked = true;
            else if (ReferenceEquals(sender, DockEdgeRevealCheck) && DockEdgeRevealCheck.IsChecked != true)
                DockAutoHideCheck.IsChecked = false;
        }
        finally { _loadingDockControls = loadingDependency; }

        var current = _dataStore.Settings.Current;
        var previousDock = current.Dock;
        var previousCorner = current.Appearance.DockCornerRadius;
        var next = new DockSettings
        {
            SizeScale = DockScaleSlider.Value,
            ShowLauncherEntry = ShowLauncherEntryCheckBox.IsChecked == true,
            ShowThisPcEntry = ShowThisPcEntryCheckBox.IsChecked == true,
            ShowDesktopIconsEntry = previousDock.ShowDesktopIconsEntry,
            ShowRecentApplications = ShowRecentApplicationsCheckBox.IsChecked == true,
            RecentApplicationsLimit = (int)Math.Round(RecentApplicationsLimitSlider.Value),
            IconSize = DockIconSizeSlider.Value,
            IconSpacing = DockSpacingSlider.Value,
            MagnificationPeak = DockPeakSlider.Value,
            FrameLengthDip = ReferenceEquals(sender, DockFrameLengthSlider)
                ? DockFrameLengthSlider.Value
                : previousDock.FrameLengthDip,
            Placement = (DockPlacement)DockPlacementCombo.SelectedIndex,
            OffsetX = DockOffsetXSlider.Value,
            OffsetY = DockOffsetYSlider.Value,
            AutoHide = DockAutoHideCheck.IsChecked == true,
            AutoHideLeaveDelayMs = (int)Math.Round(DockAutoHideDelaySlider.Value),
            EdgeRevealEnabled = DockEdgeRevealCheck.IsChecked == true,
            EdgeHotzoneSize = previousDock.EdgeHotzoneSize,
            BlockRevealWhenCovered = previousDock.BlockRevealWhenCovered,
            TargetDisplayId = previousDock.TargetDisplayId,
            ShowRunningIndicator = previousDock.ShowRunningIndicator,
        };

        var candidate = SettingsPageEditor.Clone(current);
        candidate.Dock = next;
        candidate.Appearance.DockCornerRadius = 40;
        var validationError = SettingsPageEditor.Validate(candidate);
        if (validationError is not null)
        {
            LoadDockControls();
            DockSettingsStatus.Text = "设置无效，未保存：" + validationError;
            return;
        }

        try
        {
            current.Dock = next;
            current.Appearance.DockCornerRadius = 40;
            _dataStore.Settings.Save();
            DockSettingsHost.Current?.Update(next);
            if (Math.Abs(previousCorner - current.Appearance.DockCornerRadius) > 0.001)
                ThemeService.Current?.Update(current.Appearance);
            DockFrameLengthReadout.Text = next.FrameLengthDip > 0
                ? $"固定外框：{DockFrameLengthSlider.Value:0} DIP"
                : "当前随图标内容自动调整；拖动滑块后固定外框长度。";
            LoadDockControls();
            DockSettingsStatus.Text = $"已保存；图标 {next.IconSize * next.SizeScale:0} DIP，间距 {next.IconSpacing * next.SizeScale:0} DIP，放大 {next.MagnificationPeak:0.00}×。";
        }
        catch (Exception ex)
        {
            current.Dock = previousDock;
            current.Appearance.DockCornerRadius = previousCorner;
            LoadDockControls();
            DockSettingsStatus.Text = "保存失败，已恢复原设置：" + ex.Message;
        }
    }

    private void OnDockAutoLengthClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null || _dockValuesOutOfRange) return;
        var snapshot = SettingsPageEditor.Clone(_dataStore.Settings.Current);
        _dataStore.Settings.Current.Dock.FrameLengthDip = 0;
        SavePageSnapshot(snapshot, "Dock", DockSettingsStatus);
    }

    /// <summary>重建环境报告。</summary>
    public void RefreshDiagnostics(DataLoadReport? loadReport = null)
    {
        var theme = ThemeService.Current;

        // 全程使用固定区域性：这是一份含版本号、
        // 计数与路径的诊断转储，无论用户处于哪种区域设置下读起来都应一致。
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();

        builder.AppendLine(culture, $"Windows：{WindowsCapabilities.OsVersion}（Build {WindowsCapabilities.BuildNumber}）");
        builder.AppendLine(culture, $"系统背景材质：{(WindowsCapabilities.SupportsSystemBackdrop ? "受支持（Build 22621 及以上）" : "不支持，使用自绘面板")}");
        builder.AppendLine(culture, $"窗口圆角：{(WindowsCapabilities.SupportsWindowCornerPreference ? "由 DWM 提供" : "不受支持，使用自绘圆角")}");

        if (theme is not null)
        {
            builder.AppendLine(culture, $"主题：{theme.Appearance.Theme}（当前 {(theme.IsDark ? "深色" : "浅色")}）");
            builder.AppendLine(culture, $"面板：{(theme.UseOpaquePanels ? "不透明" : "半透明玻璃")}");
            builder.AppendLine(culture, $"动画：{(theme.AnimationsEnabled ? "开启" : "已减少")}");
        }

        builder.AppendLine(culture, $"窗口玻璃做法：{(SurfaceMode == GlassSurfaceMode.Acrylic ? "亚克力背景模糊" : "自绘半透明面板（无背景模糊）")}");

        if (_dataStore is not null)
        {
            builder.AppendLine(culture, $"数据目录：{_dataStore.Paths.RootDirectory}");
            builder.AppendLine(culture, $"应用库条目：{_dataStore.Library.Items.Count}");

            if (loadReport?.HasNotices == true)
            {
                builder.AppendLine();
                builder.AppendLine("数据加载提示：");
                foreach (var notice in loadReport.Notices)
                {
                    builder.AppendLine(culture, $"· {notice}");
                }
            }
        }

        DiagnosticsText.Text = builder.ToString().TrimEnd();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _settingsTheme = ThemeService.Current;
        if (_settingsTheme is not null) _settingsTheme.Changed += OnSettingsThemeChanged;
        ApplySettingsControlPalette();
        RefreshDiagnostics();
        RefreshStartupRegistration();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_settingsTheme is not null) _settingsTheme.Changed -= OnSettingsThemeChanged;
        _settingsTheme = null;
        base.OnClosed(e);
    }

    private void OnSettingsThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ApplySettingsControlPalette);
            return;
        }
        ApplySettingsControlPalette();
    }

    private void ApplySettingsControlPalette()
    {
        if (!IsInitialized) return;
        var highContrast = ThemeService.Current?.IsHighContrast == true;
        var background = highContrast ? SystemColors.WindowColor : Color.FromRgb(0xFF, 0xFB, 0xF5);
        var hover = highContrast ? SystemColors.HighlightColor : Color.FromRgb(0xFF, 0xF5, 0xE8);
        var edge = highContrast ? SystemColors.WindowTextColor : Color.FromRgb(0xD8, 0xCA, 0xBB);
        var active = highContrast ? SystemColors.HighlightColor : Color.FromRgb(0xC9, 0x6F, 0x52);
        var text = highContrast ? SystemColors.WindowTextColor : Color.FromRgb(0x38, 0x2F, 0x2A);
        var muted = highContrast ? SystemColors.GrayTextColor : Color.FromRgb(0x78, 0x6D, 0x63);
        var popup = highContrast ? SystemColors.WindowColor : Color.FromRgb(0xFB, 0xF8, 0xF2);
        var hoverEnd = highContrast ? SystemColors.HighlightColor : Color.FromRgb(0xEC, 0xE0, 0xD0);
        var fill = new LinearGradientBrush(background, highContrast ? background : Color.FromRgb(0xF3, 0xEB, 0xDD), 90);
        var hoverFill = new LinearGradientBrush(hover, hoverEnd, 90);
        fill.Freeze(); hoverFill.Freeze();
        Resources["LiquidControlFill"] = fill;
        Resources["LiquidControlHover"] = hoverFill;
        Resources["LiquidControlEdge"] = Brush(edge);
        Resources["LiquidControlEdgeActive"] = Brush(active);
        Resources["LiquidControlText"] = Brush(text);
        Resources["LiquidControlMuted"] = Brush(muted);
        Resources["LiquidControlPopup"] = Brush(popup);
        Resources["LiquidControlSelected"] = Brush(highContrast ? SystemColors.HighlightColor : Color.FromArgb(0x33, 0xC9, 0x6F, 0x52));
        Resources["LiquidControlAccent"] = Brush(active);

        static SolidColorBrush Brush(Color color)
        {
            var value = new SolidColorBrush(color);
            value.Freeze();
            return value;
        }
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        // 窗口没有标题栏，所以拖动面板主体即可移动它。
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
