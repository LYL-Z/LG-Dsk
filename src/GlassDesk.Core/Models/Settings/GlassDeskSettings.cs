namespace GlassDesk.Core.Models.Settings;

/// <summary>
/// 用户可配置内容的根，持久化为单个 JSON 文档。
/// </summary>
/// <remarks>
/// 这是我们自己的设置模型。它从不读写参考产品的
/// 配置（MacDock 的 <c>%APPDATA%\MacDock\settings.json</c> 或启动台的
/// localStorage），那些配置保持原样，老程序才能继续工作。
/// </remarks>
public sealed class GlassDeskSettings
{
    /// <summary>
    /// 磁盘上文档的 schema 版本。只要某次变更无法仅靠
    /// 默认值吸收就递增；旧文件在加载时迁移。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppearanceSettings Appearance { get; set; } = new();

    public DockSettings Dock { get; set; } = new();

    public LauncherSettings Launcher { get; set; } = new();

    public ShortcutSettings Shortcuts { get; set; } = new();

    public DisplaySettings Display { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    public DesktopTaskbarSettings DesktopTaskbar { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    /// <summary>
    /// 把为 null 的节替换为默认值。反序列化手工编辑过或写入不完整的
    /// 文件时，某个节可能为 null，尽管类型声明并非如此；
    /// 否则每个使用方都得自己做 null 检查。
    /// </summary>
    public void Normalize()
    {
        Appearance ??= new AppearanceSettings();
        Dock ??= new DockSettings();
        Launcher ??= new LauncherSettings();
        Shortcuts ??= new ShortcutSettings();
        Display ??= new DisplaySettings();
        Performance ??= new PerformanceSettings();
        DesktopTaskbar ??= new DesktopTaskbarSettings();
        Updates ??= new UpdateSettings();
        Launcher.FullscreenMode = false;
        Shortcuts.HotCornersEnabled = false;
        Shortcuts.MouseSideButtonsEnabled = false;

        if (!Enum.IsDefined(Appearance.Theme)) Appearance.Theme = ThemeMode.System;
        if (Appearance.GlassMaterial != GlassMaterialMode.FrostedGlass)
            Appearance.GlassMaterial = GlassMaterialMode.AndroidLiquidGlass;
        Dock.SizeScale = double.IsFinite(Dock.SizeScale)
            ? Math.Clamp(Dock.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;
        Dock.IconSize = double.IsFinite(Dock.IconSize) ? Math.Clamp(Dock.IconSize, 32, 96) : DockSettings.FixedIconSize;
        Dock.IconSpacing = double.IsFinite(Dock.IconSpacing) ? Math.Clamp(Dock.IconSpacing, 0, 24) : DockSettings.FixedIconSpacing;
        Dock.MagnificationPeak = double.IsFinite(Dock.MagnificationPeak)
            ? Math.Clamp(Dock.MagnificationPeak, 1, 2) : DockSettings.FixedMagnificationPeak;
        Dock.RecentApplicationsLimit = Math.Clamp(Dock.RecentApplicationsLimit, 0, 3);
        if (!Enum.IsDefined(Dock.FolderStackViewMode))
            Dock.FolderStackViewMode = DockFolderStackViewMode.Grid;
        if (!Enum.IsDefined(Dock.FolderStackSortMode))
            Dock.FolderStackSortMode = DockFolderStackSortMode.Name;
        Dock.AutoHideLeaveDelayMs = Math.Clamp(Dock.AutoHideLeaveDelayMs, 0, 3000);
        Dock.EdgeHotzoneSize = Math.Clamp(Dock.EdgeHotzoneSize, 1, 64);
        Appearance.DockGlassTransparency = double.IsFinite(Appearance.DockGlassTransparency)
            ? Math.Clamp(Appearance.DockGlassTransparency, 0, 1) : AppearanceSettings.DefaultDockGlassTransparency;
        Appearance.LauncherGlassTransparency = double.IsFinite(Appearance.LauncherGlassTransparency)
            ? Math.Clamp(Appearance.LauncherGlassTransparency, 0, 1) : AppearanceSettings.DefaultLauncherGlassTransparency;
        Appearance.GlassTintOpacity = double.IsFinite(Appearance.GlassTintOpacity)
            ? Math.Clamp(Appearance.GlassTintOpacity, 0, 1) : AppearanceSettings.DefaultGlassTintOpacity;
        Appearance.GlassBackgroundClarity = double.IsFinite(Appearance.GlassBackgroundClarity)
            ? Math.Clamp(Appearance.GlassBackgroundClarity, 0, 1) : AppearanceSettings.DefaultGlassBackgroundClarity;

        Shortcuts.Normalize();
        DesktopTaskbar.Normalize();
    }
}
