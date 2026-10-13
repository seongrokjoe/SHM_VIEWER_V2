using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    [Fact]
    public Task DeepTreeKeepsActualDepthAndGuidesAfterFilteringAndScrolling() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        var header = "struct N15 { int value; };";
        for (var i = 14; i >= 1; i--) header += $"struct N{i} {{ N{i + 1} child; int sibling; }};";
        header += "struct Root { N1 branch; int trailing; };";
        var path = "branch" + string.Concat(Enumerable.Repeat(".child", 14)) + ".value";
        File.WriteAllText(diff.Left.Headers[0].Path, header);
        File.WriteAllBytes(diff.Left.DatPath, DatMergeTests.Document(header, (path, "1")).Bytes.ToArray());
        File.WriteAllBytes(diff.Right.DatPath, DatMergeTests.Document(header, (path, "2")).Bytes.ToArray());
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1000, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); diff.DifferencesOnly = true;
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ComparisonGrid");
            var leaf = diff.Rows.Single(r => r.Node.Path == path);
            Assert.True(leaf.Node.Depth > 12); Assert.Equal(leaf.Node.Depth * 14, leaf.Indent.Left);
            Assert.All(leaf.TreeBranches, b => Assert.False(b));
            grid.ScrollIntoView(leaf); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Assert.True(grid.Columns.Sum(c => c.ActualWidth) > grid.ActualWidth);
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(leaf);
            Assert.All(AllVisuals<DatTreeBranch>(container), branch => Assert.Equal(leaf.Node.Depth * 14 + 22, branch.ActualWidth));
            Render(window, "dat-deep-tree.png");
            diff.DifferencesOnly = false;
            Assert.DoesNotContain(diff.Rows, r => r.Node.Path == path);
            diff.DifferencesOnly = true; Assert.Contains(diff.Rows, r => r.Node.Path == path);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task StructureModeTreeColumnsAndAdvancedEditorWorkInRenderedView() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        var samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestHeaders/DatDiff"));
        foreach (var (input, index) in new[] { (diff.Left, 1), (diff.Right, 2) })
        {
            input.Headers.Clear(); input.AddFiles(new[] { Path.Combine(samples, $"structure_v{index}.h") });
            input.DatPath = Path.Combine(samples, $"structure_v{index}.dat");
        }
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1550, Height = 1000, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.NotNull(diff.Session);
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ComparisonGrid");
            var advanced = (Expander)view.FindName("AdvancedEditing"); Assert.False(advanced.IsExpanded);
            diff.ChooseStructureBase = () => DatSourceVersion.Version2; diff.StructureComparison = true;
            Assert.Empty(diff.Rows.Single(r => r.Node.Path == "common").TargetValue);
            Button ToggleAt(string path, int column)
            {
                var row = diff.Rows.Single(r => r.Node.Path == path);
                grid.ScrollIntoView(row); grid.UpdateLayout();
                var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
                var controls = AllVisuals<DatTreeBranch>(container).ToArray(); Assert.Equal(3, controls.Length);
                return Find<Button>(controls[column])!;
            }
            for (var column = 0; column < 3; column++)
            {
                var button = ToggleAt("settings", column);
                typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(button, null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Contains(diff.Rows, r => r.Node.Path == "settings.mode");
                button = ToggleAt("settings", (column + 1) % 3);
                typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(button, null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.DoesNotContain(diff.Rows, r => r.Node.Path == "settings.mode");
            }
            diff.DifferencesOnly = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.DoesNotContain(diff.Rows, r => r.Node.Path == "common");
            Render(window, "dat-structure-filter.png");
            diff.DifferencesOnly = false;
            diff.ToggleCommand.Execute(diff.Rows.Single(r => r.Node.Path == "settings"));
            var mode = diff.Rows.Single(r => r.Node.Path == "settings.mode");
            Assert.True(diff.BeginCellEdit(mode)); Assert.Empty(diff.CellEditorText);
            diff.CellEditorText = "bad"; Assert.False(diff.CommitCellEdit());
            Assert.False(advanced.IsExpanded);
            window.UpdateLayout();
            Assert.Contains(AllVisuals<TextBlock>(view), t => t.IsVisible && t.Text == diff.CellEditorError);
            diff.CellEditorText = "44"; Assert.True(diff.CommitCellEdit());
            Assert.Equal("44", diff.Rows.Single(r => r.Node.Path == "settings.mode").TargetValue);
            diff.SelectedRow = diff.Rows.Single(r => r.Node.Path == "settings"); diff.UseRightCommand.Execute(null);
            Assert.Equal(new[] { "mode", "level", "added" }, diff.SelectedRow!.Node.Target!.Type.Members.Select(m => m.Name));
            Assert.Equal(1, diff.SelectedRow.Node.Target.Type.Pack);
            Render(window, "dat-structure-comparison.png");
            advanced.IsExpanded = true; Assert.True(diff.AdvancedEditingExpanded);
            diff.ResetCommand.Execute(null); Assert.False(advanced.IsExpanded);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RealTimeHeaderCardsWrapAndKeepCompactWidthAndTooltips() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs();
        using var vm = new MainViewModel(new MemoryStore(), _ => true, setup.Model);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); await vm.Initialization;
            var folder = Path.GetDirectoryName(setup.Model.Left.DatPath)!;
            for (var i = 0; i < 12; i++)
            {
                var path = Path.Combine(folder, i == 11 ? new string('L', 110) + ".h" : $"Header_{i:00}.h");
                if (i != 11) File.WriteAllText(path, $"struct Card{i} {{ int value; }};");
                vm.HeaderFiles.Add(new(path));
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var list = (ListBox)window.FindName("RealTimeHeaderList");
            var cards = AllVisuals<ListBoxItem>(list).ToArray(); Assert.Equal(12, cards.Length);
            Assert.All(cards, c => Assert.InRange(c.ActualWidth, 30, 240));
            Assert.InRange(list.ActualHeight, 30, 96);
            var positions = cards.Select(c => c.TranslatePoint(new Point(), list)).ToArray();
            Assert.Equal(positions[0].Y, positions[1].Y); Assert.True(positions[1].X > positions[0].X);
            Assert.True(positions.Last().Y > positions[0].Y);
            var border = AllVisuals<Border>(cards.Last()).Single(b => b.ToolTip is ToolTip);
            var tooltip = (ToolTip)border.ToolTip; tooltip.PlacementTarget = border; tooltip.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains(((StackPanel)tooltip.Content).Children.OfType<TextBlock>(), t => t.Text.Contains("파일 없음"));
            tooltip.IsOpen = false;
            list.SelectedItems.Add(vm.HeaderFiles[0]); list.SelectedItems.Add(vm.HeaderFiles[1]); Assert.Equal(2, list.SelectedItems.Count);
            Render(window, "realtime-compact-headers.png");
        }
        finally { window.Close(); }
    });
}
