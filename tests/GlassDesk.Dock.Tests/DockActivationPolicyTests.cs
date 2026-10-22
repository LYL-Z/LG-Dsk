using GlassDesk.Core.Models;
using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

public class DockActivationPolicyTests
{
    [Theory]
    [InlineData(AppRuntimeState.RunningWithoutWindow)]
    [InlineData(AppRuntimeState.Unknown)]
    public void NoWindowOrUnknownStateRequiresAnExplicitLaunchChoice(AppRuntimeState state)
    {
        var entry = new AppEntry { Id = "app", Target = @"C:\Apps\Example.exe", Kind = AppItemKind.Program };

        Assert.True(DockActivationPolicy.RequiresExplicitLaunchChoice(entry, state));
    }

    [Theory]
    [InlineData(AppRuntimeState.Stopped)]
    [InlineData(AppRuntimeState.RunningWithWindows)]
    [InlineData(AppRuntimeState.NotApplicable)]
    public void KnownStoppedOrVisibleWindowStateDoesNotRequireConfirmation(AppRuntimeState state)
    {
        var entry = new AppEntry { Id = "app", Target = @"C:\Apps\Example.exe", Kind = AppItemKind.Program };

        Assert.False(DockActivationPolicy.RequiresExplicitLaunchChoice(entry, state));
    }

    [Theory]
    [InlineData(AppItemKind.Folder)]
    [InlineData(AppItemKind.File)]
    [InlineData(AppItemKind.Url)]
    public void NonApplicationEntriesDoNotUseApplicationLaunchPrompt(AppItemKind kind)
    {
        var entry = new AppEntry { Id = "entry", Target = @"C:\Somewhere", Kind = kind };

        Assert.False(DockActivationPolicy.RequiresExplicitLaunchChoice(entry, AppRuntimeState.Unknown));
    }

    [Fact]
    public void MissingOrUnusableTargetsDoNotUseBackgroundPrompt()
    {
        var missing = new AppEntry
        {
            Id = "missing",
            Target = @"C:\Apps\Missing.exe",
            Kind = AppItemKind.Program,
            IsTargetMissing = true,
        };
        var unusable = new AppEntry { Target = @"C:\Apps\Example.exe", Kind = AppItemKind.Program };

        Assert.False(DockActivationPolicy.RequiresExplicitLaunchChoice(missing, AppRuntimeState.Unknown));
        Assert.False(DockActivationPolicy.RequiresExplicitLaunchChoice(unusable, AppRuntimeState.Unknown));
        Assert.False(DockActivationPolicy.RequiresExplicitLaunchChoice(null, AppRuntimeState.Unknown));
    }
}
