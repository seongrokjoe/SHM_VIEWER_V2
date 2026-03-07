using ShmViewer.ViewModels;
using ShmViewer.Views.Dialogs;
using System.ComponentModel;
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

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Add lazy loading event handler (on Window so all tabs' TreeViews are covered)
        this.Loaded += (s, e) =>
        {
            this.AddHandler(TreeViewItem.ExpandedEvent,
                new RoutedEventHandler(TreeViewItem_Expanded));

            // Setup search results grouping
            SetupSearchResultsGrouping();
        };

        this.PreviewMouseUp += (s, e) =>
        {
            if (_currentSplitter != null)
            {
                _currentSplitter.ReleaseMouseCapture();
                _currentSplitter = null;
            }
        };
    }

    private void SetupSearchResultsGrouping()
    {
        var cvs = new CollectionViewSource { Source = _vm.SearchResults };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription("TabName"));
        SearchResultGrid.ItemsSource = cvs.View;

        // Update when search results change
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SearchResults))
            {
                cvs.Source = _vm.SearchResults;
            }
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

        // Show context menu
        var contextMenu = new ContextMenu();
        var menuItem = new MenuItem
        {
            Header = "바이너리 확인"
        };
        menuItem.Click += (s, args) => ShowDetailPopup(node);
        contextMenu.Items.Add(menuItem);

        contextMenu.PlacementTarget = tree;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void ShowDetailPopup(TreeNodeViewModel node)
    {
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
        // Find the active tab's TreeView by navigating through TabControl
        var tabControl = FindVisualChild<TabControl>(this);
        if (tabControl?.SelectedItem is not ShmTabViewModel selectedTab) return;

        // Find the TabItem containing the selected tab
        var tabItem = tabControl.ItemContainerGenerator.ContainerFromItem(selectedTab) as TabItem;
        if (tabItem == null) return;

        // Find TreeView within the selected tab's content
        var treeView = FindVisualChild<TreeView>(tabItem);
        if (treeView == null) return;

        var item = FindTreeViewItem(treeView, node);
        if (item != null)
        {
            // Use Background priority to wait for expand animation to complete
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                item.BringIntoView();
            });
        }
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

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        if (item.DataContext is not TreeNodeViewModel node) return;

        // Trigger lazy loading if this node is lazy
        if (node.IsLazy)
        {
            node.ExpandLoad();
        }
    }

    private Point _gridSplitterStart;
    private double _col0WidthStart;
    private double _col1WidthStart;
    private double _col2WidthStart;
    private GridSplitter? _currentSplitter;

    private void GridSplitter_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not GridSplitter splitter) return;
        _gridSplitterStart = e.GetPosition(this);
        _col0WidthStart = _vm.Col0Width;
        _col1WidthStart = _vm.Col1Width;
        _col2WidthStart = _vm.Col2Width;
        _currentSplitter = splitter;
        splitter.CaptureMouse();
    }

    private void GridSplitter_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_currentSplitter == null || e.LeftButton != MouseButtonState.Pressed) return;

        var currentPos = e.GetPosition(this);
        var delta = currentPos.X - _gridSplitterStart.X;

        if (_currentSplitter.Name == "Splitter0")
        {
            // Resize Col0 (SIZE +offset) and Col1 (TYPE)
            var newCol0 = Math.Max(30, _col0WidthStart + delta);
            _vm.Col0Width = newCol0;
            _vm.Col1Width = Math.Max(100, _col1WidthStart - delta);
        }
        else if (_currentSplitter.Name == "Splitter1")
        {
            // Resize Col1 (TYPE) and Col2 (NAME)
            var newCol1 = Math.Max(100, _col1WidthStart + delta);
            _vm.Col1Width = newCol1;
            _vm.Col2Width = Math.Max(100, _col2WidthStart - delta);
        }
        else if (_currentSplitter.Name == "Splitter2")
        {
            // Resize Col2 (NAME) - Col3 (VALUE) is * so it auto-adjusts
            _vm.Col2Width = Math.Max(100, _col2WidthStart + delta);
        }
    }
}
