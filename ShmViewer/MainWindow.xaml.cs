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
            if (r.Node != null) r.Node.IsHighlighted = false;

        // 탭 전환
        _vm.SelectedTab = result.Tab;

        // 탭 전환 완료 후 경로 기반으로 트리 노드 탐색 및 이동
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var node = FindNodeByPath(result.Tab, result.NodePath, out var ancestorPath);
            if (node == null) return;

            result.Node = node;
            result.AncestorPath = ancestorPath;

            // 조상 노드 펼치기
            foreach (var ancestor in result.AncestorPath)
                ancestor.IsExpanded = true;

            // 대상 노드 하이라이트
            result.Node.IsHighlighted = true;

            // UI 업데이트 후 BringIntoView (가상화 대응: ancestorPath 전달)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                BringNodeIntoView(result.Node, result.AncestorPath);
            });
        });
    }

    /// <summary>
    /// NodePath("m_abc.x") 문자열을 기반으로 트리를 순회하여 해당 TreeNodeViewModel을 찾는다.
    /// Lazy 노드는 자동으로 ExpandLoad()를 호출한다.
    /// </summary>
    private static TreeNodeViewModel? FindNodeByPath(
        ShmTabViewModel tab, string nodePath, out List<TreeNodeViewModel> ancestorPath)
    {
        ancestorPath = new List<TreeNodeViewModel>();
        if (tab.RootNodes.Count == 0) return null;

        var root = tab.RootNodes[0];

        if (string.IsNullOrEmpty(nodePath))
            return root;

        var parts = nodePath.Split('.');
        var current = root;

        foreach (var part in parts)
        {
            // Lazy 노드이면 먼저 펼쳐서 자식 생성
            if (current.IsLazy)
                current.ExpandLoad();

            // "[n]" 같은 배열 플레이스홀더 처리: "[" 이전까지만 이름으로 사용
            var name = part.Contains('[') ? part[..part.IndexOf('[')] : part;
            if (string.IsNullOrEmpty(name)) continue;

            var child = current.Children.FirstOrDefault(c => c.Name == name);
            if (child == null) break; // 더 이상 내려갈 수 없으면 현재 노드 반환

            ancestorPath.Add(current);
            current = child;
        }

        return current;
    }

    private void BringNodeIntoView(TreeNodeViewModel node, List<TreeNodeViewModel>? ancestorPath = null)
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

        // 가상화 대응: 조상 경로를 따라 순차적으로 컨테이너를 실현시킨다
        if (ancestorPath != null && ancestorPath.Count > 0)
        {
            var item = FindTreeViewItemWithVirtualization(treeView, node, ancestorPath);
            if (item != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    item.BringIntoView();
                });
            }
        }
        else
        {
            var item = FindTreeViewItem(treeView, node);
            if (item != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    item.BringIntoView();
                });
            }
        }
    }

    /// <summary>
    /// 가상화된 TreeView에서 조상 경로를 따라 컨테이너를 순차적으로 실현시키며 탐색한다.
    /// </summary>
    private TreeViewItem? FindTreeViewItemWithVirtualization(
        ItemsControl parent, TreeNodeViewModel target, List<TreeNodeViewModel> ancestorPath)
    {
        foreach (var ancestor in ancestorPath)
        {
            parent.UpdateLayout();
            var container = parent.ItemContainerGenerator.ContainerFromItem(ancestor) as TreeViewItem;
            if (container == null)
            {
                // 가상화로 인해 컨테이너 없음 → VirtualizingPanel으로 강제 실현
                var vsp = FindVisualChild<VirtualizingStackPanel>(parent);
                if (vsp != null)
                {
                    int index = parent.Items.IndexOf(ancestor);
                    if (index >= 0)
                        vsp.BringIndexIntoViewPublic(index);
                }
                parent.UpdateLayout();
                container = parent.ItemContainerGenerator.ContainerFromItem(ancestor) as TreeViewItem;
            }
            if (container == null) return null;
            container.IsExpanded = true;
            container.UpdateLayout();
            parent = container;
        }
        // 마지막 타겟 아이템
        parent.UpdateLayout();
        var targetContainer = parent.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
        if (targetContainer == null)
        {
            var vsp2 = FindVisualChild<VirtualizingStackPanel>(parent);
            if (vsp2 != null)
            {
                int index = parent.Items.IndexOf(target);
                if (index >= 0)
                    vsp2.BringIndexIntoViewPublic(index);
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

    private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        if (item.DataContext is not TreeNodeViewModel node) return;

        // Trigger async lazy loading if this node is lazy
        if (node.IsLazy)
        {
            await node.ExpandLoadAsync();
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
