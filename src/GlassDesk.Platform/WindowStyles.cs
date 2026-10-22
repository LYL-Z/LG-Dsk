using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 读取与修改窗口的扩展样式位。
/// </summary>
/// <remarks>
/// 包一层薄封装而不是把原始互操作公开出去（与 <see cref="WindowActivation"/> 同理），
/// 这样产品其余部分不必知道背后是哪个 API，也不必自己拼样式位。
/// </remarks>
public static class WindowStyles
{
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>Raise an existing topmost window without changing focus, position, or size.</summary>
    public static bool BringToTopWithoutActivation(nint window)
    {
        if (window == 0) return false;

        try
        {
            return Dpi32.SetWindowPos(
                window,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 给窗口加上 <c>WS_EX_NOACTIVATE</c>：点击它不会把它调到前台。
    /// </summary>
    /// <remarks>
    /// 程序坞的主栏靠它做到「点了不抢焦点」——用户正在别的应用里打字时
    /// 点一下 Dock，焦点必须留在原地。代价是 Dock 自己弹出的东西
    /// （右键菜单、文件夹面板、窗口选择列表）也不会随点击取得焦点，
    /// 需要键盘交互时要显式调 <see cref="WindowActivation.TryBringToForeground"/>。
    ///
    /// 返回值表示样式确实生效，供诊断与测试核对；已经有该样式时
    /// 返回 true 且不再写一遍。
    /// </remarks>
    public static bool MakeNoActivate(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            var style = User32.GetWindowLong(window, User32.GwlExStyle);
            if ((style & User32.WsExNoactivate) != 0)
            {
                return true;
            }

            // SetWindowLong 返回的是改动前的值而不是成败，这里弃元（CA1806）：
            // 写没写进去以下面的样式读回为准，返回值核对不出失败——
            // 失败时它返回 0，而新窗口的旧样式碰巧也可能是 0。
            _ = User32.SetWindowLong(window, User32.GwlExStyle, style | User32.WsExNoactivate);

            return IsNoActivate(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>临时移除 <c>WS_EX_NOACTIVATE</c>，供用户明确要求键盘操作 Dock 时调用。</summary>
    /// <remarks>
    /// 样式变化后同步通知窗口管理器。调用方应在键盘操作结束或失焦时重新调用
    /// <see cref="MakeNoActivate"/>，恢复平时点击 Dock 不抢焦点的行为。
    /// </remarks>
    public static bool MakeActivatable(nint window)
    {
        if (window == 0) return false;

        try
        {
            var style = User32.GetWindowLong(window, User32.GwlExStyle);
            if ((style & User32.WsExNoactivate) == 0) return true;

            _ = User32.SetWindowLong(window, User32.GwlExStyle, style & ~User32.WsExNoactivate);
            if (IsNoActivate(window)) return false;

            var refreshed = Dpi32.SetWindowPos(window, 0, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            if (refreshed) return true;

            _ = MakeNoActivate(window);
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _ = MakeNoActivate(window);
            return false;
        }
    }

    /// <summary>窗口是否已带 <c>WS_EX_NOACTIVATE</c> 样式。</summary>
    public static bool IsNoActivate(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            return (User32.GetWindowLong(window, User32.GwlExStyle) & User32.WsExNoactivate) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
