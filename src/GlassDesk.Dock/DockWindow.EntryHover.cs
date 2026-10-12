using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private DockRenderLoop? _entryHoverLoop;
    private readonly ScaleTransform _launcherHoverScale = new(1, 1);
    private readonly ScaleTransform _thisPcHoverScale = new(1, 1);
    private bool _launcherHovered, _thisPcHovered;

    private void InitializeEntryHover()
    {
        LaunchpadTile.RenderTransformOrigin = ThisPcTile.RenderTransformOrigin = new Point(.5, .5);
        LaunchpadTile.RenderTransform = _launcherHoverScale;
        ThisPcTile.RenderTransform = _thisPcHoverScale;
        _entryHoverLoop = new DockRenderLoop(AnimateEntryHoverFrame);
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ResetEntryHover(); };
        Closed += (_, _) => ResetEntryHover();
    }

    private void OnEntryMouseEnter(object sender, MouseEventArgs e) => SetEntryHovered(sender, true);
    private void OnEntryMouseLeave(object sender, MouseEventArgs e) => SetEntryHovered(sender, false);

    private void SetEntryHovered(object sender, bool hovered)
    {
        if (ReferenceEquals(sender, LauncherButton)) _launcherHovered = hovered;
        else if (ReferenceEquals(sender, ThisPcButton)) _thisPcHovered = hovered;
        if (IsVisible) _entryHoverLoop?.Start();
    }

    private bool AnimateEntryHoverFrame(double deltaSeconds)
    {
        var launcherTarget = _launcherHovered ? DockSettings.FixedMagnificationPeak : 1;
        var thisPcTarget = _thisPcHovered ? DockSettings.FixedMagnificationPeak : 1;
        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            _launcherHoverScale.ScaleX = _launcherHoverScale.ScaleY = launcherTarget;
            _thisPcHoverScale.ScaleX = _thisPcHoverScale.ScaleY = thisPcTarget;
            return true;
        }
        var launcherScale = DockHoverScale.Advance(_launcherHoverScale.ScaleX, launcherTarget, deltaSeconds);
        var thisPcScale = DockHoverScale.Advance(_thisPcHoverScale.ScaleX, thisPcTarget, deltaSeconds);
        _launcherHoverScale.ScaleX = _launcherHoverScale.ScaleY = launcherScale;
        _thisPcHoverScale.ScaleX = _thisPcHoverScale.ScaleY = thisPcScale;
        return launcherScale == launcherTarget && thisPcScale == thisPcTarget;
    }

    private void ResetEntryHover()
    {
        _entryHoverLoop?.Stop();
        _launcherHovered = _thisPcHovered = false;
        _launcherHoverScale.ScaleX = _launcherHoverScale.ScaleY = 1;
        _thisPcHoverScale.ScaleX = _thisPcHoverScale.ScaleY = 1;
    }
}
