using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace GlassDesk.Dock;

public sealed record NotificationAreaAppItem(string DisplayName, string ToolTipText);

/// <summary>
/// Best-effort read-only labels for notification-area buttons exposed by Windows UI Automation.
/// Windows does not publish a supported API that enumerates arbitrary third-party tray icons.
/// </summary>
public static class NotificationAreaAppItems
{
    private const int MaximumAccessibleNodes = 512;
    private const int MaximumResults = 48;

    public static IReadOnlyList<NotificationAreaAppItem> EnumerateAccessibleItems()
    {
        var results = new Dictionary<string, NotificationAreaAppItem>(StringComparer.CurrentCultureIgnoreCase);
        var walker = TreeWalker.ControlViewWalker;
        try
        {
            var root = AutomationElement.RootElement;
            foreach (var className in new[] { "Shell_TrayWnd", "NotifyIconOverflowWindow" })
            {
                var windows = root.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, className));
                foreach (AutomationElement window in windows)
                {
                    var pending = new Stack<AutomationElement>();
                    for (var child = walker.GetFirstChild(window); child is not null; child = walker.GetNextSibling(child))
                        pending.Push(child);

                    var inspected = 0;
                    while (pending.Count > 0 && inspected++ < MaximumAccessibleNodes && results.Count < MaximumResults)
                    {
                        var element = pending.Pop();
                        if (IsNotificationIcon(element, className))
                        {
                            var name = element.Current.Name?.Trim();
                            if (!string.IsNullOrWhiteSpace(name) && !IsShellControlName(name))
                                results.TryAdd(name, new NotificationAreaAppItem(name, $"托盘图标 · {name} · 由 Windows 管理"));
                        }

                        for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                            pending.Push(child);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            // UI Automation availability varies by Windows build and shell configuration.
        }

        return results.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsNotificationIcon(AutomationElement element, string windowClass)
    {
        try
        {
            if (windowClass == "NotifyIconOverflowWindow")
                return element.Current.ControlType == ControlType.Button || element.Current.ControlType == ControlType.ListItem;

            if (element.Current.ControlType != ControlType.Button && element.Current.ControlType != ControlType.ListItem)
                return false;
            var parent = TreeWalker.ControlViewWalker.GetParent(element);
            for (var depth = 0; parent is not null && depth < 8; depth++)
            {
                var current = parent.Current;
                if (current.ClassName?.Contains("TrayNotify", StringComparison.OrdinalIgnoreCase) == true
                    || current.ClassName?.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase) == true
                    || current.AutomationId?.Contains("SystemTray", StringComparison.OrdinalIgnoreCase) == true
                    || current.AutomationId?.Contains("NotificationArea", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                parent = TreeWalker.ControlViewWalker.GetParent(parent);
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
        return false;
    }

    private static bool IsShellControlName(string name) => name.Equals("Show hidden icons", StringComparison.OrdinalIgnoreCase)
        || name.Equals("显示隐藏的图标", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Network", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Volume", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Battery", StringComparison.OrdinalIgnoreCase);
}
