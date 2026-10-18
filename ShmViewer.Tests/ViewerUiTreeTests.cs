using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using Xunit;
using static ShmViewer.Core.Dat.DatSourceVersion;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    private static async Task<DataGridRow> ShowTreeRow(DataGrid grid, DatRowViewModel row)
    {
        grid.ScrollIntoView(row);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        return Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(row));
    }

    private static async Task ClickTreeArrow(DataGrid grid, DatRowViewModel row, DatSourceVersion version)
    {
        var container = await ShowTreeRow(grid, row);
        var branch = AllVisuals<DatTreeBranch>(container).Single(b => b.Version == version);
        var button = branch.Children.OfType<Button>().Single();
        Assert.True(button.IsVisible); Assert.True(button.IsEnabled);
        typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);
        await row.Owner.ToggleCommand.ExecutionTask!;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    [Theory]
    [InlineData(DatComparisonMode.All)]
    [InlineData(DatComparisonMode.AllDifferences)]
    [InlineData(DatComparisonMode.StructureDifferences)]
    public Task TreeArrowClicksSynchronizeBothSidesAndKeepNestedExpansion(DatComparisonMode mode) => RunSta(async () =>
    {
        using var setup = await DatTreeInteractionTests.CreateSetup(mode); var diff = setup.Model;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)Find<DatDiffView>(window)!.FindName("ComparisonGrid");
            var root = diff.FindVisibleRow("")!; var items = diff.FindVisibleRow("items")!;
            if (!diff.IsExpanded(items.Path)) await ClickTreeArrow(grid, items, Version2);
            var extra = diff.FindVisibleRow("items[2]")!;
            if (!diff.IsExpanded(extra.Path)) await ClickTreeArrow(grid, extra, Version1);
            var nested = diff.FindVisibleRow("items[2].nested")!;
            if (!diff.IsExpanded(nested.Path)) await ClickTreeArrow(grid, nested, Version1);
            var value = diff.FindVisibleRow("items[2].nested.value")!;
            grid.SelectedItem = value;
            await ClickTreeArrow(grid, items, Version1);
            Assert.Null(diff.FindVisibleRow(value.Path)); Assert.Same(items, grid.SelectedItem);
            var collapsed = await ShowTreeRow(grid, items);
            Assert.Equal(new[] { "◀", "▶" }, AllVisuals<DatTreeBranch>(collapsed).Select(b => b.Children.OfType<Button>().Single().Content));
            await ClickTreeArrow(grid, items, Version2);
            Assert.Same(value, diff.FindVisibleRow(value.Path));
            Assert.All(AllVisuals<DatTreeBranch>(await ShowTreeRow(grid, items)), b => Assert.Equal("▼", b.Children.OfType<Button>().Single().Content));
            await ClickTreeArrow(grid, root, Version2); Assert.Single(diff.Rows);
            await ClickTreeArrow(grid, root, Version1); Assert.Same(value, diff.FindVisibleRow(value.Path));
            grid.SelectedItem = root; grid.SelectedItems.Add(items);
            await ClickTreeArrow(grid, extra, Version1);
            Assert.Equal(2, grid.SelectedItems.Count); Assert.True(grid.SelectedItems.Contains(root)); Assert.True(grid.SelectedItems.Contains(items));
            Assert.Empty(setup.Errors);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TreeVisualsHideMissingSideAndStrikeOnlyDeletedPathsAfterUndoAndRecycling() => RunSta(async () =>
    {
        using var setup = await DatTreeInteractionTests.CreateSetup(DatComparisonMode.All); var diff = setup.Model;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)Find<DatDiffView>(window)!.FindName("ComparisonGrid");
            var items = diff.FindVisibleRow("items")!; await ClickTreeArrow(grid, items, Version1);
            var extra = diff.FindVisibleRow("items[2]")!; await ClickTreeArrow(grid, extra, Version1);
            await ClickTreeArrow(grid, diff.FindVisibleRow("items[2].nested")!, Version1);
            var child = diff.FindVisibleRow("items[2].nested.value")!;
            var branches = AllVisuals<DatTreeBranch>(await ShowTreeRow(grid, extra)).ToArray();
            Assert.Equal(Visibility.Visible, branches[0].Children[0].Visibility);
            Assert.Equal(Visibility.Hidden, branches[1].Children[0].Visibility);
            Assert.Equal(Visibility.Hidden, branches[1].Children.OfType<Button>().Single().Visibility);
            Assert.Equal(branches[0].ActualWidth, branches[1].ActualWidth);
            Render(window, "dat-tree-missing-side.png");
            await diff.ApplyRightRowCommand.ExecuteAsync(items);
            async Task CheckDeleted(DatRowViewModel row, bool deleted)
            {
                var container = await ShowTreeRow(grid, row);
                var paths = AllVisuals<TextBlock>(container).Where(t => t.Text == row.Path).ToArray();
                Assert.Equal(deleted ? 2 : 1, paths.Length);
                Assert.All(paths, t => Assert.Equal(deleted, t.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true));
                Assert.All(AllVisuals<TextBlock>(container).Except(paths), t => Assert.False(t.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true));
                if (deleted) Assert.All(AllVisuals<DatTreeBranch>(container), b =>
                {
                    Assert.Equal(Visibility.Hidden, b.Children[0].Visibility);
                    Assert.Equal(Visibility.Hidden, b.Children.OfType<Button>().Single().Visibility);
                });
            }
            await CheckDeleted(extra, true); await CheckDeleted(child, true);
            Render(window, "dat-tree-deleted-paths.png");
            await diff.UndoCommand.ExecuteAsync(null); await CheckDeleted(extra, false); await CheckDeleted(child, false);
            await diff.RedoCommand.ExecuteAsync(null); await CheckDeleted(extra, true);
            await ClickTreeArrow(grid, items, Version2); await ClickTreeArrow(grid, items, Version1);
            await CheckDeleted(child, true);
            await diff.UndoCommand.ExecuteAsync(null); await CheckDeleted(child, false);
            Assert.Empty(setup.Errors);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public Task LargeTreeTogglesPreserveSelectionWithoutMaterializingHiddenRows(int count) => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        var documents = DatPerformanceTests.Documents(count);
        foreach (var pair in new[] { (diff.Left.DatPath, documents.Left), (diff.Right.DatPath, documents.Right) })
        {
            File.WriteAllBytes(pair.DatPath, pair.Item2.Data);
            File.WriteAllBytes(DatSchemaFile.PathFor(pair.DatPath), DatSchemaFile.Serialize(pair.Item2.Schema, pair.Item2.Data));
        }
        diff.Left.UseSchemaFile = diff.Right.UseSchemaFile = true;
        await diff.DiffCommand.ExecuteAsync(null); await diff.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        var clock = Stopwatch.StartNew(); var last = clock.Elapsed; var intervals = new List<double>(); var timings = new List<double>();
        var pulse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        pulse.Tick += (_, _) => { var now = clock.Elapsed; intervals.Add((now - last).TotalMilliseconds); last = now; };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)Find<DatDiffView>(window)!.FindName("ComparisonGrid");
            var selected = diff.FindVisibleRow($"items[{count - 1}].value")!;
            grid.SelectedItem = selected; await ShowTreeRow(grid, selected);
            var first = diff.FindVisibleRow("items[0]")!; var items = diff.FindVisibleRow("items")!;
            await ShowTreeRow(grid, first); last = clock.Elapsed; pulse.Start();
            async Task Toggle(DatRowViewModel row, DatSourceVersion version)
            {
                var watch = Stopwatch.StartNew(); await ClickTreeArrow(grid, row, version); timings.Add(watch.Elapsed.TotalMilliseconds);
                Assert.InRange(AllVisuals<DataGridRow>(grid).Count(), 1, 99);
                Assert.InRange(diff.CachedRowCount, 1, 99);
            }
            await Toggle(first, Version1); Assert.Same(selected, grid.SelectedItem);
            await Toggle(first, Version2); Assert.Same(selected, grid.SelectedItem);
            await Toggle(items, Version1); Assert.Same(items, grid.SelectedItem); Assert.Equal(2, diff.Rows.Count);
            await Toggle(items, Version2); Assert.Same(selected, diff.FindVisibleRow(selected.Path));
            Assert.Equal(count * 2 + 2, diff.Rows.Count); Assert.True(diff.IsExpanded(first.Path));
            Assert.NotEmpty(intervals); Assert.Empty(setup.Errors);
            _performanceOutput.WriteLine($"tree elements={count:N0}, toggles={string.Join(", ", timings.Select(t => $"{t:F1}ms"))}, maxDispatcherGap={intervals.Max():F1}ms, cachedRows={diff.CachedRowCount}, realized={AllVisuals<DataGridRow>(grid).Count()}");
        }
        finally { pulse.Stop(); window.Close(); }
    });
}
