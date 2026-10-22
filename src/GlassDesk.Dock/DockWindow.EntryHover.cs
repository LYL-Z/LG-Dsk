using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private DockRenderLoop? _entryHoverLoop;
    private EntryWaveGroup? _leadingEntryWave;
    private EntryWaveGroup? _trailingEntryWave;
    private readonly DockEntryMagnification _recentEntryWave = new();
    private bool _entryMagnificationPointerActive;
    private double _entryMagnificationPointerX;

    private sealed class EntryHoverState(Button button)
    {
        public Button Button { get; } = button;
        public ScaleTransform Scale { get; } = new(1, 1);
        public double CurrentCenter { get; set; }

        public void Attach()
        {
            Button.RenderTransformOrigin = new Point(.5, .5);
            Button.RenderTransform = Scale;
        }
    }

    private sealed class EntryWaveGroup(Canvas canvas)
    {
        public Canvas Canvas { get; } = canvas;
        public List<EntryHoverState> Items { get; } = [];
        public EntryHoverState[] VisibleItems { get; set; } = [];
        public DockEntryMagnification Layout { get; } = new();
    }

    private void InitializeEntryHover()
    {
        _leadingEntryWave = new EntryWaveGroup(DockLeadingEntries);
        _trailingEntryWave = new EntryWaveGroup(DockTrailingEntries);

        AddEntryHover(_leadingEntryWave, FinderButton);
        AddEntryHover(_leadingEntryWave, SettingsButton);
        AddEntryHover(_leadingEntryWave, LauncherButton);
        AddEntryHover(_leadingEntryWave, ThisPcButton);
        AddEntryHover(_trailingEntryWave, DownloadsButton);
        AddEntryHover(_trailingEntryWave, RecycleBinButton);

        _entryHoverLoop = new DockRenderLoop(AnimateEntryHoverFrame);
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ResetEntryHover(); };
        Closed += (_, _) => ResetEntryHover();
    }

    private static void AddEntryHover(EntryWaveGroup group, Button button)
    {
        var state = new EntryHoverState(button);
        state.Attach();
        group.Items.Add(state);
    }

    /// <summary>Resizes fixed entry groups once per settings/content change, never per frame.</summary>
    private void ApplyEntryMagnificationMetrics()
    {
        var settings = Settings;
        var iconSize = settings.IconSize * DockScale;
        var spacing = settings.IconSpacing * DockScale;
        var peak = settings.MagnificationPeak;
        var thickness = DockMagnification.RequiredThickness(iconSize, peak, 8 * DockScale);

        ConfigureEntryWaveGroup(_leadingEntryWave!, iconSize, spacing, peak, thickness);
        ConfigureEntryWaveGroup(_trailingEntryWave!, iconSize, spacing, peak, thickness);

        var recentHeight = thickness;
        RecentApplicationsHost.Width = DockMagnification.RequiredLength(_recentModels.Count, iconSize, spacing, peak);
        RecentApplicationsHost.Height = recentHeight;
        _recentEntryWave.Reset(_recentModels.Count, iconSize, spacing, peak);
        var resting = _recentEntryWave.Geometry;
        for (var index = 0; index < _recentModels.Count; index++)
        {
            var model = _recentModels[index];
            model.CanvasLeft = resting[index].Center - iconSize / 2;
            model.CanvasTop = (recentHeight - model.TileHeight) / 2;
            model.MagnificationScale = 1;
            model.MagnificationZIndex = index;
        }

        if (_entryMagnificationPointerActive && IsVisible && !_performanceSleepRequested)
            _entryHoverLoop?.Start();
    }

    private static void ConfigureEntryWaveGroup(EntryWaveGroup group, double iconSize, double spacing,
        double peak, double thickness)
    {
        group.VisibleItems = group.Items.Where(item => item.Button.Visibility == Visibility.Visible).ToArray();
        group.Layout.Reset(group.VisibleItems.Length, iconSize, spacing, peak);
        group.Canvas.Width = DockMagnification.RequiredLength(group.VisibleItems.Length, iconSize, spacing, peak);
        group.Canvas.Height = thickness;

        var resting = group.Layout.Geometry;
        for (var index = 0; index < group.VisibleItems.Length; index++)
        {
            var state = group.VisibleItems[index];
            var geometry = resting[index];
            state.Button.Width = iconSize;
            state.Button.Height = iconSize;
            state.Scale.ScaleX = state.Scale.ScaleY = 1;
            state.CurrentCenter = geometry.Center;
            Canvas.SetLeft(state.Button, geometry.Center - iconSize / 2);
            Canvas.SetTop(state.Button, (thickness - iconSize) / 2);
            Panel.SetZIndex(state.Button, index);
        }
    }

    private void OnDockEntriesRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_performanceSleepRequested) return;

        var rowPosition = e.GetPosition(DockEntriesRow);
        if (!double.IsFinite(rowPosition.X)) return;
        _entryMagnificationPointerX = rowPosition.X;
        _entryMagnificationPointerActive = true;
        _entryHoverLoop?.Start();

        if (_tileStrip is not null)
            _tileStrip.SetExternalMagnificationPointer(e.GetPosition(_tileStrip));
    }

    private void OnDockEntriesRowMouseLeave(object sender, MouseEventArgs e)
    {
        _entryMagnificationPointerActive = false;
        _entryHoverLoop?.Start();
        if (_tileStrip is not null) _tileStrip.ClearExternalMagnificationPointer();
    }

    private bool AnimateEntryHoverFrame(double deltaSeconds)
    {
        var animationsEnabled = ThemeService.Current?.AnimationsEnabled != false;
        var settings = Settings;
        var iconSize = settings.IconSize * DockScale;
        var spacing = settings.IconSpacing * DockScale;
        var peak = settings.MagnificationPeak;
        var allSettled = true;

        if (_leadingEntryWave is { } leading)
            allSettled &= AnimateEntryWaveGroup(leading, iconSize, spacing, peak, deltaSeconds, animationsEnabled);
        if (_trailingEntryWave is { } trailing)
            allSettled &= AnimateEntryWaveGroup(trailing, iconSize, spacing, peak, deltaSeconds, animationsEnabled);

        if (_recentModels.Count > 0)
        {
            var originX = RecentApplicationsHost.TranslatePoint(new Point(), DockEntriesRow).X;
            var pointer = _entryMagnificationPointerActive
                ? _entryMagnificationPointerX - originX
                : (double?)null;
            allSettled &= _recentEntryWave.Advance(iconSize, spacing, peak, pointer,
                deltaSeconds, animationsEnabled);

            var geometry = _recentEntryWave.Geometry;
            var order = DockMagnification.TopToBottomOrder(geometry.ToArray());
            for (var rank = 0; rank < order.Length; rank++)
            {
                var index = order[rank];
                var model = _recentModels[index];
                model.CanvasLeft = geometry[index].Center - iconSize / 2;
                model.MagnificationScale = geometry[index].Scale;
                model.MagnificationZIndex = order.Length - rank;
            }
        }

        return allSettled;
    }

    private bool AnimateEntryWaveGroup(EntryWaveGroup group, double iconSize, double spacing, double peak,
        double deltaSeconds, bool animationsEnabled)
    {
        var originX = group.Canvas.TranslatePoint(new Point(), DockEntriesRow).X;
        var pointer = _entryMagnificationPointerActive
            ? _entryMagnificationPointerX - originX
            : (double?)null;
        var settled = group.Layout.Advance(iconSize, spacing, peak, pointer, deltaSeconds, animationsEnabled);
        var geometry = group.Layout.Geometry;
        var order = DockMagnification.TopToBottomOrder(geometry.ToArray());

        for (var rank = 0; rank < order.Length; rank++)
        {
            var index = order[rank];
            var state = group.VisibleItems[index];
            var tile = geometry[index];
            state.CurrentCenter = tile.Center;
            state.Scale.ScaleX = state.Scale.ScaleY = tile.Scale;
            Canvas.SetLeft(state.Button, tile.Center - iconSize / 2);
            Panel.SetZIndex(state.Button, order.Length - rank);
        }

        return settled;
    }

    private void ResetEntryHover()
    {
        _entryMagnificationPointerActive = false;
        _entryHoverLoop?.Stop();
        if (_leadingEntryWave is { } leading) ResetEntryWaveGroup(leading);
        if (_trailingEntryWave is { } trailing) ResetEntryWaveGroup(trailing);

        var settings = Settings;
        var iconSize = settings.IconSize * DockScale;
        var spacing = settings.IconSpacing * DockScale;
        var peak = settings.MagnificationPeak;
        _recentEntryWave.Reset(_recentModels.Count, iconSize, spacing, peak);
        var recentGeometry = _recentEntryWave.Geometry;
        for (var index = 0; index < _recentModels.Count; index++)
        {
            var model = _recentModels[index];
            model.CanvasLeft = recentGeometry[index].Center - iconSize / 2;
            model.MagnificationScale = 1;
            model.MagnificationZIndex = index;
        }
        _tileStrip?.ClearExternalMagnificationPointer();
    }

    private void ResetEntryWaveGroup(EntryWaveGroup group)
    {
        var settings = Settings;
        var iconSize = settings.IconSize * DockScale;
        var spacing = settings.IconSpacing * DockScale;
        var peak = settings.MagnificationPeak;
        group.VisibleItems = group.Items.Where(item => item.Button.Visibility == Visibility.Visible).ToArray();
        group.Layout.Reset(group.VisibleItems.Length, iconSize, spacing, peak);
        var geometry = group.Layout.Geometry;

        for (var index = 0; index < group.VisibleItems.Length; index++)
        {
            var state = group.VisibleItems[index];
            state.CurrentCenter = geometry[index].Center;
            state.Scale.ScaleX = state.Scale.ScaleY = 1;
            Canvas.SetLeft(state.Button, geometry[index].Center - iconSize / 2);
            Panel.SetZIndex(state.Button, index);
        }
    }
}
