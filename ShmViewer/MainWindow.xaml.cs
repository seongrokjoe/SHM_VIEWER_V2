using ShmViewer.ViewModels;
using ShmViewer.Views.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.Threading;

namespace ShmViewer;

public partial class MainWindow : Window
{
    private const int SearchNavigationMaxAttempts = 10;

    private readonly MainViewModel _vm;
    private Point _gridSplitterStart;
    private double _col0WidthStart;
    private double _col1WidthStart;
    private double _col2WidthStart;
    private GridSplitter? _currentSplitter;
    private int _searchNavigationRequestId;
    private TreeNodeViewModel? _lastSearchNavigatedNode;

    private enum SearchNavigationFailureReason
    {
        None,
        SelectedContentUnavailable,
        TreeViewUnavailable,
        NodePathNotFound,
        AncestorContainerUnavailable,
        TargetContainerUnavailable
    }

    private sealed class SearchNavigationResult
    {
        public SearchNavigationFailureReason FailureReason { get; init; }
        public TreeViewItem? TargetItem { get; init; }
        public TreeNodeViewModel? Node { get; init; }
        public IReadOnlyList<TreeNodeViewModel> AncestorPath { get; init; } = Array.Empty<TreeNodeViewModel>();
        public bool Succeeded => FailureReason == SearchNavigationFailureReason.None && TargetItem != null && Node != null;
    }

