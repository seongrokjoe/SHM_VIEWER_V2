using ShmViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;

namespace ShmViewer.Views;

public partial class DatDiffView : UserControl
{
    private DatDiffViewModel? _viewModel;
    public DatDiffView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel != null)
            {
                _viewModel.ScrollRequested -= ScrollTo; _viewModel.ViewResetRequested -= ResetView;
                _viewModel.RowsUpdating -= CaptureGridState; _viewModel.RowsUpdated -= RestoreGridState;
            }
            _viewModel = DataContext as DatDiffViewModel;
            if (_viewModel != null)
            {
                _viewModel.ScrollRequested += ScrollTo; _viewModel.ViewResetRequested += ResetView;
                _viewModel.RowsUpdating += CaptureGridState; _viewModel.RowsUpdated += RestoreGridState;
            }
            BindViewerColumns();
        };
    }
    private void ComparisonGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    { if (e.Row.Item is DatRowViewModel row) { row.Owner.RealizeRow(row); row.Refresh(); } }
    private void ComparisonGrid_UnloadingRow(object sender, DataGridRowEventArgs e)
    { if (e.Row.Item is DatRowViewModel row) row.Owner.UnrealizeRow(row); }
    private void ScrollTo(DatRowViewModel row) => ComparisonGrid.ScrollIntoView(row);
    private DatRowViewModel? _scrollAnchor;
    private double _verticalOffset, _horizontalOffset;
    private void CaptureGridState()
    {
        var scroll = FindVisual<ScrollViewer>(ComparisonGrid);
        _verticalOffset = scroll?.VerticalOffset ?? 0;
        _horizontalOffset = scroll?.HorizontalOffset ?? 0;
        _scrollAnchor = _viewModel?.Rows.ElementAtOrDefault((int)_verticalOffset);
        // WPF otherwise scans rows to relocate selected items after Reset, defeating virtualization.
        // The view-model keeps the selection paths and restores the visible selection below.
        if (_viewModel?.IsResettingRows == true) ComparisonGrid.UnselectAll();
    }
    private void RestoreGridState()
    {
        if (_viewModel == null) return;
        var selected = _viewModel.SelectedNodes.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var row in ComparisonGrid.SelectedItems.OfType<DatRowViewModel>().ToArray())
            if (!selected.Contains(row.Node.Path) && row != _viewModel.SelectedRow) ComparisonGrid.SelectedItems.Remove(row);
        if (_viewModel.IsResettingRows && _viewModel.SelectedRow is { } primary)
            ComparisonGrid.SelectedIndex = _viewModel.Rows.IndexOf(primary);
        var existing = ComparisonGrid.SelectedItems.OfType<DatRowViewModel>().ToHashSet();
        foreach (var path in selected)
            if (_viewModel.FindVisibleRow(path) is { } row && existing.Add(row)) ComparisonGrid.SelectedItems.Add(row);
        var scroll = FindVisual<ScrollViewer>(ComparisonGrid);
        var anchor = _scrollAnchor == null ? null : _viewModel.FindVisibleRow(_scrollAnchor.Path);
        for (var parent = _scrollAnchor?.Node.Parent; anchor == null && parent != null; parent = parent.Parent)
            anchor = _viewModel.FindVisibleRow(parent.Path);
        scroll?.ScrollToVerticalOffset(anchor == null ? _verticalOffset : _viewModel.Rows.IndexOf(anchor));
        scroll?.ScrollToHorizontalOffset(_horizontalOffset);
    }
    private void ResetView()
    {
        InputScroller.ScrollToTop(); ComparisonGrid.UnselectAll(); ViewerGrid.UnselectAll();
        foreach (var input in new[] { LeftInput, RightInput })
            if (input.FindName("OptionsExpander") is Expander options) options.IsExpanded = false;
        void ResetScroll(DependencyObject parent)
        {
            if (parent is ScrollViewer scroll) { scroll.ScrollToTop(); scroll.ScrollToLeftEnd(); return; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) ResetScroll(VisualTreeHelper.GetChild(parent, i));
        }
        ResetScroll(ComparisonGrid); ResetScroll(ViewerGrid);
    }
    private async void ComparisonGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && ComparisonGrid.SelectedItem is DatRowViewModel row)
        {
            IDatValueRow cell = ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[0] ? row.LeftCell :
                ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[2] ? row.RightCell : row;
            e.Handled = await row.Owner.BeginCellEditAsync(cell); return;
        }
        e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers, e.OriginalSource as DependencyObject);
    }

    private void ComparisonGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        if (_viewModel == null || _viewModel.IsRefreshingRows) return;
        if (ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[0]) _viewModel.ActiveLeft = true;
        else if (ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[2]) _viewModel.ActiveRight = true;
    }
    private async void ViewerGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel == null) return;
        if (e.Key == Key.F2 && ViewerGrid.SelectedItem is IDatValueRow row)
            e.Handled = await row.Owner.BeginCellEditAsync(row);
        else e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers, e.OriginalSource as DependencyObject);
    }

    private int _comparisonWheelDelta;
    private int _viewerWheelDelta;
    private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var grid = (DataGrid)sender;
        var scroll = FindVisual<ScrollViewer>(grid);
        if (scroll == null) return;
        ref var remainder = ref (grid == ComparisonGrid ? ref _comparisonWheelDelta : ref _viewerWheelDelta);
        remainder += e.Delta;
        while (remainder >= 120) { scroll.LineUp(); remainder -= 120; }
        while (remainder <= -120) { scroll.LineDown(); remainder += 120; }
        e.Handled = true;
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, i)) is T child) return child;
        return null;
    }

    private static readonly string[] ColumnProperties =
    {
        nameof(DatDiffViewModel.ShowViewerPath), nameof(DatDiffViewModel.ShowViewerType),
        nameof(DatDiffViewModel.ShowViewerOffset), nameof(DatDiffViewModel.ShowViewerSize), nameof(DatDiffViewModel.ShowViewerValue)
    };
    private void BindViewerColumns()
    {
        foreach (var column in ComparisonGrid.Columns.Where((_, index) => index != 1))
            BindingOperations.SetBinding(column, DataGridColumn.MinWidthProperty,
                new Binding(nameof(DatDiffViewModel.ComparisonColumnMinWidth)) { Source = _viewModel });
        for (var i = 0; i < ViewerGrid.Columns.Count; i++)
            BindingOperations.SetBinding(ViewerGrid.Columns[i], DataGridColumn.VisibilityProperty,
                new Binding(ColumnProperties[i]) { Source = _viewModel, Converter = BoolToVisConverter.Instance });
    }
    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        for (var i = 0; i < ViewerGrid.Columns.Count; i++)
        {
            var item = new MenuItem { Header = ViewerGrid.Columns[i].Header, IsCheckable = true, StaysOpenOnClick = true };
            item.SetBinding(MenuItem.IsCheckedProperty, new Binding(ColumnProperties[i]) { Source = _viewModel, Mode = BindingMode.TwoWay });
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (_viewModel == null || !_viewModel.CanEditValues || modifiers != ModifierKeys.Control) return false;
        for (var current = source; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBoxBase or PasswordBox) return false;
        var command = key switch
        {
            Key.Up => _viewModel.PreviousDifferenceCommand,
            Key.Down => _viewModel.NextDifferenceCommand,
            Key.Right => _viewModel.UseLeftCommand,
            Key.Left => _viewModel.UseRightCommand,
            Key.Z => _viewModel.UndoCommand,
            Key.Y => _viewModel.RedoCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true) return false;
        command.Execute(null); return true;
    }
    private void ComparisonGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || _viewModel.IsRefreshingRows) return;
        _viewModel.SelectedNodes.Clear();
        _viewModel.SelectedNodes.AddRange(ComparisonGrid.SelectedItems.OfType<DatRowViewModel>().Select(r => r.Node));
        _viewModel.SelectionChanged();
    }
}
