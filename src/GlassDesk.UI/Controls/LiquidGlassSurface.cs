using System.Windows;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;

/// <summary>Clear desktop transmission through a curved lens with independent reflection.</summary>
public sealed class LiquidGlassSurface : FrameworkElement, IDisposable
{
    private readonly GlassBackgroundLayer _background = new();
    private readonly LiquidGlassOptics _optics = new();
    private readonly AndroidLiquidGlassRenderer _androidGlass = new();
    private readonly GlassFrameWorker _worker = new();
    private readonly DispatcherTimer _timer;
    private TimeSpan _refreshInterval = GlassBackgroundLayer.LiveFrameInterval;
    private bool _renderClockAttached;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private long _lastRenderingTicks;
    private bool _renderFrameQueued;
    private DispatcherOperation? _renderFrameOperation;
    private int _renderFrameSignals;
    private readonly TransitionGlassLayer _transitionLayer = new();
    private ThemeService? _theme;
    private double _observedBackgroundClarity = .5;
    private double _transitionBlurRadius;
    private Rect? _panelShape;
    private double _panelRadius;
    private Geometry? _panelGeometry;
    private WriteableBitmap? _frame;
    private Rect _sampledPanelBounds;
    private Rect _sampledGlassPanelBounds;
    private double _sampledGlassPanelRadius;
    private Rect? _transitionCaptureBounds;
    private BitmapSource? _transitionBackdrop;
    private Rect _transitionBackdropBounds;
    private byte[]? _lastSource;
    private (int Width, int Height, Size Size, Rect GlassBounds, double Radius, bool Squircle, GlassBackdropSource Source,
        double Strength, double Softness, LiquidGlassDebugView DebugView,
        LiquidGlassSamplingParameters SamplingParameters, LiquidGlassWeightParameters WeightParameters,
        LiquidGlassLodParameters LodParameters, Color TintColor, double TintOpacity,
        double TintLuminanceInfluence, LiquidGlassBrdfParameters BrdfParameters,
        LiquidGlassDispersionParameters DispersionParameters, GlassMaterialMode Material, bool Dark, bool Transition,
        double BackgroundClarity, bool Launchpad) _frameKey;
    private bool _frameUsesAndroidGlass;
    private readonly Queue<long> _opticalFrameTimestamps = new();
    private double _opticalRefreshRate;
    private double _lastOpticsMilliseconds;
    private double _light = .35, _hover;
    private double _transitionProgress = 1;
    private bool _refreshPending;
    private GlassBackdropScene? _backdropScene;
    private GlassBackdropScene? _frameScene, _transitionBackdropScene;
    private long _materialRetentionUntil;
    internal Func<GlassBackdropScene?> ReadBackdropScene { get; set; }
    public Func<bool>? IsBackdropActive { get; set; }
    /// <summary>Large launchpad profile; Dock retains its compact material.</summary>
    public bool UseLaunchpadOptics { get; set; }
    /// <summary>Selects launcher transparency independently of the shared optical material.</summary>
    public bool UseLauncherTransparency { get; set; }
    private double BackgroundClarity => AndroidLiquidGlassRenderer.NormalizeBackgroundClarity(
        UseLauncherTransparency
            ? ThemeService.Current?.Appearance.LauncherGlassTransparency ?? AppearanceSettings.DefaultLauncherGlassTransparency
            : ThemeService.Current?.Appearance.DockGlassTransparency ?? AppearanceSettings.DefaultDockGlassTransparency);
    public GlassBackdropSource BackdropSource { get; private set; }
    /// <summary>Stable capture rectangle in owner-window DIPs when the surface is visually transformed.</summary>
    public Rect? CaptureBoundsInWindow { get; set; }

    /// <summary>Capture the full animation path; only the shared live geometry masks it.</summary>
    public void SetContainerTransitionBounds(Rect? bounds)
    {
        if (_transitionCaptureBounds == bounds) return;
        _transitionCaptureBounds = bounds;
        _worker.Invalidate();
        _lastSource = null;
        SetTimerInterval(GlassBackgroundLayer.LiveFrameInterval);
        RequestBackdropRefresh();
        UpdateTransitionLayer();
        InvalidateVisual();
    }

