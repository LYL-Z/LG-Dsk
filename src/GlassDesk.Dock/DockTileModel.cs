using System.ComponentModel;
using System.Windows;
using GlassDesk.Core.Models;

namespace GlassDesk.Dock;

/// <summary>
/// 一枚 Dock 磁贴的显示数据。
/// </summary>
/// <remarks>
/// 刻意做成了最小视图模型：尺寸在构建时确定（设置变更时整行重建），
/// 图标缓存未命中时可在后台加载后更新，而<strong>运行状态点</strong>也通过属性通知单独刷新——
/// 调和事件只改圆点，不重建磁贴，避免每次窗口生灭都重载一遍图标。
/// </remarks>
public sealed class DockTileModel : INotifyPropertyChanged
{
    private Visibility _runningVisibility = Visibility.Collapsed;
    private double _canvasLeft;
    private double _canvasTop;
    private double _magnificationScale = 1;
    private int _magnificationZIndex;
    private AppRuntimeState _runtimeState = AppRuntimeState.Unknown;
    private System.Windows.Media.ImageSource? _icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>应用库条目 id。点击时据此走启动/激活。</summary>
    public string EntryId { get; init; } = string.Empty;

    /// <summary>内置入口的符号字形（Segoe Fluent Icons）。普通磁贴为 null（用图标）。</summary>
    public string? Glyph { get; init; }

    /// <summary>目标已失效（R-07）。失效磁贴的图标降透明显示。</summary>
    public bool IsMissing { get; init; }

    /// <summary>该条目是可接收拖入文件的程序启动项。</summary>
    public bool AcceptsFileDrop { get; init; }

    /// <summary>文件夹磁贴的真实接收目录；空值表示不是文件夹拖放目标。</summary>
    public string? FolderDropPath { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>悬停提示（D-06）：完整名称，不截断；失效条目附说明。</summary>
    public string ToolTipText { get; init; } = string.Empty;

    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value, nameof(Icon));
    }

    /// <summary>运行状态点的颜色。来自当前调色板的前景色。</summary>
    public System.Windows.Media.Brush? DotBrush { get; init; }

    /// <summary>图标边长，DIP，来自程序坞设置。</summary>
    public double IconSize { get; init; } = 48;

    /// <summary>按钮静止高度，包含运行状态点。</summary>
    public double TileHeight { get; init; } = 64;

    /// <summary>当前显示帧的 Canvas 左坐标。</summary>
    public double CanvasLeft
    {
        get => _canvasLeft;
        set => SetField(ref _canvasLeft, value, nameof(CanvasLeft));
    }

    /// <summary>当前显示帧的 Canvas 上坐标。</summary>
    public double CanvasTop
    {
        get => _canvasTop;
        set => SetField(ref _canvasTop, value, nameof(CanvasTop));
    }

    /// <summary>当前显示帧的图标缩放。</summary>
    public double MagnificationScale
    {
        get => _magnificationScale;
        set => SetField(ref _magnificationScale, value, nameof(MagnificationScale));
    }

    /// <summary>当前显示帧的绘制层级。</summary>
    public int MagnificationZIndex
    {
        get => _magnificationZIndex;
        set => SetField(ref _magnificationZIndex, value, nameof(MagnificationZIndex));
    }

    /// <summary>运行状态点（D-07）的可见性。调和事件单独刷新它。</summary>
    public Visibility RunningVisibility
    {
        get => _runningVisibility;
        set
        {
            if (_runningVisibility == value)
            {
                return;
            }

            _runningVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningAutomationStatus)));
        }
    }

    /// <summary>进程与窗口合并后的可信运行状态。</summary>
    public AppRuntimeState RuntimeState
    {
        get => _runtimeState;
        set
        {
            if (_runtimeState == value) return;
            _runtimeState = value;
            var running = value is AppRuntimeState.RunningWithoutWindow or AppRuntimeState.RunningWithWindows;
            var visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (_runningVisibility != visibility)
            {
                _runningVisibility = visibility;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVisibility)));
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RuntimeState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningAutomationStatus)));
        }
    }

    /// <summary>供辅助技术读取的状态，与图标下方的运行点保持一致。</summary>
    public string RunningAutomationStatus => RuntimeState switch
    {
        AppRuntimeState.NotApplicable => "运行状态不适用",
        AppRuntimeState.Stopped => "未运行",
        AppRuntimeState.RunningWithoutWindow => "正在运行，但没有可匹配窗口",
        AppRuntimeState.RunningWithWindows => "正在运行，有可匹配窗口",
        _ => "运行状态未知",
    };

    private void SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
