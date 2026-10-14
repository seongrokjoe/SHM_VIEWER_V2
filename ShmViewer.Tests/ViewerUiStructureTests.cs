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
        var header = "struct N25 { int value; };";
        for (var i = 24; i >= 1; i--) header += $"struct N{i} {{ N{i + 1} child; int sibling; }};";
        header += "struct Root { N1 branch; int trailing; };";
        var path = "branch" + string.Concat(Enumerable.Repeat(".child", 24)) + ".value";
        File.WriteAllText(diff.Left.Headers[0].Path, header);
        File.WriteAllBytes(diff.Left.DatPath, DatTestData.Document(header, (path, "1")).Bytes.ToArray());
        File.WriteAllBytes(diff.Right.DatPath, DatTestData.Document(header, (path, "2")).Bytes.ToArray());
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1000, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); await diff.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ComparisonGrid");
            var leaf = diff.Rows.Single(r => r.Node.Path == path);
            Assert.True(leaf.Node.Depth > 12); Assert.Equal(leaf.Node.Depth * 14, leaf.Indent.Left);
            Assert.All(leaf.TreeBranches, b => Assert.False(b));
            grid.ScrollIntoView(leaf); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Assert.True(grid.Columns.Sum(c => c.ActualWidth) > grid.ActualWidth);
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(leaf);
            Assert.All(AllVisuals<DatTreeBranch>(container), branch => Assert.Equal(leaf.Node.Depth * 14 + 22, branch.ActualWidth));
            Render(window, "dat-deep-tree.png");
            await diff.SetComparisonModeAsync(DatComparisonMode.All);
            Assert.DoesNotContain(diff.Rows, r => r.Node.Path == path);
            await diff.SetComparisonModeAsync(DatComparisonMode.AllDifferences); Assert.Contains(diff.Rows, r => r.Node.Path == path);
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
