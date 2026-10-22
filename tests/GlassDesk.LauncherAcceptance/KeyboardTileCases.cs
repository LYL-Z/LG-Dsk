using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Core.Models;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyKeyboardTiles(LauncherWindow window, UniformGrid grid, TextBox search, string groupName)
    {
        var groupPanel = (PhoneBorder)window.FindName("GroupPanel")!;
        search.Focus();
        RaiseKey(search, window, Key.Down, Keyboard.PreviewKeyDownEvent);
        var first = (Button)grid.Children[0];
        Check("搜索框向下键进入第一个应用", "L-KEYBOARD", Keyboard.FocusedElement == first,
            "第一个磁贴获得焦点", Keyboard.FocusedElement?.GetType().Name ?? "无焦点");

        Check("应用磁贴可通过键盘聚焦", "L-KEYBOARD", first.Focusable && first.IsTabStop && first.Focus(),
            "Focusable 和 TabStop 均启用", $"Focusable {first.Focusable}；TabStop {first.IsTabStop}");
        window.UpdateLayout();
        Pump(TimeSpan.FromMilliseconds(50));
        first.ApplyTemplate();
        var chrome = (PhoneBorder)first.Template.FindName("TileChrome", first);
        var accentBrush = (window as GlassWindow)?.AccentBrush;
        Check("键盘焦点具有可见边框", "L-KEYBOARD", chrome.BorderThickness.Left > 0 && chrome.BorderBrush is not null && accentBrush is not null,
            "磁贴显示主题焦点边框", $"边框 {chrome.BorderThickness}；画笔 {chrome.BorderBrush}；窗口强调色 {accentBrush}");

        if (LauncherTileNavigator.TryMove(0, grid.Children.Count, grid.Columns,
                LauncherTileDirection.Right, out var nextIndex))
        {
            RaiseKey(first, window, Key.Right, Keyboard.PreviewKeyDownEvent);
            Check("方向键按网格移动焦点", "L-KEYBOARD", Keyboard.FocusedElement == grid.Children[nextIndex],
                $"第 {nextIndex + 1} 个磁贴获得焦点", Keyboard.FocusedElement?.GetType().Name ?? "无焦点");
        }

        var groupTile = grid.Children.Cast<Button>()
            .Single(button => string.Equals(button.Tag as string, groupName, StringComparison.Ordinal));
        groupTile.Focus();
        RaiseKey(groupTile, window, Key.Enter, Keyboard.PreviewKeyDownEvent);
        Check("Enter 打开分组磁贴", "L-KEYBOARD", groupPanel.Visibility == Visibility.Visible,
            "分组面板打开", groupPanel.Visibility.ToString());
        RaiseKey(window, window, Key.Escape, Keyboard.PreviewKeyDownEvent);
        Check("Esc 返回启动台上一级", "L-KEYBOARD", groupPanel.Visibility == Visibility.Collapsed && window.IsVisible,
            "分组收起且启动台保留", $"面板 {groupPanel.Visibility}；窗口 {window.IsVisible}");

        var tile = (Button)grid.Children[0];
        tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tile));
        window.RefreshLibrary();
        tile = (Button)grid.Children[0];
        tile.ApplyTemplate();
        chrome = (PhoneBorder)tile.Template.FindName("TileChrome", tile);
        Check("鼠标点击后磁贴模板保持有效", "L-KEYBOARD", chrome is not null && chrome.CornerRadius.TopLeft > 0,
            "磁贴圆角模板仍有效", chrome?.CornerRadius.ToString() ?? "模板未加载");

        var iconHost = ((StackPanel)tile.Content).Children[0] as Border;
        var pressedKey = (DependencyPropertyKey)typeof(ButtonBase)
            .GetField("IsPressedPropertyKey", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        tile.SetValue(pressedKey, true);
        Pump(TimeSpan.FromMilliseconds(100));
        Check("按下只轻微缩小当前图标", "L-KEYBOARD", iconHost?.RenderTransform is ScaleTransform { ScaleX: < 1 and >= .95, ScaleY: < 1 and >= .95 },
            "当前图标轻微缩小", iconHost?.RenderTransform.ToString() ?? "无图标容器");
        tile.SetValue(pressedKey, false);
        Pump(TimeSpan.FromMilliseconds(300));
        Check("松开后图标恢复且没有残留动画", "L-KEYBOARD", iconHost?.RenderTransform is ScaleTransform { ScaleX: 1, ScaleY: 1, HasAnimatedProperties: false },
            "原始大小且动画时钟结束", iconHost?.RenderTransform is ScaleTransform motion
                ? $"X {motion.ScaleX}；Y {motion.ScaleY}；Animated {motion.HasAnimatedProperties}"
                : "无图标容器");
    }

    private static void RaiseKey(UIElement target, LauncherWindow window, Key key, RoutedEvent routedEvent) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
        { RoutedEvent = routedEvent });

    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisual<T>(child)) yield return nested;
        }
    }
}
