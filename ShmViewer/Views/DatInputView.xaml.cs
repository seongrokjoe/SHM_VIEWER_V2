using ShmViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ShmViewer.Views;

public partial class DatInputView : UserControl
{
    public DatInputView() => InitializeComponent();
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
