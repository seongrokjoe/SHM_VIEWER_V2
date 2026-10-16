using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using ShmViewer.Views.Dialogs;
using ShmViewer.Core.Dat;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    [Fact]
    public Task MemberReplacementDialogExplainsPreservedValuesAndDefaultsToCancel() => RunSta(async () =>
    {
        using var setup = await DatMemberReplacementViewModelTests.CreateSetup(); var diff = setup.Model;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ComparisonGrid");
            var row = diff.FindVisibleRow("initSCU[0].Init_F_Fix[0].RefillOn")!; var before = diff.Session!.Left.Current;
            grid.SelectedItem = row; grid.ScrollIntoView(row);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var scroll = AllVisuals<ScrollViewer>(grid).First(); scroll.ScrollToVerticalOffset(diff.Rows.IndexOf(row));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var offset = scroll.VerticalOffset;
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
            Assert.Equal("#FF6A2D32", ((SolidColorBrush)container.Background).Color.ToString());
            var labels = AllVisuals<TextBlock>(container).Select(t => t.Text).ToArray();
            Assert.Contains("initSCU[0].Init_F_Fix[0].nSpare", labels);
            Assert.Contains("initSCU[0].Init_F_Fix[0].RefillOn", labels);
            Assert.Contains(labels, text => text.Contains("동일 배치 · 이름 다름"));
            Assert.Equal(2, AllVisuals<Border>(container).Count(b => b.Background is SolidColorBrush brush &&
                brush.Color.ToString() == "#FF6A2D32" && b.Padding.Left == 6));
            Render(window, "dat-member-paired-before.png");
            var accepted = false; var calls = 0;
            diff.ConfirmMemberReplacement = plan =>
            {
                Assert.True(window.Dispatcher.CheckAccess()); calls++;
                var dialog = new DatMemberReplacementDialog(plan) { Left = -12000, Top = -12000, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Owner = window };
                try
                {
                    dialog.Show(); dialog.UpdateLayout();
                    var text = string.Join("\n", AllVisuals<TextBox>(dialog).Select(t => t.Text));
                    Assert.Contains("버전2 → 버전1", text); Assert.Contains("initSCU[*].Init_F_Fix[*].RefillOn", text);
                    Assert.Contains("initSCU[*].Init_F_Fix[*].nSpare 전체 6개", text);
                    Assert.Contains("다른 멤버 값과 패딩 바이트는 버전1", text); Assert.Contains("전체 6개", text);
                    var cancel = AllVisuals<Button>(dialog).Single(b => Equals(b.Content, "취소"));
                    var apply = AllVisuals<Button>(dialog).Single(b => Equals(b.Content, "해당 필드만 적용"));
                    Assert.True(cancel.IsDefault); Assert.True(cancel.IsCancel); Assert.False(apply.IsDefault);
                    Render(dialog, "dat-member-replacement.png");
                    dialog.Width = dialog.MinWidth; dialog.UpdateLayout(); Render(dialog, "dat-member-replacement-minimum.png");
                    (accepted ? apply : cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return dialog.Confirmed;
                }
                finally { dialog.Close(); }
            };
            var arrow = AllVisuals<Button>(container).Single(b => Equals(b.Content, "←"));
            typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(arrow, null);
            await diff.ApplyRightRowCommand.ExecutionTask!;
            Assert.Same(before, diff.Session.Left.Current);
            accepted = true;
            typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(arrow, null);
            await diff.ApplyRightRowCommand.ExecutionTask!;
            Assert.Equal(2, calls); Assert.Empty(setup.Errors);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(offset, scroll.VerticalOffset); Assert.Same(row, grid.SelectedItem);
            Assert.Equal(row.LeftPath, row.RightPath); Assert.False(row.NameDiffers);
            Assert.Null(diff.FindVisibleRow("initSCU[0].Init_F_Fix[0].nSpare"));
            Assert.Equal("1000", diff.FindVisibleRow("initSCU[0].Init_F_Fix[0].kept")!.LeftValue);
            Render(window, "dat-member-replacement-result.png");
            await diff.UndoCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(row, grid.SelectedItem); Assert.Equal(offset, scroll.VerticalOffset); Assert.True(row.NameDiffers);
            Render(window, "dat-member-paired-undo.png");
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public Task LargePairedRowsKeepVirtualizationSelectionAndScroll(int count) => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        foreach (var (input, name, value) in new[] { (diff.Left, "nSpare", (byte)0x11), (diff.Right, "RefillOn", (byte)0x22) })
        {
            var schema = DatBinaryTests.Parse($"struct Fix {{ short {name}; int kept; }}; struct Root {{ Fix items[{count}]; }};");
            var data = Enumerable.Repeat(value, schema.Root.Size).ToArray();
            File.WriteAllBytes(input.DatPath, data);
            File.WriteAllBytes(DatSchemaFile.PathFor(input.DatPath), DatSchemaFile.Serialize(schema, data));
            input.UseSchemaFile = true;
        }
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        var ticks = 0; var pulse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        pulse.Tick += (_, _) => ticks++;
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); pulse.Start();
            var timer = Stopwatch.StartNew(); await diff.DiffCommand.ExecuteAsync(null); var load = timer.Elapsed.TotalMilliseconds;
            await diff.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
            Assert.Empty(setup.Errors); Assert.Equal(count, diff.Session!.Nodes.Count(n => n.HasNameDifference));
            Assert.Equal(1, diff.Session.Comparison.MemberPairing.CachedTypePairs);
            var grid = (DataGrid)Find<DatDiffView>(window)!.FindName("ComparisonGrid");
            var row = diff.FindVisibleRow($"items[{count / 2}].RefillOn")!;
            grid.SelectedItem = row; grid.ScrollIntoView(row);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var scroll = AllVisuals<ScrollViewer>(grid).First(); var offset = scroll.VerticalOffset; var rows = diff.Rows.Count;
            var cachedBefore = diff.CachedRowCount;
            var resets = 0; diff.Rows.CollectionChanged += (_, _) => resets++;
            diff.ConfirmMemberReplacement = plan => { Assert.Equal(count, plan.MemberReplacement!.AffectedCount); return true; };
            timer.Restart(); await diff.ApplyRightRowCommand.ExecuteAsync(row); var apply = timer.Elapsed.TotalMilliseconds;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var cachedAfter = diff.CachedRowCount;
            Assert.Empty(setup.Errors); Assert.False(row.NameDiffers); Assert.Same(row, grid.SelectedItem);
            Assert.Equal(rows, diff.Rows.Count); Assert.Equal(offset, scroll.VerticalOffset);
            Assert.Equal("286331153", diff.FindVisibleRow($"items[{count / 2}].kept")!.LeftValue);
            await diff.UndoCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(row.NameDiffers); Assert.Same(row, grid.SelectedItem); Assert.Equal(offset, scroll.VerticalOffset);
            _performanceOutput.WriteLine($"paired elements={count:N0}, load={load:F1}ms, apply={apply:F1}ms, rows={rows:N0}, cachedRows={cachedBefore}/{cachedAfter}/{diff.CachedRowCount}, realized={AllVisuals<DataGridRow>(grid).Count()}, dispatcherTicks={ticks}");
            Assert.Equal(0, resets);
            Assert.True(diff.CachedRowCount < 100, $"Cached rows: {diff.CachedRowCount}");
            Assert.True(AllVisuals<DataGridRow>(grid).Count() < 100); Assert.True(ticks > 3);
        }
        finally { pulse.Stop(); window.Close(); }
    });
}
