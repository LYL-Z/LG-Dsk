using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlassDesk.Core.Models;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let an active IME own candidate navigation and commit/cancel keys.
        if (_imeComposing) return;

        if (e.Key is Key.Down or Key.Up)
        {
            var index = e.Key == Key.Down ? 0 : TileGrid.Children.Count - 1;
            if (FocusTile(index)) e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(SearchBox.Text) || TileGrid.Children.Count == 0)
            return;

        var first = TileGrid.Children[0] as LauncherAppTileButton;
        if (first is null) return;
        if (TileGrid.Children.Count == 1 && first.IsEnabled)
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, first));
        else
            first.Focus();
        e.Handled = true;
    }

    private void OnTilePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not LauncherAppTileButton tile || _logicalIconDrag) return;
        if (e.Key == Key.Enter)
        {
            tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tile));
            e.Handled = true;
            return;
        }

        var direction = e.Key switch
        {
            Key.Left => LauncherTileDirection.Left,
            Key.Right => LauncherTileDirection.Right,
            Key.Up => LauncherTileDirection.Up,
            Key.Down => LauncherTileDirection.Down,
            _ => (LauncherTileDirection?)null,
        };
        if (direction is null) return;

        var current = TileGrid.Children.IndexOf(tile);
        if (!LauncherTileNavigator.TryMove(current, TileGrid.Children.Count, _view.Columns, direction.Value, out var target))
            return;
        if (FocusTile(target)) e.Handled = true;
    }

    private void OnLauncherPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _logicalIconDrag || _imeComposing) return;
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }
        else if (_view.OpenGroupId is not null)
        {
            _view.OpenGroupId = null;
            GroupPanel.Visibility = Visibility.Collapsed;
            RenderTiles();
            SearchBox.Focus();
        }
        else
        {
            CloseWithTransition();
        }
        e.Handled = true;
    }

    private bool FocusTile(int index)
    {
        if (index < 0 || index >= TileGrid.Children.Count || TileGrid.Children[index] is not UIElement tile)
            return false;
        return tile.Focus();
    }
}
