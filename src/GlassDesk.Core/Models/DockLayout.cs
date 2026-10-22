namespace GlassDesk.Core.Models;

/// <summary>
/// Dock 排列。与应用库分开存储，这样重排
/// Dock 绝不会损坏 <see cref="AppEntry"/>。
/// </summary>
public sealed class DockLayout
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>布局文档版本；版本 2 增加了最近使用应用顺序。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Dock 中显示的应用库条目 id，按顺序排列。库中已不存在的
    /// id 属于过期项，应当跳过而不是当作错误——用户可能已经
    /// 删掉了该应用，Dock 只需相应缩短。
    /// </summary>
    public List<string> PinnedItemIds { get; set; } = [];

    /// <summary>最近成功进入前台的应用 id，按最新到最旧排列。</summary>
    public List<string> RecentApplicationIds { get; set; } = [];
}
