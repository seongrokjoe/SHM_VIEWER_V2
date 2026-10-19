using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShmViewer.ViewModels;

namespace ShmViewer.Views;

public partial class DatDiffView
{
    private long _viewerAnchor;
    private double _viewerVertical, _viewerHorizontal;
    private void ViewerGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    { if (e.Row.Item is DatViewerRowViewModel row) { row.Owner.ViewerRows.Realized.Add(row); row.RefreshValue(); } }
    private void ViewerGrid_UnloadingRow(object sender, DataGridRowEventArgs e)
    { if (e.Row.Item is DatViewerRowViewModel row) row.Owner.ViewerRows.Realized.Remove(row); }
    private void CaptureViewerState()
    {
        var scroll = FindVisual<ScrollViewer>(ViewerGrid);
        _viewerVertical = scroll?.VerticalOffset ?? 0; _viewerHorizontal = scroll?.HorizontalOffset ?? 0;
        _viewerAnchor = _viewModel?.ViewerRows.ElementAtOrDefault((int)_viewerVertical)?.Id ?? 0;
        ViewerGrid.UnselectAll();
    }
    private void RestoreViewerState()
    {
        if (_viewModel == null) return;
        if (_viewModel.SelectedViewerRow is { } selected) ViewerGrid.SelectedIndex = _viewModel.ViewerRows.IndexOf(selected);
        var anchor = _viewerAnchor;
        while (anchor > 0 && !_viewModel.ViewerRows.IsVisible(anchor))
            anchor = _viewModel.ViewerSession?.Entries.GetValueOrDefault(anchor)?.ParentId ?? 0;
        var scroll = FindVisual<ScrollViewer>(ViewerGrid);
        scroll?.ScrollToVerticalOffset(_viewModel.ViewerRows.IsVisible(anchor) ? _viewModel.ViewerRows.IndexOf(_viewModel.ViewerRows.Get(anchor)) : _viewerVertical);
        scroll?.ScrollToHorizontalOffset(_viewerHorizontal);
    }
    private void ScrollViewerTo(DatViewerRowViewModel row) => ViewerGrid.ScrollIntoView(row);
    private void ViewerRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel?.CanInteract == true && sender is DataGridRow row) row.IsSelected = true;
    }
    private async void SingleName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: DatViewerRowViewModel row }) return;
        e.Handled = true; await row.Owner.RenameSingleCommand.ExecuteAsync(row);
    }
    private async void SingleType_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: DatViewerRowViewModel row }) return;
        e.Handled = true; await row.Owner.ChangeSingleTypeCommand.ExecuteAsync(row);
    }
    private static void FocusName(TextBox editor)
    {
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (editor.IsVisible && editor.IsEnabled && editor.DataContext is DatViewerRowViewModel { IsNameEditing: true })
            { editor.Focus(); editor.SelectAll(); }
        }));
    }
    private void SingleNameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is true) FocusName((TextBox)sender); }
    private void SingleNameEditor_Loaded(object sender, RoutedEventArgs e) => FocusName((TextBox)sender);
    private async void SingleNameEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel == null || e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Enter) await _viewModel.ApplySingleInputCommand.ExecuteAsync(null);
        else await _viewModel.CancelSingleInputCommand.ExecuteAsync(null);
    }
    private static bool IsTextInput(DependencyObject? source)
    {
        for (var current = source; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBoxBase or PasswordBox) return true;
        return false;
    }
}
