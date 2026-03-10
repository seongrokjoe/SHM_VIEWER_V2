using ShmViewer.ViewModels;
using ShmViewer.Views.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShmViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private Point _gridSplitterStart;
    private double _col0WidthStart;
    private double _col1WidthStart;
    private double _col2WidthStart;
    private GridSplitter? _currentSplitter;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        Loaded += (_, _) =>
        {
            AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeViewItem_Expanded));
            SetupSearchResultsGrouping();
        };

        PreviewMouseUp += (_, _) =>
        {
            if (_currentSplitter == null)
                return;

            _currentSplitter.ReleaseMouseCapture();
            _currentSplitter = null;
        };
    }

    private void SetupSearchResultsGrouping()
    {
        var cvs = new CollectionViewSource { Source = _vm.SearchResults };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription("TabName"));
        SearchResultGrid.ItemsSource = cvs.View;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SearchResults))
                cvs.Source = _vm.SearchResults;
        };
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
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var headers = files.Where(f =>
            f.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase));

        _vm.AddHeaderFiles(headers);
    }

    private void TreeView_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView tree)
            return;

        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = VisualTreeHelper.GetParent(source);

        if (source is not TreeViewItem item || item.DataContext is not TreeNodeViewModel node)
            return;

        item.IsSelected = true;

        if (node.MemberInfo == null || node.MemberInfo.ResolvedType != null)
            return;

        var contextMenu = new ContextMenu();
        var menuItem = new MenuItem
        {
            Header = "바이너리 확인"
        };
        menuItem.Click += (_, _) => ShowDetailPopup(node);
        contextMenu.Items.Add(menuItem);
        contextMenu.PlacementTarget = tree;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void ShowDetailPopup(TreeNodeViewModel node)
    {
        var selectedTab = _vm.SelectedTab;
        if (selectedTab == null)
            return;

        try
        {
            byte[]? data = null;

            if (selectedTab.IsRunning && !string.IsNullOrEmpty(selectedTab.ShmName))
            {
                var reader = new Core.Shm.ShmReader();
                data = reader.ReadSnapshot(selectedTab.ShmName, node.Offset + node.Size + 64);
            }

            data ??= new byte[node.Offset + node.Size + 64];

            var popup = new DetailPopup(node, data)
            {
                Left = SystemParameters.PrimaryScreenWidth / 2 - 210,
                Top = SystemParameters.PrimaryScreenHeight / 2 - 200
            };
            popup.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"바이너리 확인 실패: {ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void TreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = VisualTreeHelper.GetParent(source);

        if (source is TreeViewItem item)
            item.IsSelected = true;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.SearchCommand.Execute(null);
    }

    private async void SearchResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not SearchResultViewModel result)
            return;

        foreach (var entry in _vm.SearchResults)
        {
            if (entry.Node != null)
                entry.Node.IsHighlighted = false;
        }

        _vm.SelectedTab = result.Tab;

        await Dispatcher.Yield(DispatcherPriority.Loaded);

        var (node, ancestorPath) = await FindNodeByPathAsync(result.Tab, result.NodePath);
        if (node == null)
            return;

        result.Node = node;
        result.AncestorPath = ancestorPath;

        foreach (var ancestor in ancestorPath)
            ancestor.IsExpanded = true;

        node.IsHighlighted = true;

        await Dispatcher.Yield(DispatcherPriority.Loaded);
        BringNodeIntoView(node, ancestorPath);
    }

    private static async Task<(TreeNodeViewModel? node, List<TreeNodeViewModel> ancestorPath)> FindNodeByPathAsync(
        ShmTabViewModel tab,
        string nodePath)
    {
        var ancestorPath = new List<TreeNodeViewModel>();
        if (tab.RootNodes.Count == 0)
            return (null, ancestorPath);

        var current = tab.RootNodes[0];
        if (string.IsNullOrEmpty(nodePath))
            return (current, ancestorPath);

        foreach (var part in nodePath.Split('.'))
        {
            if (current.IsLazy || current.IsExpanding)
                await tab.ExpandNodeAsync(current);

            var name = part.Contains('[') ? part[..part.IndexOf('[')] : part;
            if (string.IsNullOrEmpty(name))
                continue;

            var child = current.Children.FirstOrDefault(c => c.Name == name);
            if (child == null)
                break;

            ancestorPath.Add(current);
            current = child;
        }

        return (current, ancestorPath);
    }

    private void BringNodeIntoView(TreeNodeViewModel node, List<TreeNodeViewModel>? ancestorPath = null)
    {
        var tabControl = FindVisualChild<TabControl>(this);
        if (tabControl?.SelectedItem is not ShmTabViewModel selectedTab)
            return;

        var tabItem = tabControl.ItemContainerGenerator.ContainerFromItem(selectedTab) as TabItem;
        if (tabItem == null)
            return;

        var treeView = FindVisualChild<TreeView>(tabItem);
        if (treeView == null)
            return;

        if (ancestorPath is { Count: > 0 })
        {
            var item = FindTreeViewItemWithVirtualization(treeView, node, ancestorPath);
            if (item != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    item.BringIntoView();
                });
            }
            return;
        }

        var directItem = FindTreeViewItem(treeView, node);
        if (directItem != null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                directItem.BringIntoView();
            });
        }
    }

    private TreeViewItem? FindTreeViewItemWithVirtualization(
        ItemsControl parent,
        TreeNodeViewModel target,
        List<TreeNodeViewModel> ancestorPath)
    {
        foreach (var ancestor in ancestorPath)
        {
            parent.UpdateLayout();
            var container = parent.ItemContainerGenerator.ContainerFromItem(ancestor) as TreeViewItem;
            if (container == null)
            {
                var vsp = FindVisualChild<VirtualizingStackPanel>(parent);
                if (vsp != null)
                {
                    var index = parent.Items.IndexOf(ancestor);
                    if (index >= 0)
                        vsp.BringIndexIntoViewPublic(index);
                }

                parent.UpdateLayout();
                container = parent.ItemContainerGenerator.ContainerFromItem(ancestor) as TreeViewItem;
            }

            if (container == null)
                return null;

            container.IsExpanded = true;
            container.UpdateLayout();
            parent = container;
        }

        parent.UpdateLayout();
        var targetContainer = parent.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
        if (targetContainer == null)
        {
            var vsp = FindVisualChild<VirtualizingStackPanel>(parent);
            if (vsp != null)
            {
                var index = parent.Items.IndexOf(target);
                if (index >= 0)
                    vsp.BringIndexIntoViewPublic(index);
            }

            parent.UpdateLayout();
            targetContainer = parent.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
        }

        return targetContainer;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;

            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, object item)
    {
        var container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
        if (container != null)
            return container;

        for (int i = 0; i < parent.Items.Count; i++)
        {
            var child = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
            if (child == null)
                continue;

            var result = FindTreeViewItem(child, item);
            if (result != null)
                return result;
        }

        return null;
    }

    private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item)
            return;

        if (item.DataContext is not TreeNodeViewModel node || _vm.SelectedTab == null)
            return;

        if (node.IsLazy || node.IsExpanding)
            await _vm.SelectedTab.ExpandNodeAsync(node);
    }

    private void GridSplitter_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not GridSplitter splitter)
            return;

        _gridSplitterStart = e.GetPosition(this);
        _col0WidthStart = _vm.Col0Width;
        _col1WidthStart = _vm.Col1Width;
        _col2WidthStart = _vm.Col2Width;
        _currentSplitter = splitter;
        splitter.CaptureMouse();
    }

    private void GridSplitter_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_currentSplitter == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPos = e.GetPosition(this);
        var delta = currentPos.X - _gridSplitterStart.X;

        if (_currentSplitter.Name == "Splitter0")
        {
            _vm.Col0Width = Math.Max(30, _col0WidthStart + delta);
            _vm.Col1Width = Math.Max(100, _col1WidthStart - delta);
        }
        else if (_currentSplitter.Name == "Splitter1")
        {
            _vm.Col1Width = Math.Max(100, _col1WidthStart + delta);
            _vm.Col2Width = Math.Max(100, _col2WidthStart - delta);
        }
        else if (_currentSplitter.Name == "Splitter2")
        {
            _vm.Col2Width = Math.Max(100, _col2WidthStart + delta);
        }
    }
}
