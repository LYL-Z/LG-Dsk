using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock;

/// <summary>Accessible, refreshable folder-stack presentation for grid, list, and fan modes.</summary>
internal sealed class FolderStackPanel : Border
{
    private readonly ContentControl _itemsHost = new();
    private readonly TextBlock _status = new();
    private readonly Dictionary<DockFolderStackViewMode, ToggleButton> _modeButtons = [];
    private readonly Dictionary<DockFolderStackSortMode, ToggleButton> _sortButtons = [];
    private readonly Brush _foreground;
    private readonly Brush _secondaryForeground;
    private IReadOnlyList<FolderStackItem> _items = [];
    private string? _error;
    private bool _hasMore;
    private DockFolderStackViewMode _mode;
    private DockFolderStackSortMode _sortMode;

    public FolderStackPanel(
        string title,
        DockFolderStackViewMode initialMode,
        DockFolderStackSortMode initialSortMode,
        Brush background,
        Brush borderBrush,
        Brush foreground,
        Brush secondaryForeground)
    {
        _foreground = foreground;
        _secondaryForeground = secondaryForeground;
        CornerRadius = new CornerRadius(12);
        Background = background;
        BorderBrush = borderBrush;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(8);
        MinWidth = 260;
        MaxWidth = 390;
        MaxHeight = 440;
        Focusable = true;
        AutomationProperties.SetName(this, $"{title}文件堆栈");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 4) };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
        };
        AutomationProperties.SetName(titleBlock, title);
        header.Children.Add(titleBlock);

        var openFolder = HeaderButton("打开", "在文件管理器中打开这个文件夹", OpenFolderRequested);
        DockPanel.SetDock(openFolder, System.Windows.Controls.Dock.Right);
        header.Children.Add(openFolder);
        var refresh = HeaderButton("↻", "刷新文件夹内容", RefreshRequested);
        DockPanel.SetDock(refresh, System.Windows.Controls.Dock.Right);
        header.Children.Add(refresh);
        layout.Children.Add(header);

        var modeBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AddModeButton(modeBar, DockFolderStackViewMode.Grid, "网格");
        AddModeButton(modeBar, DockFolderStackViewMode.List, "列表");
        AddModeButton(modeBar, DockFolderStackViewMode.Fan, "扇形");

        var sortBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AddSortButton(sortBar, DockFolderStackSortMode.Name, "名称", "按名称升序排序");
        AddSortButton(sortBar, DockFolderStackSortMode.ModifiedDate, "日期", "按最后修改时间排序，最近修改的项目在前");

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(sortBar, System.Windows.Controls.Dock.Right);
        toolbar.Children.Add(sortBar);
        toolbar.Children.Add(modeBar);
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);

        _status.Margin = new Thickness(3, 0, 3, 4);
        _status.FontSize = 10;
        _status.Foreground = _secondaryForeground;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Text = "正在读取文件夹…";
        Grid.SetRow(_status, 2);
        layout.Children.Add(_status);

        _itemsHost.MinHeight = 80;
        Grid.SetRow(_itemsHost, 3);
        layout.Children.Add(_itemsHost);
        Child = layout;
        SetViewMode(initialMode, notify: false);
        SetSortMode(initialSortMode, notify: false);
    }

    public Func<DockFolderStackViewMode, bool>? ViewModeChanged { get; set; }
    public Func<DockFolderStackSortMode, bool>? SortModeChanged { get; set; }
    public event Action? RefreshRequested;
    public event Action? OpenFolderRequested;
    public event Action<string>? ItemInvoked;

    public void SetLoading()
    {
        _status.Text = _items.Count > 0 ? "正在刷新…" : "正在读取文件夹…";
        if (_items.Count == 0)
            _itemsHost.Content = StatusContent(_status.Text);
    }

    public void SetSnapshot(FolderStackSnapshot snapshot)
    {
        _items = snapshot.Items;
        _hasMore = snapshot.HasMore;
        _error = snapshot.Error;
        RenderItems();
    }

    private Button HeaderButton(string text, string accessibleName, Action? action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = text == "打开" ? 42 : 28,
            Height = 25,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(5, 1, 5, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _foreground,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            ToolTip = accessibleName,
        };
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) => action?.Invoke();
        return button;
    }

    private void AddModeButton(Panel host, DockFolderStackViewMode mode, string label)
    {
        var button = new ToggleButton
        {
            Content = label,
            MinWidth = 48,
            Height = 25,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(7, 1, 7, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _foreground,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            ToolTip = $"以{label}方式显示文件夹内容",
        };
        AutomationProperties.SetName(button, $"{label}视图");
        button.Click += (_, _) => SetViewMode(mode, notify: true);
        _modeButtons.Add(mode, button);
        host.Children.Add(button);
    }

    private void AddSortButton(Panel host, DockFolderStackSortMode mode, string label, string description)
    {
        var button = new ToggleButton
        {
            Content = label,
            MinWidth = 46,
            Height = 25,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _foreground,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            ToolTip = description,
        };
        AutomationProperties.SetName(button, description);
        button.Click += (_, _) => SetSortMode(mode, notify: true);
        _sortButtons.Add(mode, button);
        host.Children.Add(button);
    }

    private void SetViewMode(DockFolderStackViewMode mode, bool notify)
    {
        if (!Enum.IsDefined(mode)) mode = DockFolderStackViewMode.Grid;
        var previous = _mode;
        var changed = _mode != mode;
        _mode = mode;
        foreach (var pair in _modeButtons) pair.Value.IsChecked = pair.Key == mode;
        if (_itemsHost is not null) RenderItems();
        if (notify && changed && ViewModeChanged?.Invoke(mode) is false)
        {
            _mode = previous;
            foreach (var pair in _modeButtons) pair.Value.IsChecked = pair.Key == previous;
            RenderItems();
        }
    }

    private void SetSortMode(DockFolderStackSortMode mode, bool notify)
    {
        if (!Enum.IsDefined(mode)) mode = DockFolderStackSortMode.Name;
        var previous = _sortMode;
        var changed = _sortMode != mode;
        _sortMode = mode;
        foreach (var pair in _sortButtons) pair.Value.IsChecked = pair.Key == mode;
        if (notify && changed && SortModeChanged?.Invoke(mode) is false)
        {
            _sortMode = previous;
            foreach (var pair in _sortButtons) pair.Value.IsChecked = pair.Key == previous;
        }
    }

    private void RenderItems()
    {
        if (_error is not null)
        {
            _status.Text = _error;
            _itemsHost.Content = StatusContent(_error);
            return;
        }

        if (_items.Count == 0)
        {
            _status.Text = "0 项";
            _itemsHost.Content = StatusContent("（空文件夹）");
            return;
        }

        var itemCount = _hasMore
            ? $"至少 {FolderStackSnapshot.MaximumItems + 1} 项"
            : $"{_items.Count} 项";
        if (_mode == DockFolderStackViewMode.Fan && _items.Count > DockFolderStackLayout.MaximumFanItems)
            _status.Text = $"{itemCount} · 扇形展示前 {DockFolderStackLayout.MaximumFanItems} 项";
        else if (_hasMore)
            _status.Text = $"{itemCount} · 当前最多显示 {FolderStackSnapshot.MaximumItems} 项";
        else
            _status.Text = itemCount;

        _itemsHost.Content = _mode switch
        {
            DockFolderStackViewMode.List => CreateListView(),
            DockFolderStackViewMode.Fan => CreateFanView(),
            _ => CreateGridView(),
        };
    }

    private ScrollViewer CreateGridView()
    {
        var wrap = new WrapPanel { Width = 360, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var item in _items)
            wrap.Children.Add(CreateItemButton(item, DockFolderStackViewMode.Grid));
        return new ScrollViewer
        {
            Content = wrap,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private ScrollViewer CreateListView()
    {
        var rows = new StackPanel();
        foreach (var item in _items)
            rows.Children.Add(CreateItemButton(item, DockFolderStackViewMode.List));
        return new ScrollViewer
        {
            Content = rows,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private Canvas CreateFanView()
    {
        var canvas = new Canvas
        {
            Width = DockFolderStackLayout.CanvasWidth,
            Height = DockFolderStackLayout.CanvasHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipToBounds = false,
        };
        var positions = DockFolderStackLayout.CalculateFan(_items.Count);
        for (var index = 0; index < positions.Count; index++)
        {
            var button = CreateItemButton(_items[index], DockFolderStackViewMode.Fan);
            var position = positions[index];
            Canvas.SetLeft(button, position.Left);
            Canvas.SetTop(button, position.Top);
            Panel.SetZIndex(button, index);
            button.RenderTransformOrigin = new Point(.5, .64);
            button.RenderTransform = new RotateTransform(position.Rotation);
            canvas.Children.Add(button);
        }
        return canvas;
    }

    private Button CreateItemButton(FolderStackItem item, DockFolderStackViewMode presentation)
    {
        var isFan = presentation == DockFolderStackViewMode.Fan;
        var isList = presentation == DockFolderStackViewMode.List;
        var iconSize = isList ? 26d : 34d;
        var image = new Image
        {
            Source = ToBitmapSource(item.Icon),
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        UIElement icon = image.Source is null
            ? new TextBlock
            {
                Text = item.IsDirectory ? "▣" : "▱",
                Width = iconSize,
                Height = iconSize,
                FontSize = isList ? 20 : 27,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _secondaryForeground,
            }
            : image;

        var name = new TextBlock
        {
            Text = item.DisplayName,
            MaxWidth = isList ? 270 : isFan ? 54 : 68,
            FontSize = isFan ? 8 : 9,
            Foreground = _foreground,
            TextAlignment = isList ? TextAlignment.Left : TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        UIElement content;
        if (isList)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1, 4, 1) };
            row.Children.Add(icon);
            var kind = new TextBlock
            {
                Text = item.IsDirectory ? "文件夹" : Path.GetExtension(item.DisplayName).TrimStart('.'),
                Width = 48,
                FontSize = 9,
                Foreground = _secondaryForeground,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            row.Children.Add(name);
            row.Children.Add(kind);
            content = row;
        }
        else
        {
            var tile = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tile.Children.Add(icon);
            name.Margin = new Thickness(0, 2, 0, 0);
            tile.Children.Add(name);
            content = tile;
        }

        var button = new Button
        {
            Content = content,
            Width = isList ? 360 : isFan ? DockFolderStackLayout.ItemWidth : 72,
            Height = isList ? 34 : isFan ? DockFolderStackLayout.ItemHeight : 72,
            Margin = isList ? new Thickness(0, 0, 0, 1) : new Thickness(2),
            Padding = new Thickness(2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _foreground,
            HorizontalContentAlignment = isList ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            ToolTip = item.DisplayName,
        };
        AutomationProperties.SetName(button, item.DisplayName);
        AutomationProperties.SetHelpText(button, item.IsDirectory ? "打开文件夹" : "打开文件");
        button.Click += (_, _) => ItemInvoked?.Invoke(item.Path);
        return button;
    }

    private TextBlock StatusContent(string message) => new()
    {
        Text = message,
        Foreground = _secondaryForeground,
        Margin = new Thickness(10, 16, 10, 16),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        MaxWidth = 330,
    };

    private static BitmapSource? ToBitmapSource(GlassDesk.Platform.Shell.ExtractedIcon? icon)
    {
        if (icon is null || icon.Width <= 0 || icon.Height <= 0 || icon.Bgra.Length != icon.Width * icon.Height * 4)
            return null;

        try
        {
            var source = BitmapSource.Create(icon.Width, icon.Height, 96, 96,
                PixelFormats.Bgra32, null, icon.Bgra, icon.Width * 4);
            source.Freeze();
            return source;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
