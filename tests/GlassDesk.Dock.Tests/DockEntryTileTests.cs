using System.Windows.Interop;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// Dock 现在只显示启动台内置入口；显示桌面的底层最小化链路仍由平台断言覆盖。
/// </summary>
public class DockEntryTileTests
{
    [Fact]
    public void 无固定项时Dock只显示启动台入口()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                window.Show();

                // 未附加宿主：没有固定项，Dock 保留启动台入口。
                Assert.Equal(1, window.TileCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void 最小化与还原链路可用()
    {
        // 「显示桌面」（D-13）的实现就是逐窗口 SW_MINIMIZE；
        // 这条用例锁住 WindowControl 对真实窗口的最小化/还原动作。
        StaTest.Run(() =>
        {
            var target = new System.Windows.Window
            {
                Width = 200,
                Height = 100,
                WindowStyle = System.Windows.WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Content = new System.Windows.Controls.Border(),
            };

            try
            {
                target.Show();
                var handle = new WindowInteropHelper(target).Handle;

                Assert.False(WindowControl.IsMinimized(handle));

                _ = WindowControl.Minimize(handle);
                Assert.True(WindowControl.IsMinimized(handle), "SW_MINIMIZE 后窗口应处于最小化");

                _ = WindowControl.Restore(handle);
                Assert.False(WindowControl.IsMinimized(handle));
            }
            finally
            {
                target.Close();
            }
        });
    }
}
