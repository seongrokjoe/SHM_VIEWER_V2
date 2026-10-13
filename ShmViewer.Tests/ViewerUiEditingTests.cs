using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    [Fact]
    public Task StructArrayMemberCheckboxesAndEmptyCellsWorkInRenderedView() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        File.WriteAllText(diff.Left.Headers[0].Path, DatArrayMemberTests.Header);
        File.WriteAllBytes(diff.Left.DatPath, DatArrayMemberTests.Input(false).Bytes.ToArray());
        File.WriteAllBytes(diff.Right.DatPath, DatArrayMemberTests.Input(true).Bytes.ToArray());
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1600, Height = 1000, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); diff.DifferencesOnly = true;
            var view = Find<DatDiffView>(window)!;
            var grid = (DataGrid)view.FindName("ComparisonGrid");
            DatRowViewModel Row(int i) => diff.Rows.Single(r => r.Node.Path == DatArrayMemberTests.Prefix + $"[{i}]");
            DataGridRow Container(int i)
            {
                var row = Row(i); grid.SelectedItem = row; grid.ScrollIntoView(row); grid.UpdateLayout();
                return (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
            }
            static void Click(CheckBox toggle) =>
                typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(toggle, null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var container = Container(0);
            var choice = AllVisuals<CheckBox>(container).Single(c => ReferenceEquals(c.Command, diff.CheckRightCommand));
            Assert.True(choice.IsVisible); Assert.True(choice.IsEnabled); Assert.False(choice.IsChecked);
            Assert.Equal(string.Empty, Row(0).TargetValue); Assert.False(diff.CanSave);
            Render(window, "dat-array-members-unassigned.png");
            Click(choice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("20", Row(0).TargetValue); Assert.True(Row(0).RightChecked);

            container = Container(3);
            var cell = Find<DatValueCell>(container)!;
            cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(diff.HasCellEdit); Assert.Equal(string.Empty, diff.CellEditorText);
            var editor = (TextBox)cell.FindName("ValueEditor");
            Assert.True(editor.IsVisible);
            editor.Text = "33"; editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(diff.HasCellEdit); Assert.Equal("33", Row(3).TargetValue);

            Container(6);
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, Key.F2)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(diff.HasCellEdit); Assert.Equal(string.Empty, diff.CellEditorText);
            diff.CellEditorText = "0"; Assert.True(diff.CommitCellEdit());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(diff.CanSave); Assert.Equal(64, diff.Session!.Result.Length);
            Render(window, "dat-array-members-resolved.png");

            container = Container(0);
            choice = AllVisuals<CheckBox>(container).Single(c => ReferenceEquals(c.Command, diff.CheckRightCommand));
            Click(choice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(string.Empty, Row(0).TargetValue); Assert.NotNull(Row(0).Node.Target); Assert.False(diff.CanSave);
            diff.UndoCommand.Execute(null); Assert.True(diff.CanSave);
            diff.RedoCommand.Execute(null); Assert.False(diff.CanSave);
            diff.ShowHex = true; Assert.Equal(string.Empty, Row(0).TargetValue);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LoadOnlyCellsColumnsWheelAndHeaderSelectionWorkInRenderedView() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        const string header = "struct Item { int x; int y; }; struct Root { Item items[80]; };";
        File.WriteAllText(diff.Left.Headers[0].Path, header);
        var data = DatMergeTests.Document(header, ("items[0].x", "7"));
        File.WriteAllBytes(diff.Left.DatPath, data.Bytes.ToArray());
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            diff.LoadOnly = true; await diff.DiffCommand.ExecuteAsync(null);
            var view = Find<DatDiffView>(window)!;
            var grid = (DataGrid)view.FindName("ViewerGrid");
            diff.ToggleViewerCommand.Execute(diff.ViewerRows.Single(r => r.Path == "items"));
            diff.ToggleViewerCommand.Execute(diff.ViewerRows.Single(r => r.Path == "items[0]"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, grid.Columns[2].Visibility);
            diff.ShowViewerOffset = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Visible, grid.Columns[2].Visibility);
            diff.ShowViewerOffset = false; diff.ShowViewerType = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, grid.Columns[1].Visibility);

            var row = diff.ViewerRows.Single(r => r.Path == "items[0].x");
            grid.SelectedItem = row; grid.ScrollIntoView(row); grid.UpdateLayout();
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, Key.F2)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(diff.HasCellEdit);
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
            var cell = Find<DatValueCell>(container)!;
            var editor = (TextBox)cell.FindName("ValueEditor");
            Assert.True(editor.IsVisible); Assert.Equal(FontWeights.Bold, editor.FontWeight);
            editor.Text = "99"; editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(diff.HasCellEdit);
            Assert.Equal("99", diff.ViewerRows.Single(r => r.Path == "items[0].x").Value);
            Assert.Equal(data.Bytes.ToArray(), diff.ViewerDocument!.Bytes.ToArray());
            Assert.Equal(data.Bytes.Length, diff.ViewerSession!.Bytes.Length);

            var scroll = Find<ScrollViewer>(grid)!;
            scroll.ScrollToTop(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, scroll.VerticalOffset);
            grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -60) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.Equal(1, scroll.VerticalOffset);
            grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -60) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.Equal(2, scroll.VerticalOffset);
            Render(window, "dat-load-only-editable.png");
            Assert.Equal(SelectionMode.Extended, ((ListBox)((DatInputView)view.FindName("LeftInput")).FindName("HeaderList")).SelectionMode);
            Assert.Equal(SelectionMode.Extended, ((ListBox)window.FindName("RealTimeHeaderList")).SelectionMode);
            diff.ResetCommand.Execute(null);
            Assert.False(diff.ShowViewerOffset); Assert.True(diff.ShowViewerType);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RealtimeLoadingRemainsVisibleThroughTreePreparationAndStopsAfterCompletion() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs();
        using var vm = new MainViewModel(new MemoryStore(), _ => true);
        vm.AddHeaderFiles(new[] { setup.Model.Left.Headers[0].Path }); await vm.Initialization;
        vm.NewShmName = "TEST_LOAD"; vm.NewStructName = "Root"; vm.LoadShmTabCommand.Execute(null);
        var tab = Assert.Single(vm.Tabs);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var refresh = vm.RefreshHeadersCommand.ExecuteAsync(null);
            Assert.True(vm.IsParsing); Assert.True(tab.IsBusy); Assert.False(tab.IsStatusError);
            Assert.Equal("헤더 분석 중...", tab.BusyMessage);
            window.UpdateLayout();
            Assert.Contains(AllVisuals<ProgressBar>(window), p => p.IsVisible && p.IsIndeterminate);
            Render(window, "realtime-header-loading.png");
            await refresh;
            Assert.True(tab.IsTreeBuilt); Assert.False(tab.IsBusy); Assert.False(tab.IsInputBlocked);
            Assert.False(vm.IsParsing); Assert.False(tab.IsStatusError);
            vm.RemoveHeaderFiles(vm.HeaderFiles.ToArray()); await vm.Initialization;
            Assert.False(tab.IsBusy); Assert.True(tab.IsStatusError);
            Assert.Empty(tab.RootNodes);
        }
        finally { window.Close(); }
    });
}
