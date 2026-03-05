using ShmViewer.ViewModels;
using ShmViewer.Views.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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

        try
        {
            var reader = new Core.Shm.ShmReader();
            if (selectedTab.IsLoaded && !string.IsNullOrEmpty(selectedTab.ShmName))
            {
                var data = reader.ReadSnapshot(selectedTab.ShmName,
                    node.Offset + node.Size + 64);

                var popup = new DetailPopup(node, data);
                popup.Left = SystemParameters.PrimaryScreenWidth / 2 - 210;
                popup.Top = SystemParameters.PrimaryScreenHeight / 2 - 200;
                popup.Show();
            }
        }
        catch { /* If SHM read fails, just don't show popup */ }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.SearchCommand.Execute(null);
    }

    private void SearchResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not SearchResultViewModel result) return;

        // 이전 하이라이트 초기화
        foreach (var r in _vm.SearchResults)
            r.Node.IsHighlighted = false;

        // 탭 전환
        _vm.SelectedTab = result.Tab;

        // 조상 노드 펼치기
        foreach (var ancestor in result.AncestorPath)
            ancestor.IsExpanded = true;

        // 대상 노드 하이라이트
        result.Node.IsHighlighted = true;

        // UI 업데이트 후 BringIntoView
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            BringNodeIntoView(result.Node);
        });
    }

    private void BringNodeIntoView(TreeNodeViewModel node)
    {
        var treeView = FindVisualChild<TreeView>(this);
        if (treeView == null) return;

        var item = FindTreeViewItem(treeView, node);
        item?.BringIntoView();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, object item)
    {
        var container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
        if (container != null) return container;

        for (int i = 0; i < parent.Items.Count; i++)
        {
            var child = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
            if (child == null) continue;
            var result = FindTreeViewItem(child, item);
            if (result != null) return result;
        }
        return null;
    }
}