    /// <summary>One physical panel inside an untransformed host; capture and drawing share its bounds.</summary>
    public void SetPanelShape(Rect bounds, double radius, Geometry? geometry = null)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        _panelShape = bounds;
        _panelRadius = double.IsFinite(radius) ? Math.Max(0, radius) : 0;
        _panelGeometry = geometry;
        Clip = geometry ?? (Window.GetWindow(this) is GlassWindow owner
            ? owner.CreateGlassPanelGeometry(bounds, _panelRadius)
            : SquircleGlassGeometry.Create(bounds, _panelRadius));
        CaptureBoundsInWindow = bounds;
        UpdateTransitionLayer();
        InvalidateVisual();
    }

    /// <summary>Normalized container-transition progress shared with the owning window and shadow.</summary>
    public double TransitionProgress
    {
        get => _transitionProgress;
        set
        {
            _transitionProgress = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
            UpdateTransitionLayer();
            if (_transitionProgress < 1 && _refreshInterval > GlassBackgroundLayer.TransitionFrameInterval)
                SetTimerInterval(GlassBackgroundLayer.TransitionFrameInterval);
            InvalidateVisual();
        }
    }

    /// <summary>Additional background filtering only; specular edges remain sharp.</summary>
    public double TransitionBlurRadius
    {
        get => _transitionBlurRadius;
        set => _transitionBlurRadius = double.IsFinite(value) ? Math.Clamp(value, 0, 6) : 0;
    }

    public LiquidGlassSurface()
    {
        ReadBackdropScene = CaptureBackdropScene;
        _backdropScene = ReadBackdropScene();
        IsHitTestVisible = false;
        AddVisualChild(_transitionLayer);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnWatchdogFrame;
        Loaded += (_, _) =>
        {
            _theme = ThemeService.Current;
            if (_theme is not null)
            {
                _theme.Changed -= OnAppearanceChanged;
                _theme.Changed += OnAppearanceChanged;
                _observedBackgroundClarity = BackgroundClarity;
            }
            RequestBackdropRefresh(revealing: true); _timer.Start();
        };
        Unloaded += (_, _) => Dispose();
        IsVisibleChanged += (_, _) =>
        {
            UpdateRenderClock();
            if (!IsVisible)
            {
                // Keep material and suspend readback; the compatible session stays warm
                // while live geometry decides whether the previous mask fits.
                _worker.Invalidate(); _worker.ClearCachedBuffers(); _lastSource = null; _background.Suspend();
            }
            else RequestBackdropRefresh(revealing: true);
        };
    }

    /// <summary>Wake capture when a visible host reveals its previously inactive panel.</summary>
    public void RequestBackdropRefresh(bool revealing = false)
    {
        EnsureBackdropScene(revealing);
        SetTimerInterval(GlassBackgroundLayer.LiveFrameInterval);
        if (_refreshPending) return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _refreshPending = false;
            if (IsLoaded) OnFrame(null, EventArgs.Empty);
        }));
        InvalidateVisual();
    }

    public void Dispose()
    {
        if (_theme is not null) _theme.Changed -= OnAppearanceChanged;
        _theme = null;
        _worker.Invalidate();
        _worker.ClearCachedBuffers();
        StopRenderClock();
        _timer.Stop(); _background.Dispose(); _frame = null; _lastSource = null;
        _transitionBackdrop = null;
        _frameScene = null; _transitionBackdropScene = null;
        UpdateTransitionLayer();
        BackdropSource = GlassBackdropSource.Transparent;
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        var clarity = BackgroundClarity;
        if (clarity == _observedBackgroundClarity) return;
        _observedBackgroundClarity = clarity;
        _worker.Invalidate();
        _lastSource = null;
        SetTimerInterval(GlassBackgroundLayer.LiveFrameInterval);
        // Theme updates may originate outside a Dispatcher callback (including startup).
        // Queue the capture so its awaited worker resumes in WPF's UI context.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnFrame(null, EventArgs.Empty)));
        InvalidateVisual();
    }

    private async void OnFrame(object? sender, EventArgs e)
    {
        if (!RenderCapability.IsPixelShaderVersionSupported(3, 0)) GlassDesk.UI.BackdropRecovery.UseBasicRendering();
        EnsureBackdropScene();
        if (Window.GetWindow(this) is not GlassWindow owner || !owner.IsVisible || !IsVisible
            || ActualWidth < 1 || ActualHeight < 1 || IsBackdropActive?.Invoke() == false)
        {
            _worker.Invalidate();
            _worker.ClearCachedBuffers();
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _background.Suspend(); _lastSource = null;
            return;
        }
        if (owner.PrimaryDisplaySession is { IsAvailable: false })
        {
            _worker.Invalidate();
            _worker.ClearCachedBuffers();
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _background.Dispose(); _frame = null; _transitionBackdrop = null; _lastSource = null;
            UpdateTransitionLayer();
            return;
        }
        if (ThemeService.Current?.UseOpaquePanels == true)
        {
            _worker.Invalidate();
            _worker.ClearCachedBuffers();
            BackdropSource = GlassBackdropSource.Opaque;
            _frame = null; _lastSource = null; _background.Dispose();
            SetTimerInterval(TimeSpan.FromMilliseconds(200)); InvalidateVisual(); return;
        }
        // Capture and WPF state stay on the dispatcher. Do not acquire or enqueue another
        // frame while the immutable previous capture is being composed off the UI thread.
        if (_worker.IsBusy) return;
        var reduced = ThemeService.Current?.AnimationsEnabled == false;
        var hovering = owner.IsMouseOver && !reduced;
        var panelRect = _panelShape ?? new Rect(RenderSize);
        var target = hovering ? Math.Clamp((Mouse.GetPosition(this).X - panelRect.X) / panelRect.Width, .05, .95) : .35;
        _light += (target - _light) * (reduced ? 1 : .22);
        _hover += ((hovering ? 1 : 0) - _hover) * (reduced ? 1 : .2);
        var samplingParameters = LiquidGlassDebugState.SamplingParameters;
        var weightParameters = LiquidGlassDebugState.WeightParameters;
        var lodParameters = LiquidGlassDebugState.LodParameters;
        var debugView = LiquidGlassDebugState.View;
        var material = ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass;
        var dark = ThemeService.Current?.IsDark != false;
        var backgroundClarity = BackgroundClarity;
        var useAndroidGlass = material == GlassMaterialMode.AndroidLiquidGlass && debugView == LiquidGlassDebugView.None;
        var transitionCapture = useAndroidGlass ? _transitionCaptureBounds : null;
        var transitionBackdrop = transitionCapture is not null;
        var finishingParameters = LiquidGlassDebugState.FinishingParameters;
        var brdfParameters = LiquidGlassDebugState.BrdfParameters;
        var dispersionParameters = LiquidGlassDebugState.DispersionParameters;
        var opticsView = LiquidGlassFinishComposer.OpticsView(debugView);
        var usesMixedSampling = opticsView is LiquidGlassDebugView.None
            or LiquidGlassDebugView.FinalMixedSampling
            or LiquidGlassDebugView.LodOff
            or LiquidGlassDebugView.FaceLodOnly
            or LiquidGlassDebugView.InnerLodOnly
            or LiquidGlassDebugView.OuterLodOnly
            or LiquidGlassDebugView.FinalMixedWithLod
            or LiquidGlassDebugView.DispersionOff
            or LiquidGlassDebugView.InnerDispersionOnly
            or LiquidGlassDebugView.OuterDispersionOnly
            or LiquidGlassDebugView.FinalWithDispersion
            or LiquidGlassDebugView.DispersionTimesTenDebug;
        var usesOuterSampling = opticsView == LiquidGlassDebugView.OuterOnly || usesMixedSampling;
        var usesLodSampling = opticsView is LiquidGlassDebugView.None
            or LiquidGlassDebugView.FaceLodOnly
            or LiquidGlassDebugView.InnerLodOnly
            or LiquidGlassDebugView.OuterLodOnly
            or LiquidGlassDebugView.FinalMixedWithLod
            or LiquidGlassDebugView.DispersionOff
            or LiquidGlassDebugView.InnerDispersionOnly
            or LiquidGlassDebugView.OuterDispersionOnly
            or LiquidGlassDebugView.FinalWithDispersion
            or LiquidGlassDebugView.DispersionTimesTenDebug;
        var captureMarginDip = usesOuterSampling ? samplingParameters.OuterRimCaptureMarginDip : 0;
        if (usesLodSampling) captureMarginDip += lodParameters.MaximumActiveFootprintDip;
        // Three-box Gaussian support plus maximum inward lens displacement.
        if (useAndroidGlass) captureMarginDip = 48;
        var captureBounds = transitionCapture ?? CaptureBoundsInWindow;
        var topLeft = captureBounds is { } fixedBounds
            ? owner.PointToScreen(new Point(fixedBounds.X, fixedBounds.Y))
            : PointToScreen(new Point());
        var bottomRight = captureBounds is { } fixedBounds2
            ? owner.PointToScreen(new Point(fixedBounds2.Right, fixedBounds2.Bottom))
            : PointToScreen(new Point(ActualWidth, ActualHeight));
        var primaryDisplay = owner.PrimaryDisplaySession is { IsAvailable: true } primarySession
            ? primarySession.Display
            : DisplayService.Primary();
        if (primaryDisplay is null)
        {
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _background.Dispose(); _frame = null; _lastSource = null;
            return;
        }
        var dpiScale = primaryDisplay.ScaleFactor;
        var regionLeft = (int)Math.Floor(topLeft.X - captureMarginDip * dpiScale);
        var regionTop = (int)Math.Floor(topLeft.Y - captureMarginDip * dpiScale);
        var regionRight = (int)Math.Ceiling(bottomRight.X + captureMarginDip * dpiScale);
        var regionBottom = (int)Math.Ceiling(bottomRight.Y + captureMarginDip * dpiScale);
        var requestedRegion = new PixelRect(regionLeft, regionTop,
            Math.Max(1, regionRight - regionLeft), Math.Max(1, regionBottom - regionTop));
        var region = owner.PrimaryDisplaySession is { IsAvailable: true } session
            ? session.ClipToPrimaryBounds(requestedRegion)
            : ClipRegion(requestedRegion, primaryDisplay.Bounds);
        if (region.Width <= 0 || region.Height <= 0)
        {
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _background.Dispose(); _frame = null; _lastSource = null;
            return;
        }
        var launchpadOptics = UseLaunchpadOptics;
        var pixelBudget = launchpadOptics
            ? 320_000 + 680_000 * Math.Clamp((backgroundClarity - .5) * 2, 0, 1)
            : !transitionBackdrop && _transitionProgress < 1 ? 100_000 : 320_000;
        var scale = Math.Min(1, Math.Min(2048.0 / Math.Max(requestedRegion.Width, requestedRegion.Height),
            Math.Sqrt(pixelBudget / ((double)requestedRegion.Width * requestedRegion.Height))));
        var width = Math.Max(1, (int)Math.Floor(requestedRegion.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(requestedRegion.Height * scale));
        var radius = _panelShape is null ? owner.PanelCornerRadius.TopLeft : _panelRadius;
        var squircle = owner.UsesSquirclePanelGeometry;
        var captureGeometry = GlassCaptureGeometry.Create(requestedRegion, region, topLeft,
            panelRect, dpiScale, captureMarginDip > 0);
        if (transitionBackdrop)
            captureGeometry = GlassCaptureGeometry.Create(requestedRegion, region,
                owner.PointToScreen(panelRect.Location), panelRect, dpiScale, true);
        var sampleSizeDip = captureGeometry.SampleSizeDip;
        var glassBoundsDip = captureGeometry.GlassBoundsDip;
        var sampledFrameBounds = captureGeometry.FrameBoundsDip;
        // Background is captured first. The glass stage below only reads this fixed frame;
        // WPF content is a separate visual above LiquidGlassSurface in each window.
        var background = _background.Capture(region,
            Math.Max(1, (int)Math.Floor(region.Width * scale)),
            Math.Max(1, (int)Math.Floor(region.Height * scale)), radius,
            owner.SurfaceMode == GlassSurfaceMode.OpticalGlass);
        var capturedScene = _backdropScene;
        if (background is not null)
            background = GlassBackgroundLayer.ExtendCapture(background, region, requestedRegion, width, height);
        var source = background?.Pixels;
        var kind = background?.Source ?? GlassBackdropSource.Transparent;
        BackdropSource = kind;
        if (kind == GlassBackdropSource.Basic)
        {
            // Capture, CPU optics and shader are all optional for this readable body.
            _worker.Invalidate(); _frame = null; _transitionBackdrop = null; _lastSource = null;
            UpdateTransitionLayer(); InvalidateVisual(); return;
        }
        var strength = .6 + .4 * _transitionProgress;
        // The unmasked transition prefilter depends on capture size/material, not the
        // moving silhouette. Its GPU lens receives the current shape every render.
        var key = (width, height, sampleSizeDip, transitionBackdrop ? Rect.Empty : glassBoundsDip,
            transitionBackdrop ? 0 : radius, squircle, kind,
            strength, _transitionBlurRadius, debugView, samplingParameters, weightParameters, lodParameters,
            finishingParameters.TintColor, finishingParameters.TintOpacity,
            finishingParameters.TintLuminanceInfluence, brdfParameters, dispersionParameters, material, dark, transitionBackdrop, backgroundClarity, launchpadOptics);
        // A cold native session may have no first frame yet. Keep the last completed
        // texture, masked by current geometry, until a fresh source replaces it.
        if (source is null) { _lastSource = null; }
        else if ((transitionBackdrop ? _transitionBackdrop is null : _frame is null)
            || _frameKey != key || _lastSource is null
            || !ReferenceEquals(source, _lastSource) && !source.AsSpan().SequenceEqual(_lastSource))
        {
            // Capture and optics follow the current panel, independently of content scale.
            // Cached geometry is rebuilt only when its physical sampling parameters change.
            var applyWallpaperShoulder = !useAndroidGlass && kind == GlassBackdropSource.Wallpaper
                && (debugView == LiquidGlassDebugView.None || LiquidGlassFinishComposer.IsComparisonView(debugView));
            var wallpaperWeights = applyWallpaperShoulder
                ? CaptureWallpaperShoulderWeights(owner, width, height, sampleSizeDip, glassBoundsDip, radius)
                : null;
            using var result = transitionBackdrop
                ? await _worker.RenderBufferedAsync(source.Length, (pixels, scratch) =>
                    AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source, pixels, scratch, width, height, sampleSizeDip, dark, strength,
                        backgroundClarity: backgroundClarity, launchpadOptics: launchpadOptics))
                : await _worker.RenderAsync(() => LiquidGlassFrameComposer.Compose(_optics, source, width, height,
                sampleSizeDip, radius, squircle, strength, _transitionBlurRadius, debugView,
                samplingParameters, weightParameters, glassBoundsDip, lodParameters,
                finishingParameters, brdfParameters, dispersionParameters,
                wallpaperWeights is not null
                    ? pixels => ApplyWallpaperShoulder(pixels, wallpaperWeights)
                : null, material, dark, _androidGlass, backgroundClarity: backgroundClarity, launchpadOptics: launchpadOptics));
            EnsureBackdropScene();
            if (result is null || !_worker.IsCurrent(result) || !owner.IsVisible || !IsVisible || IsBackdropActive?.Invoke() == false
                || owner.PrimaryDisplaySession is { IsAvailable: false }
                || ThemeService.Current?.UseOpaquePanels == true
                || backgroundClarity != BackgroundClarity || capturedScene is null || !capturedScene.Equals(_backdropScene)) return;
            var refracted = result.Pixels;
            if (transitionBackdrop)
            {
                if (!capturedScene.Equals(_frameScene)) { _frame = null; _frameScene = null; }
                if (!TryUpdateBackdropBitmap(_transitionBackdrop, refracted, width, height, out var submitted)) return;
                _transitionBackdrop = submitted;
                _transitionBackdropScene = capturedScene;
                if (_transitionBackdropBounds != sampledFrameBounds) InvalidateArrange();
                _transitionBackdropBounds = sampledFrameBounds;
            }
            else
            {
                if (!capturedScene.Equals(_transitionBackdropScene)) { _transitionBackdrop = null; _transitionBackdropScene = null; }
                if (!TryUpdateBackdropBitmap(_frame, refracted, width, height, out var submitted)) return;
                _frame = submitted;
                _frameScene = capturedScene;
                _sampledGlassPanelBounds = panelRect;
                _sampledGlassPanelRadius = radius;
                _sampledPanelBounds = sampledFrameBounds;
            }
            _lastOpticsMilliseconds = result.Milliseconds;
            _materialRetentionUntil = 0;
            _frameUsesAndroidGlass = useAndroidGlass;
            _opticalFrameTimestamps.Enqueue(Stopwatch.GetTimestamp());
            _lastSource = source; _frameKey = key;
        }
        if (!transitionBackdrop && _frame is not null)
        {
            _sampledPanelBounds = sampledFrameBounds;
            _sampledGlassPanelBounds = panelRect;
            _sampledGlassPanelRadius = radius;
        }
        UpdateTransitionLayer();
        RefreshOpticalRefreshRate();
        SetTimerInterval(_transitionProgress < 1
            ? GlassBackgroundLayer.TransitionFrameInterval
            : source is null || kind is GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene || hovering
                ? GlassBackgroundLayer.LiveFrameInterval : TimeSpan.FromMilliseconds(160));
        InvalidateVisual();
    }

    // WPF owns the copied pixels. Keep the same native texture while its dimensions
    // match; WritePixels also notifies the existing drawing/shader of the new frame.
    internal static bool TryUpdateBackdropBitmap(BitmapSource? current, byte[] pixels, int width, int height, out WriteableBitmap bitmap)
    {
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Invalid bitmap submission extent.");
        bitmap = current is WriteableBitmap retained && !retained.IsFrozen
            && retained.PixelWidth == width && retained.PixelHeight == height
            ? retained : new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        if (!bitmap.TryLock(new Duration(TimeSpan.Zero))) return false;
        try
        {
            var rowBytes = checked(width * 4);
            if (bitmap.BackBufferStride == rowBytes)
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.BackBuffer, pixels.Length);
            else
                for (var row = 0; row < height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, row * rowBytes,
                        bitmap.BackBuffer + row * bitmap.BackBufferStride, rowBytes);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height)); return true;
        }
        finally { bitmap.Unlock(); }
    }

    private static double[] CaptureWallpaperShoulderWeights(GlassWindow owner, int width, int height,
        Size sampleSizeDip, Rect glassBoundsDip, double radius)
    {
        // Snapshot owner-dependent geometry on its dispatcher; the worker only reads values.
        var weights = new double[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            owner.TryGetGlassPanelEdge(glassBoundsDip, radius,
                new Point((x + .5) * sampleSizeDip.Width / width,
                    (y + .5) * sampleSizeDip.Height / height), out var depth, out _);
            weights[y * width + x] = Math.Clamp(1 - depth / 12, 0, 1);
        }
        return weights;
    }

    private static void ApplyWallpaperShoulder(byte[] pixels, double[] weights)
    {
        // Same alpha expression and placement before Tint / BRDF / Dispersion.
        for (var pixel = 0; pixel < weights.Length; pixel++)
        {
            var weight = weights[pixel];
            var index = pixel * 4 + 3;
            pixels[index] = (byte)(pixels[index] * .32 * weight * weight);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        EnsureBackdropScene();
        UpdateTransitionLayer();
        var dc = drawingContext;
        base.OnRender(dc);
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var rect = _panelShape ?? new Rect(0, 0, ActualWidth, ActualHeight);
        var glass = Window.GetWindow(this) as GlassWindow;
        var radius = _panelShape is null ? glass?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius;
        var transition = Math.Clamp(_transitionProgress, 0, 1);
        var highlightScale = .48 + .52 * transition;
        var debugView = LiquidGlassDebugState.View;
        Geometry CreatePanel(Rect bounds, double cornerRadius)
        {
            if (_panelShape is not null && bounds == rect && cornerRadius == radius)
                return _panelGeometry ??= glass is null
                    ? SquircleGlassGeometry.Create(bounds, cornerRadius)
                    : glass.CreateGlassPanelGeometry(bounds, cornerRadius);
            return glass is null ? SquircleGlassGeometry.Create(bounds, cornerRadius)
                : glass.CreateGlassPanelGeometry(bounds, cornerRadius);
        }
        var dark = ThemeService.Current?.IsDark != false;
        if (debugView != LiquidGlassDebugView.None
            && (!LiquidGlassFinishComposer.IsComparisonView(debugView)
                || LiquidGlassFinishComposer.IsBrdfComponentView(debugView)))
        {
            if (_frame is not null) dc.DrawImage(_frame, _sampledPanelBounds);
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1);
            dc.DrawGeometry(null, outline, CreatePanel(rect, radius));
            if (LiquidGlassDebugState.ShowPerformanceOverlay) DrawMetricsOverlay(dc, rect);
            return;
        }

        dc.PushClip(CreatePanel(rect, radius));
        if (ThemeService.Current?.UseOpaquePanels == true || _background.RequiresBasicBackground)
        {
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(28, 29, 34) : Color.FromRgb(246, 247, 250)), null, rect);
            if (_background.RequiresBasicBackground) DrawCompatibilityNotice(dc, rect, "基础背景 · 正在恢复实时效果", dark);
            dc.Pop(); return;
        }
        var compatibleChrome = debugView == LiquidGlassDebugView.None && (_frameUsesAndroidGlass
            || _frame is null && (ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass)
                == GlassMaterialMode.AndroidLiquidGlass);
        var opticalFrameMatches = _frame is not null && _sampledGlassPanelBounds == rect
            && _sampledGlassPanelRadius == radius;
        var useTransitionMaterial = _transitionCaptureBounds is not null || !opticalFrameMatches;
        var hasTransitionBackdrop = compatibleChrome && _transitionBackdrop is not null
            && _transitionBackdropBounds.Contains(rect);
        if (compatibleChrome && useTransitionMaterial && !hasTransitionBackdrop)
        {
            // During first capture/expansion, paint a complete low-opacity body. A stale
            // tiny frame must never leave just an outline on the expanding launcher.
            var tint = dark ? (byte)18 : (byte)250;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(255 * AndroidLiquidGlassRenderer.BodyOpacity * AndroidLiquidGlassRenderer.BodyTintScale(
                    BackgroundClarity, UseLaunchpadOptics)), tint, tint, tint)), null, rect);
        }
        if (opticalFrameMatches && (!compatibleChrome || _transitionCaptureBounds is null))
        {
            dc.DrawImage(_frame, _sampledPanelBounds);
        }
        if (compatibleChrome)
        {
            if (!opticalFrameMatches && !hasTransitionBackdrop)
                DrawWaitingRim(dc, CreatePanel(rect, radius), highlightScale);
            if (LiquidGlassDebugState.ShowPerformanceOverlay) DrawMetricsOverlay(dc, rect);
            if (BackdropSource == GlassBackdropSource.CompatibleScene)
                DrawCompatibilityNotice(dc, rect, "兼容场景玻璃 · 壁纸为静态", dark);
            else if (BackdropSource == GlassBackdropSource.Wallpaper)
                DrawCompatibilityNotice(dc, rect, "壁纸玻璃 · 不反映后方窗口", dark);
            dc.Pop();
            return;
        }
        var body = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(ScaleAlpha(5, highlightScale),255,255,255),0), new(Colors.Transparent,.35),
            new(Color.FromArgb(ScaleAlpha(dark ? (byte)6 : (byte)3, highlightScale),12,18,30),1)
        }, new Point(0,0), new Point(0,1));
        dc.DrawRectangle(body, null, rect);
        var reflection = new RadialGradientBrush
        {
            Center = new Point(_light, -.12), GradientOrigin = new Point(_light, -.12), RadiusX = .38, RadiusY = .58,
            GradientStops = new GradientStopCollection { new(Color.FromArgb(ScaleAlpha(7 + _hover * 4, highlightScale),255,255,255),0), new(Colors.Transparent,1) }
        };
        dc.DrawRectangle(reflection, null, rect);
        var rim = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(ScaleAlpha(70, highlightScale),255,255,255),0), new(Color.FromArgb(ScaleAlpha(16, highlightScale),255,255,255),.24),
            new(Color.FromArgb(ScaleAlpha(24, highlightScale),18,28,44),.58), new(Color.FromArgb(ScaleAlpha(42, highlightScale),255,255,255),1)
        }, new Point(_light * .5,0), new Point(1 - _light * .5,1));
        var rimBounds = rect;
        rimBounds.Inflate(-.55,-.55);
        if (rimBounds.Width > 0 && rimBounds.Height > 0)
        {
            dc.DrawGeometry(null, new Pen(rim,.8 * highlightScale), CreatePanel(rimBounds, Math.Max(0,radius-.55)));
            // Unequal light lobes on the same curved rim: bright upper shoulder,
            // a narrow left glint and a weaker lower-right reflected light.
            DrawGlint(new Point(.12 + _light * .35, .015), .40, .23, 198, 1.15);
            DrawGlint(new Point(.015, .27), .10, .33, 104, 1.0);
            DrawGlint(new Point(.96, .80), .17, .30, 118, .85);
        }
        var innerBounds = rect;
        innerBounds.Inflate(-2.05,-2.05);
        if (innerBounds.Width > 0 && innerBounds.Height > 0)
        {
            dc.DrawGeometry(null,new Pen(reflection,2.4 * highlightScale),CreatePanel(innerBounds,Math.Max(0,radius-2.05)));
            var inner = new LinearGradientBrush(Color.FromArgb(ScaleAlpha(16, highlightScale),20,32,48),Color.FromArgb(ScaleAlpha(18, highlightScale),255,255,255),90);
            dc.DrawGeometry(null,new Pen(inner,.75),CreatePanel(innerBounds,Math.Max(0,radius-2.05)));
        }
        if (LiquidGlassDebugState.ShowPerformanceOverlay) DrawMetricsOverlay(dc, rect);
        dc.Pop();

        void DrawGlint(Point center, double rx, double ry, byte alpha, double thickness)
        {
            var brush = new RadialGradientBrush
            {
                Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(ScaleAlpha(alpha, highlightScale),255,255,255),0),
                    new(Color.FromArgb(ScaleAlpha(alpha * .4, highlightScale),235,245,255),.45),
                    new(Colors.Transparent,1)
                }
            };
            dc.DrawGeometry(null, new Pen(brush, thickness), CreatePanel(rimBounds, Math.Max(0,radius-.55)));
        }
    }

    private void DrawCompatibilityNotice(DrawingContext dc, Rect rect, string text, bool dark)
    {
        if (rect.Height < 120 || rect.Width < 300) return;
        var label = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 11,
            dark ? Brushes.WhiteSmoke : Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(Math.Max(rect.Left + 12, rect.Right - label.Width - 16), rect.Bottom - label.Height - 12));
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _transitionLayer
        : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        _transitionLayer.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _transitionLayer.Arrange(_transitionBackdropBounds.Width > 0
            ? _transitionBackdropBounds : new Rect(finalSize));
        return finalSize;
    }

    private void UpdateTransitionLayer()
    {
        var panel = _panelShape ?? new Rect(RenderSize);
        var radius = _panelShape is null ? (Window.GetWindow(this) as GlassWindow)?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius;
        var opticalMatches = _frame is not null && _sampledGlassPanelBounds == panel && _sampledGlassPanelRadius == radius;
        var enabled = (_transitionCaptureBounds is not null || !opticalMatches)
            && _transitionBackdrop is not null && _transitionBackdropBounds.Contains(panel)
            && LiquidGlassDebugState.View == LiquidGlassDebugView.None
            && ThemeService.Current?.UseOpaquePanels != true
            && (ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass) == GlassMaterialMode.AndroidLiquidGlass;
        _transitionLayer.SetBackdrop(enabled ? _transitionBackdrop : null);
        _transitionLayer.Opacity = enabled ? 1 : 0;
        if (enabled) _transitionLayer.SetPanel(panel, _transitionBackdropBounds, radius, .6 + .4 * _transitionProgress, UseLaunchpadOptics);
    }

    private void EnsureBackdropScene(bool revealing = false)
    {
        var scene = ReadBackdropScene();
        if (_frame is not null) _frameScene ??= _backdropScene;
        if (_transitionBackdrop is not null) _transitionBackdropScene ??= _backdropScene;
        if (scene is null || !scene.Equals(_backdropScene))
        {
            _backdropScene = scene;
            _worker.Invalidate();
            _background.Suspend();
            _lastSource = null;
        }
        // Fresh acquisition still follows every scene boundary. While visible, an
        // added overlay or z-order change can retain material from windows that
        // remain alive at the same bounds. Transient transparent tool overlays
        // may disappear during input. Both cases get at most 100 ms to replace
        // material; ordinary window removal/movement and recovery clear at once.
        var discarded = false;
        bool Reusable(GlassBackdropScene? materialScene)
        {
            if (scene is null) return false;
            if (scene.Equals(materialScene)) return true;
            if (revealing || !scene.CanRetainMaterialFrom(materialScene)) return false;
            var now = Stopwatch.GetTimestamp();
            if (_materialRetentionUntil == 0) _materialRetentionUntil = now + Stopwatch.Frequency / 10;
            return now < _materialRetentionUntil;
        }
        if (_frame is not null && !Reusable(_frameScene))
        {
            _frame = null; _frameScene = null; discarded = true;
        }
        if (_transitionBackdrop is not null && !Reusable(_transitionBackdropScene))
        {
            _transitionBackdrop = null; _transitionBackdropScene = null; discarded = true;
        }
        if (!discarded) return;
        _worker.Invalidate(); _lastSource = null;
        if (_frame is null && _transitionBackdrop is null) BackdropSource = GlassBackdropSource.Transparent;
        UpdateTransitionLayer(); InvalidateVisual();
    }

    private GlassBackdropScene? CaptureBackdropScene()
    {
        if (Window.GetWindow(this) is not GlassWindow owner || PresentationSource.FromVisual(owner) is null
            || owner.ActualWidth < 1 || owner.ActualHeight < 1) return GlassBackdropScene.Capture();
        // Include the owner's entire animation path and capture guard band, then
        // use the same primary-monitor boundary as desktop acquisition.
        var first = owner.PointToScreen(new Point(-64, -64));
        var last = owner.PointToScreen(new Point(owner.ActualWidth + 64, owner.ActualHeight + 64));
        var region = new PixelRect((int)Math.Floor(first.X), (int)Math.Floor(first.Y),
            (int)Math.Ceiling(last.X - first.X), (int)Math.Ceiling(last.Y - first.Y));
        if (owner.PrimaryDisplaySession is { IsAvailable: true } session) region = session.ClipToPrimaryBounds(region);
        return GlassBackdropScene.Capture(region);
    }

    private static void DrawWaitingRim(DrawingContext dc, Geometry panel, double strength)
    {
        // Keep the material's directional edge visible while a changed scene supplies
        // its first texture. This stroke contains no pixels from the previous window.
        var rim = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(ScaleAlpha(100, strength), 255, 255, 255), 0),
            new(Color.FromArgb(ScaleAlpha(20, strength), 255, 255, 255), .4),
            new(Color.FromArgb(ScaleAlpha(75, strength), 12, 17, 27), 1),
        }, new Point(0, 0), new Point(1, 1));
        dc.DrawGeometry(null, new Pen(rim, 1.5 * strength), panel);
    }

    private void SetTimerInterval(TimeSpan interval)
    {
        _refreshInterval = interval;
        // A 16 ms DispatcherTimer can wake only every second display frame because
        // its Windows timer is rounded. Live updates follow WPF's render clock;
        // the timer only wakes idle modes or recovers a temporarily silent clock.
        var watchdog = interval <= GlassBackgroundLayer.LiveFrameInterval ? TimeSpan.FromMilliseconds(100) : interval;
        if (_timer.Interval != watchdog) _timer.Interval = watchdog;
        UpdateRenderClock();
    }

    private void UpdateRenderClock()
    {
        var active = IsLoaded && IsVisible && _refreshInterval <= GlassBackgroundLayer.LiveFrameInterval;
        if (active && !_renderClockAttached)
        {
            _lastRenderingTime = TimeSpan.MinValue; _lastRenderingTicks = 0;
            CompositionTarget.Rendering += OnRenderingFrame; _renderClockAttached = true;
        }
        else if (!active) StopRenderClock();
    }

    private void StopRenderClock()
    {
        if (!_renderClockAttached) return;
        CompositionTarget.Rendering -= OnRenderingFrame; _renderClockAttached = false;
        _renderFrameOperation?.Abort(); _renderFrameOperation = null; _renderFrameQueued = false;
        _lastRenderingTicks = 0;
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        _lastRenderingTicks = Stopwatch.GetTimestamp();
        // Rendering only signals one update. CPU optics run off the dispatcher;
        // acquisition and its completion must not wait behind sustained input.
        if (_renderFrameQueued)
        {
            // Give the periodic background yield one display interval. Sustained
            // mouse input must not pin it there and block all subsequent frames.
            if (_renderFrameOperation is { Status: DispatcherOperationStatus.Pending, Priority: DispatcherPriority.Background })
                _renderFrameOperation.Priority = DispatcherPriority.Normal;
            return;
        }
        if (_worker.IsBusy) return;
        _renderFrameQueued = true;
        // Periodically join the background queue as well: a continuous stream at
        // Normal priority can otherwise starve timers/icon notifications.
        var priority = ++_renderFrameSignals % 8 == 0 ? DispatcherPriority.Background : DispatcherPriority.Normal;
        _renderFrameOperation = Dispatcher.BeginInvoke(priority, new Action(() =>
        {
            _renderFrameOperation = null;
            _renderFrameQueued = false;
            if (_renderClockAttached && IsLoaded && IsVisible) OnFrame(null, EventArgs.Empty);
        }));
    }

    private void OnWatchdogFrame(object? sender, EventArgs e)
    {
        if (_renderClockAttached && _lastRenderingTicks != 0
            && Stopwatch.GetElapsedTime(_lastRenderingTicks) < TimeSpan.FromMilliseconds(100)) return;
        OnFrame(sender, e);
    }

    private static PixelRect ClipRegion(PixelRect rect, PixelRect bounds)
    {
        var left = Math.Max(rect.X, bounds.X);
        var top = Math.Max(rect.Y, bounds.Y);
        var right = Math.Min(rect.Right, bounds.Right);
        var bottom = Math.Min(rect.Bottom, bounds.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(Math.Clamp(left, bounds.X, bounds.Right), Math.Clamp(top, bounds.Y, bounds.Bottom), 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    private static byte ScaleAlpha(double alpha, double scale) =>
        (byte)Math.Clamp((int)Math.Round(alpha * scale), 0, 255);

    private void RefreshOpticalRefreshRate()
    {
        var cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        while (_opticalFrameTimestamps.TryPeek(out var timestamp) && timestamp < cutoff)
            _opticalFrameTimestamps.Dequeue();
        _opticalRefreshRate = _opticalFrameTimestamps.Count;
    }

    private void DrawMetricsOverlay(DrawingContext drawingContext, Rect bounds)
    {
        var text = new FormattedText(
            $"折射刷新 {_opticalRefreshRate:0} fps · CPU {_lastOpticsMilliseconds:0.00} ms",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var label = new Rect(bounds.Left + 6, bounds.Bottom - text.Height - 8, text.Width + 12, text.Height + 4);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 12, 18, 28)),
            null, label, 3, 3);
        drawingContext.DrawText(text, new Point(label.Left + 6, label.Top + 2));
    }
}
