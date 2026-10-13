using ShmViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ShmViewer.Views;

public partial class DatDiffView : UserControl
{
    private DatDiffViewModel? _viewModel;
    public DatDiffView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel != null) _viewModel.ScrollRequested -= ScrollTo;
            _viewModel = DataContext as DatDiffViewModel;
            if (_viewModel != null) _viewModel.ScrollRequested += ScrollTo;
        };
    }
    private void ScrollTo(DatRowViewModel row) => ComparisonGrid.ScrollIntoView(row);
    private void ComparisonGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers, e.OriginalSource as DependencyObject);
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (_viewModel == null || _viewModel.IsBusy || modifiers != ModifierKeys.Control) return false;
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
    }
}
