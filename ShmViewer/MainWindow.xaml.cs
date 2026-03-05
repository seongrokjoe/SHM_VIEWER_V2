using ShmViewer.ViewModels;
using ShmViewer.Views.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShmViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var headers = files.Where(f =>
            f.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase));
        _vm.AddHeaderFiles(headers);
    }

    private void TreeView_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView tree) return;
        if (tree.SelectedItem is not TreeNodeViewModel node) return;
        if (node.MemberInfo == null || node.MemberInfo.ResolvedType != null) return;

        var selectedTab = _vm.SelectedTab;
        if (selectedTab == null) return;

        // Get raw byte data from the current SHM snapshot
        // We pass the cached data stored in tab — for simplicity read again
        try
        {
            var reader = new Core.Shm.ShmReader();
            if (selectedTab.IsLoaded && !string.IsNullOrEmpty(selectedTab.ShmName))
            {
                var data = reader.ReadSnapshot(selectedTab.ShmName,
                    node.Offset + node.Size + 64); // read enough

                var popup = new DetailPopup(node, data);
                popup.Left = SystemParameters.PrimaryScreenWidth / 2 - 210;
                popup.Top = SystemParameters.PrimaryScreenHeight / 2 - 200;
                popup.Show();
            }
        }
        catch { /* If SHM read fails, just don't show popup */ }
    }
}