    private sealed class SelectedTreeViewContext
    {
        public ContentPresenter? SelectedContentHost { get; init; }
        public TreeView? TreeView { get; init; }
    }

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
        SelectTreeViewItemFromSource(e.OriginalSource as DependencyObject);
    }

    private void TreeCell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TreeNodeViewModel node ||
            element.Tag is not TreeCellKind cellKind)
        {
            return;
        }

        SelectTreeViewItemFromSource(element);

        var action = TreeCellContextMenuPolicy.ResolveAction(cellKind, node);
        switch (action)
        {
            case TreeCellContextAction.Copy:
                ShowCopyMenu(element, GetDisplayedText(element));
                e.Handled = true;
                break;

            case TreeCellContextAction.ShowBinary:
                ShowBinaryMenu(element, node);
                e.Handled = true;
                break;
        }
    }

    private void ShowCopyMenu(FrameworkElement placementTarget, string text)
    {
        var menuItem = new MenuItem
        {
            Header = "복사하기"
        };
        menuItem.Click += (_, _) => CopyText(text);
        OpenContextMenu(placementTarget, menuItem);
    }

    private void ShowBinaryMenu(FrameworkElement placementTarget, TreeNodeViewModel node)
    {
        var menuItem = new MenuItem
        {
            Header = "바이너리 확인"
        };
        menuItem.Click += (_, _) => ShowDetailPopup(node);
        OpenContextMenu(placementTarget, menuItem);
    }

    private static void OpenContextMenu(FrameworkElement placementTarget, params MenuItem[] items)
    {
        if (items.Length == 0)
            return;

        var contextMenu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.MousePoint
        };

        foreach (var item in items)
            contextMenu.Items.Add(item);

        contextMenu.IsOpen = true;
    }

    private static string GetDisplayedText(FrameworkElement element)
    {
        if (element is TextBlock textBlock)
            return textBlock.Text;

        var childTextBlock = FindVisualChild<TextBlock>(element);
        return childTextBlock?.Text ?? string.Empty;
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text ?? string.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"복사 실패: {ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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
        SelectTreeViewItemFromSource(e.OriginalSource as DependencyObject);
    }

    private static void SelectTreeViewItemFromSource(DependencyObject? source)
    {
        var item = FindAncestor<TreeViewItem>(source);
        if (item != null)
            item.IsSelected = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T target)
                return target;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.SearchCommand.Execute(null);
    }

    private async void SearchResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        var result = ResolveSearchResultFromDoubleClick(e) ?? grid.SelectedItem as SearchResultViewModel;
        if (result == null)
            return;

        await NavigateToSearchResultAsync(result);
    }

    private async Task NavigateToSearchResultAsync(SearchResultViewModel result)
    {
        var requestId = Interlocked.Increment(ref _searchNavigationRequestId);

        ResetPreviousSearchNavigationState();

        var navigation = await TryNavigateToSearchResultAsync(result, requestId);
        if (!navigation.Succeeded)
        {
            TraceSearchNavigationFailure(result, navigation.FailureReason);
            return;
        }

        result.Node = navigation.Node;
        result.AncestorPath = navigation.AncestorPath.ToList();
        _lastSearchNavigatedNode = navigation.Node;

        ApplySelectionToTreeViewItem(navigation.TargetItem!, navigation.Node!);
    }

    private static SearchResultViewModel? ResolveSearchResultFromDoubleClick(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return null;

        var row = FindAncestor<DataGridRow>(source);
        return row?.Item as SearchResultViewModel;
    }

    private void ResetPreviousSearchNavigationState()
    {
        if (_lastSearchNavigatedNode != null)
        {
            _lastSearchNavigatedNode.IsHighlighted = false;
            _lastSearchNavigatedNode.IsSelected = false;
            _lastSearchNavigatedNode = null;
        }

        foreach (var entry in _vm.SearchResults)
        {
            if (entry.Node != null)
                entry.Node.IsHighlighted = false;
        }
    }

    private async Task<SearchNavigationResult> TryNavigateToSearchResultAsync(
        SearchResultViewModel result,
        int requestId)
    {
        _vm.SelectedTab = result.Tab;

        var treeContext = await WaitForTreeViewAsync(result.Tab, requestId);
        if (!IsCurrentSearchNavigation(requestId))
        {
            return new SearchNavigationResult
            {
                FailureReason = SearchNavigationFailureReason.TreeViewUnavailable
            };
        }

        if (treeContext.SelectedContentHost == null)
        {
            return new SearchNavigationResult
            {
                FailureReason = SearchNavigationFailureReason.SelectedContentUnavailable
            };
        }

        if (treeContext.TreeView == null)
        {
            return new SearchNavigationResult
            {
                FailureReason = SearchNavigationFailureReason.TreeViewUnavailable
            };
        }

        var match = await SearchNavigationHelper.FindNodeByPathAsync(result.Tab, result.NodePath);
        if (match == null || !IsCurrentSearchNavigation(requestId))
        {
            return new SearchNavigationResult
            {
                FailureReason = SearchNavigationFailureReason.NodePathNotFound
            };
        }

        match.Node.IsHighlighted = true;
        match.Node.IsSelected = true;

        var parent = await ExpandAncestorChainAsync(treeContext.TreeView, match.AncestorPath, requestId);
        if (parent == null || !IsCurrentSearchNavigation(requestId))
        {
            ResetNodeNavigationState(match.Node);
            return new SearchNavigationResult
            {
                FailureReason = SearchNavigationFailureReason.AncestorContainerUnavailable
            };
        }

        var item = await WaitForContainerAsync(parent, match.Node, requestId);
        if (item == null || !IsCurrentSearchNavigation(requestId))
        {
            ResetNodeNavigationState(match.Node);
            return new SearchNavigationResult
            {
                FailureReason = SearchNavigationFailureReason.TargetContainerUnavailable
            };
        }

        await Dispatcher.InvokeAsync(() =>
        {
            item.UpdateLayout();
            item.BringIntoView();
            EnsureTreeItemVisible(treeContext.TreeView, item);
        }, DispatcherPriority.Background);

        return new SearchNavigationResult
        {
            FailureReason = SearchNavigationFailureReason.None,
            TargetItem = item,
            Node = match.Node,
            AncestorPath = match.AncestorPath
        };
    }

    private bool IsCurrentSearchNavigation(int requestId) =>
        requestId == Volatile.Read(ref _searchNavigationRequestId);

    private async Task<SelectedTreeViewContext> WaitForTreeViewAsync(ShmTabViewModel tab, int requestId)
    {
        for (int attempt = 0; attempt < SearchNavigationMaxAttempts; attempt++)
        {
            if (!IsCurrentSearchNavigation(requestId))
                return new SelectedTreeViewContext();

            var treeContext = FindTreeViewForTab(tab);
            if (treeContext.TreeView != null && treeContext.TreeView.IsLoaded)
            {
                treeContext.TreeView.ApplyTemplate();
                treeContext.TreeView.UpdateLayout();
                return treeContext;
            }

            await Dispatcher.InvokeAsync(() => { }, attempt == 0
                ? DispatcherPriority.Loaded
                : DispatcherPriority.Background);
        }

        return FindTreeViewForTab(tab);
    }

    private async Task<ItemsControl?> ExpandAncestorChainAsync(
        TreeView treeView,
        IReadOnlyList<TreeNodeViewModel> ancestorPath,
        int requestId)
    {
        ItemsControl parent = treeView;

        foreach (var ancestor in ancestorPath)
        {
            if (!IsCurrentSearchNavigation(requestId))
                return null;

            ancestor.IsExpanded = true;

            var container = await WaitForContainerAsync(parent, ancestor, requestId);
            if (container == null)
                return null;

            await Dispatcher.InvokeAsync(() =>
            {
                container.IsExpanded = true;
                container.ApplyTemplate();
                container.UpdateLayout();
            }, DispatcherPriority.Loaded);

            parent = container;
        }

        return parent;
    }

    private async Task<TreeViewItem?> WaitForContainerAsync(
        ItemsControl parent,
        object item,
        int requestId)
    {
        for (int attempt = 0; attempt < SearchNavigationMaxAttempts; attempt++)
        {
            if (!IsCurrentSearchNavigation(requestId))
                return null;

            parent.ApplyTemplate();
            parent.UpdateLayout();

            var container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (container != null)
            {
                container.ApplyTemplate();
                container.UpdateLayout();
                return container;
            }

            TryBringItemContainerIntoView(parent, item);

            container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (container != null)
            {
                container.ApplyTemplate();
                container.UpdateLayout();
                return container;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (parent is TreeViewItem parentItem && !parentItem.IsExpanded)
                    parentItem.IsExpanded = true;

                parent.ApplyTemplate();
                parent.UpdateLayout();
            }, attempt == 0
                ? DispatcherPriority.Loaded
                : DispatcherPriority.Background);
        }

        return null;
    }

    private SelectedTreeViewContext FindTreeViewForTab(ShmTabViewModel tab)
    {
        var tabControl = FindVisualChild<TabControl>(this);
        if (tabControl == null)
            return new SelectedTreeViewContext();

        if (!ReferenceEquals(tabControl.SelectedItem, tab))
            tabControl.SelectedItem = tab;

        tabControl.ApplyTemplate();
        tabControl.UpdateLayout();

        var selectedContentHost = tabControl.Template?.FindName("PART_SelectedContentHost", tabControl) as ContentPresenter;
        if (selectedContentHost != null)
        {
            selectedContentHost.ApplyTemplate();
            selectedContentHost.UpdateLayout();

            var selectedTree = FindVisualDescendant<TreeView>(
                selectedContentHost,
                tree => ReferenceEquals(tree.DataContext, tab));
            if (selectedTree != null)
            {
                return new SelectedTreeViewContext
                {
                    SelectedContentHost = selectedContentHost,
                    TreeView = selectedTree
                };
            }
        }

        var fallbackTree = FindVisualDescendant<TreeView>(
            tabControl,
            tree => ReferenceEquals(tree.DataContext, tab));

        return new SelectedTreeViewContext
        {
            SelectedContentHost = selectedContentHost,
            TreeView = fallbackTree
        };
    }

    private void TryBringItemContainerIntoView(ItemsControl parent, object item)
    {
        var index = parent.Items.IndexOf(item);
        if (index < 0)
            return;

        if (FindItemsHostPanel(parent) is { } vsp)
            vsp.BringIndexIntoViewPublic(index);

        parent.UpdateLayout();
    }

    private static void ApplySelectionToTreeViewItem(TreeViewItem item, TreeNodeViewModel node)
    {
        node.IsSelected = true;
        item.UpdateLayout();
        item.IsSelected = true;
        item.Focus();
        Keyboard.Focus(item);
        item.BringIntoView();
        node.IsHighlighted = false;
    }

    private static void ResetNodeNavigationState(TreeNodeViewModel node)
    {
        node.IsHighlighted = false;
        node.IsSelected = false;
    }

    private static void EnsureTreeItemVisible(TreeView treeView, TreeViewItem item)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(treeView);
        if (scrollViewer == null)
        {
            item.BringIntoView();
            return;
        }

        item.BringIntoView();

        if (item.ActualHeight <= 0)
            return;

        try
        {
            var bounds = item.TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(new Point(0, 0), item.RenderSize));

            if (bounds.Top < 0)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset + bounds.Top - item.ActualHeight));
            }
            else if (bounds.Bottom > scrollViewer.ViewportHeight)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + (bounds.Bottom - scrollViewer.ViewportHeight) + item.ActualHeight);
            }
        }
        catch (InvalidOperationException)
        {
            item.BringIntoView();
        }
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

    private static T? FindVisualDescendant<T>(
        DependencyObject parent,
        Func<T, bool> predicate) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && predicate(match))
                return match;

            var nested = FindVisualDescendant(child, predicate);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static VirtualizingStackPanel? FindItemsHostPanel(ItemsControl parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is VirtualizingStackPanel panel &&
                ReferenceEquals(ItemsControl.GetItemsOwner(panel), parent))
            {
                return panel;
            }

            var nested = FindItemsHostPanel(child, parent);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static VirtualizingStackPanel? FindItemsHostPanel(DependencyObject parent, ItemsControl owner)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is VirtualizingStackPanel panel &&
                ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner))
            {
                return panel;
            }

            var nested = FindItemsHostPanel(child, owner);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static void TraceSearchNavigationFailure(
        SearchResultViewModel result,
        SearchNavigationFailureReason failureReason)
    {
        Debug.WriteLine(
            $"Search navigation failed: {failureReason} | Tab={result.TabName} | Path={result.NodePath}");
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
