using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class InteractionSettingsTests
{
    [Theory]
    [InlineData("XButton1")]
    [InlineData("WheelUp")]
    [InlineData("WheelDown")]
    public void MouseChordUsesExistingSaveConflictAndRollbackFlow(string key)
    {
        using var fixture = new Fixture();
        var binding = new ShortcutSettings { ToggleLauncher = new() { Enabled = true, Key = key }, ToggleRunningAppSwitcher = new() };
        Assert.Null(fixture.Service.Apply(binding, new()));
        var before = File.ReadAllBytes(fixture.Data.Settings.FilePath);
        using (File.Open(fixture.Data.Settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.NotNull(fixture.Service.Apply(Bound("Q"), new()));
        Assert.Equal(key, fixture.Data.Settings.Current.Shortcuts.ToggleLauncher.Key);
        Assert.Equal(before, File.ReadAllBytes(fixture.Data.Settings.FilePath));
        Assert.Equal(InputAction.ToggleLauncher, fixture.Service.ResolveAction(fixture.Registry.Items.Single().Key));
    }
    [Fact]
    public void ConflictPreservesOldBindingAndPersistedSettings()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Service.Apply(Bound("Q"), new()));
        var before = File.ReadAllBytes(fixture.Data.Settings.FilePath);
        fixture.Registry.RejectKey = 'W';
        Assert.NotNull(fixture.Service.Apply(Bound("W"), new()));
        Assert.Equal("Q", fixture.Data.Settings.Current.Shortcuts.ToggleLauncher.Key);
        Assert.Equal(before, File.ReadAllBytes(fixture.Data.Settings.FilePath));
        Assert.Equal(InputAction.ToggleLauncher, fixture.Service.ResolveAction(fixture.Registry.Items.Single().Key));
    }

    [Fact]
    public void DefaultSettingsRegisterRunningAppSwitcherOnCtrlAltSpace()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Service.Apply(new ShortcutSettings { ToggleLauncher = new() }, new()));
        var registration = Assert.Single(fixture.Registry.Items);
        Assert.Equal(new HotkeyChord(3, 32), registration.Value);
        Assert.Equal(InputAction.ToggleRunningAppSwitcher, fixture.Service.ResolveAction(registration.Key));
    }

    [Fact]
    public void SaveFailureRestoresMemoryAndOldRegistration()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Service.Apply(Bound("Q"), new()));
        var before = File.ReadAllBytes(fixture.Data.Settings.FilePath);
        using (File.Open(fixture.Data.Settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.NotNull(fixture.Service.Apply(Bound("W"), new() { DockDisplayId = "missing" }));
        Assert.Equal("Q", fixture.Data.Settings.Current.Shortcuts.ToggleLauncher.Key);
        Assert.Null(fixture.Data.Settings.Current.Display.DockDisplayId);
        Assert.Equal(before, File.ReadAllBytes(fixture.Data.Settings.FilePath));
        Assert.Equal((uint)'Q', fixture.Registry.Items.Single().Value.VirtualKey);
    }

    [Fact]
    public void LegacyPointerFlagsStayDisabledWhileHotkeysStillRegister()
    {
        using var fixture = new Fixture();
        var legacy = Bound("Q");
        legacy.MouseSideButtonsEnabled = true;
        legacy.HotCornersEnabled = true;
        legacy.HotCornerTarget = HotCorner.TopLeft;
        Assert.Null(fixture.Service.Apply(legacy, new(), persist: false));
        Assert.False(fixture.Data.Settings.Current.Shortcuts.MouseSideButtonsEnabled);
        Assert.False(fixture.Data.Settings.Current.Shortcuts.HotCornersEnabled);
        Assert.Equal(InputAction.ToggleLauncher, fixture.Service.ResolveAction(fixture.Registry.Items.Single().Key));
    }
    [Fact]
    public void SwappingActionsReusesExistingChordAndDisposalReleasesAll()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Service.Apply(Bound("Q"), new()));
        var id = fixture.Registry.Items.Single().Key;
        var next = new ShortcutSettings { HideLauncher = Bound("Q").ToggleLauncher, ToggleRunningAppSwitcher = new() };
        Assert.Null(fixture.Service.Apply(next, new()));
        Assert.Equal(InputAction.HideLauncher, fixture.Service.ResolveAction(id));
        fixture.Service.Dispose();
        Assert.Empty(fixture.Registry.Items);
    }

    [Fact]
    public void FailedToUnregisterOldHotkeyKeepsItDisabledAndRetriesOnNextApply()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Service.Apply(Bound("Q"), new()));
        var staleId = fixture.Registry.Items.Single().Key;
        fixture.Registry.RefuseUnregisterIds.Add(staleId);

        Assert.Null(fixture.Service.Apply(Bound("W"), new()));

        Assert.Equal("W", fixture.Data.Settings.Current.Shortcuts.ToggleLauncher.Key);
        Assert.Equal(InputAction.None, fixture.Service.ResolveAction(staleId));
        Assert.NotNull(fixture.Service.LastWarning);
        var activeId = fixture.Registry.Items.Single(pair => pair.Value.VirtualKey == 'W').Key;
        Assert.Equal(InputAction.ToggleLauncher, fixture.Service.ResolveAction(activeId));

        fixture.Registry.RefuseUnregisterIds.Clear();
        Assert.Null(fixture.Service.Apply(Bound("W"), new()));

        Assert.DoesNotContain(staleId, fixture.Registry.Items.Keys);
        Assert.Null(fixture.Service.LastWarning);
    }

    [Fact]
    public void FailedSaveWithUnreleasedNewHotkeyKeepsItDisabledForCleanupRetry()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Service.Apply(Bound("Q"), new()));
        var originalBytes = File.ReadAllBytes(fixture.Data.Settings.FilePath);
        var leakedKey = (uint)'R';
        fixture.Registry.RefuseUnregisterKeys.Add(leakedKey);
        string? error;
        using (File.Open(fixture.Data.Settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var candidate = new ShortcutSettings
            {
                ToggleLauncher = Bound("R").ToggleLauncher,
                ToggleRunningAppSwitcher = new(),
            };
            error = fixture.Service.Apply(candidate, new());
        }

        Assert.Contains("保存失败", error, StringComparison.Ordinal);
        Assert.Contains("旧快捷键", error, StringComparison.Ordinal);
        Assert.Equal("Q", fixture.Data.Settings.Current.Shortcuts.ToggleLauncher.Key);
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.Data.Settings.FilePath));
        var staleId = Assert.Single(fixture.Registry.Items, pair => pair.Value.VirtualKey == leakedKey).Key;
        Assert.Equal(InputAction.None, fixture.Service.ResolveAction(staleId));

        fixture.Registry.RefuseUnregisterKeys.Clear();
        Assert.Null(fixture.Service.Apply(Bound("Q"), new()));
        Assert.DoesNotContain(staleId, fixture.Registry.Items.Keys);
    }

    private static ShortcutSettings Bound(string key) => new()
    {
        ToggleLauncher = new() { Enabled = true, Control = true, Alt = true, Key = key },
        ToggleRunningAppSwitcher = new(),
    };
    private sealed class Registry : IHotkeyRegistry
    {
        public readonly Dictionary<int, HotkeyChord> Items = [];
        public readonly HashSet<int> RefuseUnregisterIds = [];
        public readonly HashSet<uint> RefuseUnregisterKeys = [];
        public uint RejectKey;
        public bool TryRegister(int id, HotkeyChord chord, out int errorCode)
        {
            errorCode = 1409;
            if (chord.VirtualKey == RejectKey || Items.ContainsValue(chord)) return false;
            Items.Add(id, chord); errorCode = 0; return true;
        }
        public bool Unregister(int id) => !RefuseUnregisterIds.Contains(id)
            && (!Items.TryGetValue(id, out var chord) || !RefuseUnregisterKeys.Contains(chord.VirtualKey))
            && Items.Remove(id);
        public void Dispose() => Items.Clear();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "GlassDesk-P6-test-" + Guid.NewGuid().ToString("n"));
        public AppPaths Paths { get; }
        public GlassDeskDataStore Data { get; }
        public Registry Registry { get; } = new();
        public InteractionSettingsService Service { get; }
        public Fixture() { Paths = new AppPaths(root); Data = new(Paths); Data.LoadAll(); Service = new(Data.Settings, Registry); }
        public void Dispose() { Service.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
