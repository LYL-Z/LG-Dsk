using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

public enum GlassBackdropSource { Transparent, Wallpaper, LiveDesktop, Opaque, CompatibleScene, Basic }

/// <summary>A fixed BGRA snapshot for one glass frame, independent of reused capture buffers.</summary>
internal sealed class GlassBackgroundFrame
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public GlassBackdropSource Source { get; }

    public GlassBackgroundFrame(byte[] pixels, int width, int height, GlassBackdropSource source)
        : this(pixels, width, height, source, false) { }

    private GlassBackgroundFrame(byte[] pixels, int width, int height, GlassBackdropSource source, bool ownsPixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Invalid BGRA background frame.", nameof(pixels));
        Pixels = ownsPixels ? pixels : (byte[])pixels.Clone();
        Width = width;
        Height = height;
        Source = source;
    }

    // Only newly allocated buffers may be transferred; capture buffers still require a snapshot.
    internal static GlassBackgroundFrame FromOwnedPixels(byte[] pixels, int width, int height, GlassBackdropSource source)
        => new(pixels, width, height, source, true);
}

/// <summary>Background stage: acquire self-excluded desktop pixels or a wallpaper fallback.</summary>
internal sealed class GlassBackgroundLayer : IDisposable
{
    public static TimeSpan LiveFrameInterval => TimeSpan.FromMilliseconds(16);
    public static TimeSpan TransitionFrameInterval => LiveFrameInterval;
    private readonly SharedBackdropFeed _compatible = new();
    private readonly SharedBackdropFeed _wallpaperFeed = new((region, _, trace) =>
        new IsolatedDesktopSceneCapture(region, GlassBackdropSource.Wallpaper, trace));
    private GlassBackgroundFrame? _lastLive;
    private BackdropFrame? _lastCapture;
    private (PixelRect Region, int Width, int Height) _lastLiveGeometry;
    private long _pendingSince;
    private GlassBackgroundFrame? _basic;
    internal bool RequiresBasicBackground { get; private set; }

    public GlassBackgroundFrame? Capture(PixelRect region, int width, int height, double radius, bool allowDesktop)
    {
        if (GlassDesk.UI.BackdropRecovery.RequiresBasicRendering)
        {
            RequiresBasicBackground = true;
            _compatible.Suspend(); _wallpaperFeed.Suspend(); return Basic(width, height);
        }
        if (allowDesktop) _wallpaperFeed.Suspend();
        if (allowDesktop && _compatible.TryRead(region, width, height, out var captured)
            && captured is not null)
        {
            _pendingSince = 0; RequiresBasicBackground = false;
            if (_lastLive is not null && _lastLiveGeometry == (region, width, height)
                && _lastLive.Source == captured.Source
                && (ReferenceEquals(_lastCapture, captured) || captured.Pixels.Span.SequenceEqual(_lastLive.Pixels)))
            {
                _lastCapture = captured;
                return _lastLive;
            }
            _lastCapture = captured;
            _lastLiveGeometry = (region, width, height);
            return _lastLive = GlassBackgroundFrame.FromOwnedPixels(captured.Pixels.ToArray(), width, height,
                captured.Source);
        }

        // Tooltips/menus deliberately stop screen capture. Replay only the previous safe
        // snapshot at the identical coordinates while exclusion remains verified.
        // This never samples the popup and never stretches a stale frame after a move.
        // The feed is the authority for scene/generation validity. A failed read does
        // not authorize replay of a snapshot after an exclusion or resume boundary.

        // Pending or failed live capture is not a wallpaper-mode switch.
        // Dynamic wallpapers can report a solid black Windows wallpaper underneath
        // the visible desktop. Publishing it here makes both glass surfaces flash
        // opaque black before the first live frame. Null keeps completed material,
        // or the surface's transparent waiting body, until capture is ready.
        // The feed retries transient failures; retain completed material while it
        // recovers instead of replacing live wallpaper with its hidden black bitmap.
        if (allowDesktop)
        {
            if (_pendingSince == 0) _pendingSince = System.Diagnostics.Stopwatch.GetTimestamp();
            RequiresBasicBackground = _compatible.Failure is not null
                || System.Diagnostics.Stopwatch.GetElapsedTime(_pendingSince) >= TimeSpan.FromSeconds(3);
            if (!RequiresBasicBackground) return null;
            return Basic(width, height);
        }

        _compatible.Suspend();
        if (_wallpaperFeed.TryRead(region, width, height, out var wallpaper) && wallpaper is not null)
        {
            RequiresBasicBackground = false;
            return GlassBackgroundFrame.FromOwnedPixels(wallpaper.Pixels.ToArray(), width, height, GlassBackdropSource.Wallpaper);
        }
        RequiresBasicBackground = true;
        return Basic(width, height);
    }

    private GlassBackgroundFrame Basic(int width, int height)
    {
        if (_basic is null || _basic.Width != width || _basic.Height != height)
        {
            var pixels = new byte[checked(width * height * 4)];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 40; pixels[i + 1] = 34; pixels[i + 2] = 30; pixels[i + 3] = 255; }
            _basic = GlassBackgroundFrame.FromOwnedPixels(pixels, width, height, GlassBackdropSource.Basic);
        }
        return _basic;
    }

    public void Suspend()
    {
        _compatible.Suspend();
        _wallpaperFeed.Suspend();
        _lastLive = null;
        _lastCapture = null;
        _lastLiveGeometry = default;
        _pendingSince = 0; RequiresBasicBackground = false;
    }

    public void Dispose()
    {
        _compatible.Dispose();
        _wallpaperFeed.Dispose();
        _lastLive = null;
        _lastCapture = null;
        _lastLiveGeometry = default;
        _pendingSince = 0; RequiresBasicBackground = false;
    }

    /// <summary>Extend only captured Primary pixels into the logical offscreen margin.</summary>
    internal static GlassBackgroundFrame ExtendCapture(GlassBackgroundFrame frame,
        PixelRect capturedRegion, PixelRect requestedRegion, int width, int height)
    {
        if (capturedRegion == requestedRegion && frame.Width == width && frame.Height == height)
            return frame;
        var pixels = new byte[checked(width * height * 4)];
        var columns = ArrayPool<int>.Shared.Rent(width);
        try
        {
            for (var x = 0; x < width; x++)
            {
                var screenX = requestedRegion.X + (x + .5) * requestedRegion.Width / width;
                columns[x] = Math.Clamp((int)Math.Floor((screenX - capturedRegion.X)
                    * frame.Width / capturedRegion.Width), 0, frame.Width - 1);
            }
            var source = MemoryMarshal.Cast<byte, uint>(frame.Pixels.AsSpan());
            var target = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
            var previousSourceY = -1;
            for (var y = 0; y < height; y++)
            {
                var screenY = requestedRegion.Y + (y + .5) * requestedRegion.Height / height;
                var sourceY = Math.Clamp((int)Math.Floor((screenY - capturedRegion.Y)
                    * frame.Height / capturedRegion.Height), 0, frame.Height - 1);
                var row = target.Slice(y * width, width);
                if (sourceY == previousSourceY)
                    target.Slice((y - 1) * width, width).CopyTo(row);
                else
                    for (var x = 0; x < width; x++) row[x] = source[sourceY * frame.Width + columns[x]];
                previousSourceY = sourceY;
            }
            return GlassBackgroundFrame.FromOwnedPixels(pixels, width, height, frame.Source);
        }
        finally { ArrayPool<int>.Shared.Return(columns); }
    }
}
