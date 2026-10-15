using ShmViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ShmViewer.Views;

public partial class DatValueCell : UserControl
{
    public static readonly DependencyProperty ValueTextAlignmentProperty = DependencyProperty.Register(nameof(ValueTextAlignment),
        typeof(TextAlignment), typeof(DatValueCell), new PropertyMetadata(TextAlignment.Left));
    public TextAlignment ValueTextAlignment { get => (TextAlignment)GetValue(ValueTextAlignmentProperty); set => SetValue(ValueTextAlignmentProperty, value); }
    public DatValueCell() => InitializeComponent();

    private async void Value_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && DataContext is IDatValueRow row && await row.Owner.BeginCellEditAsync(row))
        { FocusEditor(); e.Handled = true; }
    }

    private async void Value_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not IDatValueRow row) return;
        if (e.Key == Key.F2 && await row.Owner.BeginCellEditAsync(row)) { FocusEditor(); e.Handled = true; }
        else if (Equals(row.Owner.CellEditTarget, row.EditTarget))
        {
            if (e.Key == Key.Enter) { e.Handled = true; await row.Owner.CommitCellEditAsync(); }
            else if (e.Key == Key.Escape) { row.Owner.CancelCellEditCommand.Execute(null); e.Handled = true; }
        }
    }

    private void Editor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ValueEditor.IsVisible) FocusEditor();
    }

    private void FocusEditor() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    {
        if (ValueEditor.IsVisible) { ValueEditor.Focus(); ValueEditor.SelectAll(); }
    }));

    private async void Editor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Recycled rows can lose focus while being detached; their draft belongs to the view-model.
        if (e.NewFocus != null && IsLoaded && IsVisible && DataContext is IDatValueRow row &&
            !row.Owner.IsRefreshingRows && Equals(row.Owner.CellEditTarget, row.EditTarget))
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (Equals(row.Owner.CellEditTarget, row.EditTarget) && row.Owner.CanEditValues) await row.Owner.CommitCellEditAsync();
        }
    }
}
