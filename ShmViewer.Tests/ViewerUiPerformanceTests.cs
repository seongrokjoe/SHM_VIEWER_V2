using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using Xunit;
using Xunit.Abstractions;

namespace ShmViewer.Tests;

[Collection("DAT performance")]
public sealed partial class ViewerUiTests
{
    private readonly ITestOutputHelper _performanceOutput;
    public ViewerUiTests(ITestOutputHelper output) => _performanceOutput = output;

    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public Task LargeDiffKeepsDispatcherResponsiveAndRefreshesOnlyRealizedRows(int count) => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        var documents = DatPerformanceTests.Documents(count, differentStructure: true);
        foreach (var pair in new[] { (diff.Left.DatPath, documents.Left), (diff.Right.DatPath, documents.Right) })
        {
            File.WriteAllBytes(pair.DatPath, pair.Item2.Data);
            File.WriteAllBytes(DatSchemaFile.PathFor(pair.DatPath), DatSchemaFile.Serialize(pair.Item2.Schema, pair.Item2.Data));
        }
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        var clock = Stopwatch.StartNew(); var last = clock.Elapsed; var largestGap = TimeSpan.Zero; var pulses = 0; var intervals = new List<double>(); var phase = "load"; var gaps = new List<string>(); var lastPause = GC.GetTotalPauseDuration(); var maxPublish = 0.0; var publishWatch = new Stopwatch();
        var pulse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        pulse.Tick += (_, _) =>
        {
            var now = clock.Elapsed; var pause = GC.GetTotalPauseDuration(); intervals.Add((now - last).TotalMilliseconds);
            if (now - last > TimeSpan.FromMilliseconds(90)) gaps.Add($"{phase}:{(now-last).TotalMilliseconds:F0}ms(gc={(pause-lastPause).TotalMilliseconds:F0}ms)");
            largestGap = now - last > largestGap ? now - last : largestGap; last = now; lastPause = pause; pulses++;
        };
        diff.RowsUpdating += () => publishWatch.Restart();
        diff.RowsUpdated += () => { publishWatch.Stop(); maxPublish = Math.Max(maxPublish, publishWatch.Elapsed.TotalMilliseconds); };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            pulse.Start(); last = clock.Elapsed;
            var watch = Stopwatch.StartNew(); await diff.DiffCommand.ExecuteAsync(null); var load = watch.Elapsed.TotalMilliseconds;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); pulse.Stop();
            Assert.NotNull(diff.Session);
            phase = "filter"; last = clock.Elapsed; pulse.Start();
            watch.Restart(); await diff.SetComparisonModeAsync(DatComparisonMode.AllDifferences); var filter = watch.Elapsed.TotalMilliseconds;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); pulse.Stop();
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ComparisonGrid");
            var row = diff.FindVisibleRow($"items[{count / 2}].value")!;
            grid.SelectedItem = row; grid.ScrollIntoView(row);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var changes = 0; var collectionEvents = 0;
            System.ComponentModel.PropertyChangedEventHandler onChange = (_, _) => changes++;
            foreach (var item in AllVisuals<DataGridRow>(grid).Select(r => r.Item).OfType<DatRowViewModel>().Append(diff.FindVisibleRow("items[1].value")!)) item.PropertyChanged += onChange;
            diff.Rows.CollectionChanged += (_, _) => collectionEvents++;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            phase = "single"; last = clock.Elapsed; pulse.Start();
            watch.Restart(); await diff.ApplyLeftRowCommand.ExecuteAsync(row); var apply = watch.Elapsed.TotalMilliseconds; var singleTiming = diff.LastEditTiming;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); pulse.Stop();
            Assert.Same(row, diff.SelectedRow); Assert.False(row.ValueDiffers);
            Assert.Equal(0, collectionEvents); Assert.InRange(changes, 1, 300);
            Assert.True(AllVisuals<DataGridRow>(grid).Count() < 100);
            Assert.True(diff.CachedRowCount < 100);
            var beforeStructure = diff.Session!.Right.Current;
            var offset = Find<ScrollViewer>(grid)!.VerticalOffset;
            var structuralRow = diff.FindVisibleRow("items[0].extra")!;
            diff.EditorText = "uncommitted draft";
            var cancelled = diff.ApplyLeftRowCommand.ExecuteAsync(structuralRow);
            Assert.True(diff.IsBusy); Assert.True(diff.CanCancel); Assert.True(grid.IsEnabled);
            Render(window, "dat-large-busy.png");
            diff.CancelCommand.Execute(null); await cancelled;
            Assert.Same(beforeStructure, diff.Session.Right.Current); Assert.Equal("uncommitted draft", diff.EditorText);
            Assert.True(diff.HasPendingEdit);
            diff.CancelEditCommand.Execute(null);
            phase = "structure"; last = clock.Elapsed; pulse.Start();
            watch.Restart(); await diff.ApplyLeftRowCommand.ExecuteAsync(structuralRow);
            var structure = watch.Elapsed.TotalMilliseconds;
            Assert.False(diff.Session.Root.HasDifference); Assert.Same(row, diff.SelectedRow);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            pulse.Stop();
            Assert.Equal(offset, Find<ScrollViewer>(grid)!.VerticalOffset);
            phase = "undo"; last = clock.Elapsed; pulse.Start();
            await diff.UndoCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); pulse.Stop();
            Assert.Same(beforeStructure, diff.Session.Right.Current);
            Assert.True(pulses > 3);
            Assert.InRange(maxPublish, 0, 100);
            Assert.InRange(singleTiming.Start, 0, 100);
            Assert.InRange(singleTiming.Commit, 0, 100);
            Assert.InRange(singleTiming.Finish, 0, 100);
            var p95 = intervals.Order().ElementAt((int)Math.Ceiling(intervals.Count * 0.95) - 1);
            _performanceOutput.WriteLine($"elements={count:N0}, rows={diff.Rows.Count:N0}, load={load:F1}ms, filter={filter:F1}ms, single={apply:F1}ms, structure={structure:F1}ms, dispatcherP95={p95:F1}ms, maxDispatcherGap={largestGap.TotalMilliseconds:F1}ms, pulses={pulses}, cachedRows={diff.CachedRowCount}, publish={maxPublish:F1}ms, singlePhases={singleTiming}, gaps={string.Join(",", gaps)}, notifications={changes}, realized={AllVisuals<DataGridRow>(grid).Count()}");
            Render(window, "dat-large-responsive.png");
        }
        finally { pulse.Stop(); window.Close(); }
    });
}

[CollectionDefinition("DAT performance", DisableParallelization = true)]
public sealed class DatPerformanceCollection { }
