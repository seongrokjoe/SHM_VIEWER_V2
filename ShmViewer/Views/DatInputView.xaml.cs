using ShmViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShmViewer.Views;

public partial class DatInputView : UserControl
{
    public DatInputView() => InitializeComponent();
    private void Headers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DatInputViewModel input)
            input.SelectHeaders(HeaderList.SelectedItems.OfType<HeaderFileEntry>().ToArray());
    }
    private void Headers_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is DatInputViewModel input)
        { input.RemoveHeaderCommand.Execute(null); e.Handled = true; }
    }
    private void Input_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsEnabled && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Input_Drop(object sender, DragEventArgs e)
    {
        if (IsEnabled && DataContext is DatInputViewModel input && e.Data.GetData(DataFormats.FileDrop) is string[] files) input.AddFiles(files);
        e.Handled = true;
    }
}
