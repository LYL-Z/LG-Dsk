using System.Windows;

namespace GlassDesk.Dock.Tests;

public sealed class DockFileDropPolicyTests
{
    private const DragDropEffects CopyMove = DragDropEffects.Copy | DragDropEffects.Move;

    [Fact]
    public void ApplicationTileAlwaysKeepsTheSourceAndRequiresCopyPermission()
    {
        var app = new DockTileModel { EntryId = "app", AcceptsFileDrop = true };

        Assert.Equal(
            DragDropEffects.Copy,
            DockFileDropPolicy.Resolve(app, [@"C:\work\image.png"], DragDropKeyStates.LeftMouseButton, CopyMove));
        Assert.Equal(
            DragDropEffects.None,
            DockFileDropPolicy.Resolve(app, [@"C:\work\image.png"], DragDropKeyStates.LeftMouseButton, DragDropEffects.Move));
    }

    [Fact]
    public void FolderUsesMoveOnSameVolumeAndCopyAcrossVolumesByDefault()
    {
        var folder = new DockTileModel { EntryId = "folder", FolderDropPath = @"C:\target" };

        Assert.Equal(
            DragDropEffects.Move,
            DockFileDropPolicy.Resolve(folder, [@"C:\source\image.png"], DragDropKeyStates.LeftMouseButton, CopyMove));
        Assert.Equal(
            DragDropEffects.Copy,
            DockFileDropPolicy.Resolve(folder, [@"D:\source\image.png"], DragDropKeyStates.LeftMouseButton, CopyMove));
    }

    [Fact]
    public void FolderHonorsControlCopyShiftMoveAndRejectsLinkGestures()
    {
        var folder = new DockTileModel { EntryId = "folder", FolderDropPath = @"C:\target" };
        var source = new[] { @"C:\source\image.png" };

        Assert.Equal(
            DragDropEffects.Copy,
            DockFileDropPolicy.Resolve(folder, source, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ControlKey, CopyMove));
        Assert.Equal(
            DragDropEffects.Move,
            DockFileDropPolicy.Resolve(folder, source, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey, CopyMove));
        Assert.Equal(
            DragDropEffects.None,
            DockFileDropPolicy.Resolve(folder, source, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.AltKey, CopyMove));
        Assert.Equal(
            DragDropEffects.None,
            DockFileDropPolicy.Resolve(folder, source,
                DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey,
                CopyMove));
    }

    [Fact]
    public void FolderRejectsUnsupportedAndMalformedDropTargets()
    {
        var ordinaryApp = new DockTileModel { EntryId = "app" };
        var folder = new DockTileModel { EntryId = "folder", FolderDropPath = @"C:\target" };

        Assert.Equal(
            DragDropEffects.None,
            DockFileDropPolicy.Resolve(ordinaryApp, [@"C:\source\image.png"], DragDropKeyStates.LeftMouseButton, CopyMove));
        Assert.Equal(
            DragDropEffects.None,
            DockFileDropPolicy.Resolve(folder, ["relative.png"], DragDropKeyStates.LeftMouseButton, CopyMove));
        Assert.Equal(
            DragDropEffects.None,
            DockFileDropPolicy.Resolve(folder, [], DragDropKeyStates.LeftMouseButton, CopyMove));
    }
}
