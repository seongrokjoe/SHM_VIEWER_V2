using ShmViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;

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
    private void ComparisonGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;
        _viewModel.SelectedNodes.Clear();
        _viewModel.SelectedNodes.AddRange(ComparisonGrid.SelectedItems.OfType<DatRowViewModel>().Select(r => r.Node));
    }
}
