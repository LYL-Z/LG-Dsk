namespace GlassDesk.Core.Models;

/// <summary>应用库条目的运行状态，分开表示窗口、进程和无法确认的情况。</summary>
public enum AppRuntimeState
{
    /// <summary>条目不是应用，运行状态不适用。</summary>
    NotApplicable = 0,

    /// <summary>当前没有匹配的窗口或可确认的运行进程。</summary>
    Stopped = 1,

    /// <summary>进程已运行，但当前没有可匹配、可切换的顶层窗口。</summary>
    RunningWithoutWindow = 2,

    /// <summary>至少有一个匹配的顶层窗口。</summary>
    RunningWithWindows = 3,

    /// <summary>现有身份信息不足以安全确认应用是否运行。</summary>
    Unknown = 4,
}
