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
            if (_viewModel != null) { _viewModel.ScrollRequested -= ScrollTo; _viewModel.ViewResetRequested -= ResetView; }
            _viewModel = DataContext as DatDiffViewModel;
            if (_viewModel != null) { _viewModel.ScrollRequested += ScrollTo; _viewModel.ViewResetRequested += ResetView; }
            BindViewerColumns();
        };
    }
    private void ScrollTo(DatRowViewModel row) => ComparisonGrid.ScrollIntoView(row);
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
    private void ComparisonGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && ComparisonGrid.SelectedItem is DatRowViewModel row)
        {
            IDatValueRow cell = ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[0] ? row.LeftCell :
                ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[2] ? row.RightCell : row;
            e.Handled = row.Owner.BeginCellEdit(cell); return;
        }
        e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers, e.OriginalSource as DependencyObject);
    }

    private void ComparisonGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        if (_viewModel == null || _viewModel.IsRefreshingRows) return;
        if (ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[0]) _viewModel.ActiveLeft = true;
        else if (ComparisonGrid.CurrentCell.Column == ComparisonGrid.Columns[2]) _viewModel.ActiveRight = true;
    }
    private void ViewerGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel == null) return;
        if (e.Key == Key.F2 && ViewerGrid.SelectedItem is IDatValueRow row)
            e.Handled = row.Owner.BeginCellEdit(row);
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
        if (_viewModel == null) return;
        _viewModel.SelectedNodes.Clear();
        _viewModel.SelectedNodes.AddRange(ComparisonGrid.SelectedItems.OfType<DatRowViewModel>().Select(r => r.Node));
        _viewModel.SelectionChanged();
    }
}
