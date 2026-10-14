using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using ShmViewer.Views.Dialogs;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    [Fact]
    public Task MirroredRowsKeepMultiSelectionAndScrollAfterBatchApply() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        var header = "struct Root { " + string.Join(" ", Enumerable.Range(0, 80).Select(i => $"int value{i};")) + " };";
        DatComparisonModeTests.SetHeaders(diff, header, header);
        File.WriteAllBytes(diff.Left.DatPath, DatTestData.Document(header,
            Enumerable.Range(0, 80).Select(i => ($"value{i}", i.ToString())).ToArray()).Data);
        File.WriteAllBytes(diff.Right.DatPath, DatTestData.Document(header,
            Enumerable.Range(0, 80).Select(i => ($"value{i}", (i + 100).ToString())).ToArray()).Data);
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); var tabs = (TabControl)window.FindName("WorkspaceTabs"); tabs.SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null);
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ComparisonGrid");
            var option = (CheckBox)view.FindName("DifferencesOnlyOption");
            ClickToggle(option);
            await diff.ChangeComparisonModeCommand.ExecutionTask!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(option.IsChecked); Assert.True(diff.DifferencesOnly);
            var a = diff.Rows.Single(r => r.Node.Path == "value40");
            var b = diff.Rows.Single(r => r.Node.Path == "value41");
            grid.ScrollIntoView(a); grid.SelectedItems.Add(a); grid.SelectedItems.Add(b);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var scroll = AllVisuals<ScrollViewer>(grid).First();
            var offset = scroll.VerticalOffset;
            Assert.True(offset > 0); Assert.Equal(2, diff.SelectedNodes.Count);
            diff.UseLeftCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Assert.Equal(offset, scroll.VerticalOffset); Assert.Equal(2, grid.SelectedItems.Count);
            Assert.Equal(2, diff.SelectedNodes.Count); Assert.Contains(a, grid.SelectedItems.OfType<DatRowViewModel>());
            Assert.Equal("동일", a.DifferenceText); Assert.Equal("동일", b.DifferenceText);
            Assert.Equal("40", a.RightValue); Assert.Equal("41", b.RightValue);
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(a);
            var branches = AllVisuals<DatTreeBranch>(container).ToArray();
            Assert.Equal(2, branches.Length); Assert.True(branches[0].IsMirrored); Assert.False(branches[1].IsMirrored);
            var values = AllVisuals<DatValueCell>(container).ToArray();
            Assert.Equal(TextAlignment.Right, values[0].ValueTextAlignment); Assert.Equal(TextAlignment.Left, values[1].ValueTextAlignment);
            Assert.True(branches[0].TranslatePoint(new Point(), container).X > values[0].TranslatePoint(new Point(), container).X);
            Assert.True(branches[1].TranslatePoint(new Point(), container).X < values[1].TranslatePoint(new Point(), container).X);
            grid.CurrentCell = new DataGridCellInfo(a, grid.Columns[0]);
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, Key.F2)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(diff.HasCellEdit); Assert.True(diff.ActiveLeft);
            var editor = (TextBox)values[0].FindName("ValueEditor"); Assert.Equal(TextAlignment.Right, editor.TextAlignment);
            Assert.Equal(FlowDirection.LeftToRight, editor.FlowDirection);
            diff.CancelCellEditCommand.Execute(null);
            Render(window, "dat-mirrored-retained-rows.png");

            var structure = (CheckBox)view.FindName("StructureComparisonOption");
            var prompts = 0;
            diff.ConfirmModeChange = () => { prompts++; return DatModeChangeChoice.Cancel; };
            ClickToggle(structure); await diff.ChangeComparisonModeCommand.ExecutionTask!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, prompts); Assert.False(structure.IsChecked); Assert.True(option.IsChecked);
            Assert.Contains(a, diff.Rows);
            diff.ConfirmModeChange = () => DatModeChangeChoice.Discard;
            ClickToggle(structure); await diff.ChangeComparisonModeCommand.ExecutionTask!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(structure.IsChecked); Assert.False(option.IsChecked); Assert.Empty(diff.Rows);
            Assert.False(diff.Session!.IsDirty);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ModeChangeDialogAndWorkspaceTabsHaveReadableLayout() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs();
        using var vm = new MainViewModel(new MemoryStore(), _ => true, setup.Model);
        var window = new MainWindow(vm) { Width = 1000, Height = 700, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var tabs = (TabControl)window.FindName("WorkspaceTabs");
            var items = tabs.Items.Cast<TabItem>().ToArray();
            Assert.All(items, item => Assert.InRange(item.ActualHeight, 38, 52));
            Render(window, "workspace-tabs.png");
            var borders = items.Select(item => (Border)item.Template.FindName("TabBorder", item)).ToArray();
            Assert.True(borders[1].TranslatePoint(new Point(), tabs).X >= borders[0].TranslatePoint(new Point(), tabs).X + borders[0].ActualWidth + 6);
            Assert.Equal(FontWeights.Normal, tabs.FontWeight);
            var dialog = new DatModeChangeDialog { Left = -12000, Top = -12000, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var buttons = AllVisuals<Button>(dialog).ToArray();
                Assert.Equal(new[] { "저장 후 변경", "저장하지 않고 변경", "취소" }, buttons.Select(b => b.Content));
                Assert.All(buttons, button =>
                {
                    var location = button.TranslatePoint(new Point(), (UIElement)dialog.Content);
                    Assert.True(location.X >= 0); Assert.True(location.X + button.ActualWidth <= ((FrameworkElement)dialog.Content).ActualWidth);
                    Assert.True(location.Y >= 0); Assert.True(location.Y + button.ActualHeight <= ((FrameworkElement)dialog.Content).ActualHeight);
                });
                Render(dialog, "dat-mode-change-notice.png");
                buttons[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(DatModeChangeChoice.Cancel, dialog.Choice);
            }
            finally { dialog.Close(); }
        }
        finally { window.Close(); }
    });

    private static void ClickToggle(ToggleButton button) => typeof(ToggleButton)
        .GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);
}
