using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using ShmViewer.Views.Dialogs;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    [Fact]
    public Task SingleStructureDraftGhostAndTypeDialogRenderAndKeepSelection() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        File.WriteAllText(diff.Left.Headers[0].Path, DatSingleStructureTests.Header);
        File.WriteAllBytes(diff.Left.DatPath, DatSingleStructureTests.Session().Bytes.ToArray());
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            diff.SingleLoad = true; await diff.DiffCommand.ExecuteAsync(null);
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ViewerGrid");
            await diff.ToggleViewerCommand.ExecuteAsync(diff.ViewerRows.Single(r => r.Path == "items"));
            await diff.ToggleViewerCommand.ExecuteAsync(diff.ViewerRows.Single(r => r.Path == "items[0]"));
            grid.SelectedItem = diff.ViewerRows.Single(r => r.Path == "items[0].a");
            await diff.AddSingleCommand.ExecuteAsync("after");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var draftRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(diff.SingleDraft);
            var editor = AllVisuals<TextBox>(draftRow).Single(t => t.Name == "SingleNameEditor");
            Assert.True(editor.IsVisible); editor.Text = "RefillOn"; editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal("RefillOn", diff.SingleNameText); Render(window, "single-load-draft.png");
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.NotNull(diff.ApplySingleInputCommand.ExecutionTask);
            await diff.ApplySingleInputCommand.ExecutionTask;
            Assert.True(string.IsNullOrEmpty(diff.SingleError), diff.SingleError);
            Assert.NotNull(diff.SelectedViewerRow);
            var added = diff.SelectedViewerRow!; Assert.Equal("items[0].RefillOn", added.Path); Assert.Equal("추가", added.State);
            await diff.RenameSingleCommand.ExecuteAsync(added); diff.SingleNameText = "RefillEnabled";
            await diff.ApplySingleInputCommand.ExecuteAsync(null);
            Assert.Same(added, grid.SelectedItem); Assert.NotNull(diff.ViewerSession!.Find("items[2].RefillEnabled"));
            await diff.DeleteSingleCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(added, grid.SelectedItem); Assert.True(added.Entry.IsDeleted);
            var deletedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(added);
            Assert.Contains(AllVisuals<TextBlock>(deletedRow), t => t.Text == added.Path && t.TextDecorations.Count > 0);
            Render(window, "single-load-deleted.png");
            grid.SelectedItem = diff.ViewerRows[0];
            deletedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            Assert.Same(added, grid.SelectedItem);
            var menu = grid.ContextMenu; menu.PlacementTarget = grid; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var restore = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "복구"));
            Assert.True(restore.IsEnabled); Assert.Same(diff.RestoreSingleCommand, restore.Command);
            menu.IsOpen = false;
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)restore.Command).ExecuteAsync(restore.CommandParameter);
            Assert.Same(added, grid.SelectedItem);
            await diff.UndoCommand.ExecuteAsync(null); Assert.True(added.Entry.IsDeleted);
            await diff.RedoCommand.ExecuteAsync(null); Assert.False(added.Entry.IsDeleted);
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(grid.ActualHeight > 80); Render(window, "single-load-minimum.png");
            window.Width = 1440; window.Height = 900;
            var dialog = new DatTypeDialog(diff.SingleTypes, "long[10]") { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000, ShowActivated = false };
            try
            {
                dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var preview = AllVisuals<Button>(dialog).Single(b => Equals(b.Content, "크기 확인"));
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var timeout = Stopwatch.StartNew();
                while (!preview.IsEnabled && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                Assert.Contains(AllVisuals<TextBlock>(dialog), t => t.Text.Contains("40 B"));
                Render(dialog, "single-load-type-dialog.png");
                Assert.Contains(AllVisuals<ComboBox>(dialog), c => c.Items.OfType<DatTypeChoice>().Any(t => t.Name == "long"));
            }
            finally { dialog.Close(); }
            Assert.Empty(setup.Errors);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public Task SingleLoadLargeArrayKeepsRowsVirtualAndPreservesValues(int count) => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        var document = DatPerformanceTests.Documents(count).Left;
        File.WriteAllBytes(diff.Left.DatPath, document.Data);
        File.WriteAllBytes(DatSchemaFile.PathFor(diff.Left.DatPath), DatSchemaFile.Serialize(document.Schema, document.Data));
        diff.Left.UseSchemaFile = true; diff.SingleLoad = true;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        var clock = Stopwatch.StartNew(); var last = clock.Elapsed; var gaps = new List<double>();
        var pulse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        pulse.Tick += (_, _) => { var now = clock.Elapsed; gaps.Add((now - last).TotalMilliseconds); last = now; };
        var publish = new Stopwatch(); var maxPublish = 0.0;
        diff.ViewerRowsUpdating += () => publish.Restart();
        diff.ViewerRowsUpdated += () => maxPublish = Math.Max(maxPublish, publish.Elapsed.TotalMilliseconds);
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); last = clock.Elapsed; pulse.Start();
            var watch = Stopwatch.StartNew(); await diff.DiffCommand.ExecuteAsync(null); var load = watch.Elapsed.TotalMilliseconds;
            var session = diff.ViewerSession!; Assert.NotNull(session);
            await diff.ToggleViewerCommand.ExecuteAsync(diff.ViewerRows.Get(session.Find("items")!.Id));
            await diff.ToggleViewerCommand.ExecuteAsync(diff.ViewerRows.Get(session.Find($"items[{count / 2}]")!.Id));
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ViewerGrid");
            var row = diff.ViewerRows.Get(session.Find($"items[{count / 2}].value")!.Id);
            grid.SelectedItem = row; grid.ScrollIntoView(row); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await diff.RenameSingleCommand.ExecuteAsync(row); diff.SingleNameText = "renamed";
            watch.Restart(); await diff.ApplySingleInputCommand.ExecuteAsync(null); var rename = watch.Elapsed.TotalMilliseconds;
            Assert.Same(row, diff.SelectedViewerRow); Assert.Equal((count / 2 + 1).ToString(), row.Value);
            Assert.NotNull(session.Find($"items[{count - 1}].renamed"));
            var before = session.Document; await diff.AddSingleCommand.ExecuteAsync("after"); diff.SingleNameText = "extra";
            var running = diff.ApplySingleInputCommand.ExecuteAsync(null); diff.CancelCommand.Execute(null); await running;
            Assert.Same(before, session.Document); Assert.True(diff.HasSingleInput);
            watch.Restart(); await diff.ApplySingleInputCommand.ExecuteAsync(null); var insert = watch.Elapsed.TotalMilliseconds;
            Assert.True(string.IsNullOrEmpty(diff.SingleError), diff.SingleError);
            Assert.NotNull(diff.SelectedViewerRow);
            var added = diff.SelectedViewerRow!; Assert.Equal("0", added.Value);
            await diff.DeleteSingleCommand.ExecuteAsync(null); Assert.True(added.Entry.IsDeleted);
            await diff.RestoreSingleCommand.ExecuteAsync(null); Assert.False(added.Entry.IsDeleted);
            await diff.UndoCommand.ExecuteAsync(null); Assert.True(added.Entry.IsDeleted);
            diff.ChooseSingleType = (catalog, _) => Task.FromResult<BinaryType?>(catalog.Resolve("long long"));
            watch.Restart(); await diff.ChangeSingleTypeCommand.ExecuteAsync(row); var typeChange = watch.Elapsed.TotalMilliseconds;
            Assert.Equal("long long", row.TypeName);
            Assert.Equal("long long", session.Find($"items[{count - 1}].renamed")!.Field.Type.Name);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); pulse.Stop();
            Assert.Equal((count / 2 + 1).ToString(), row.Value); Assert.Empty(setup.Errors);
            Assert.InRange(AllVisuals<DataGridRow>(grid).Count(), 1, 80);
            Assert.InRange(diff.ViewerRows.CachedCount, 1, 200); Assert.True(gaps.Count > 3);
            _performanceOutput.WriteLine($"Single Load elements={count:N0}, load={load:F1}ms, rename={rename:F1}ms, insert={insert:F1}ms, type={typeChange:F1}ms, publish={maxPublish:F1}ms, dispatcherP95={gaps.Order().ElementAt((int)(gaps.Count * .95)):F1}ms, maxGap={gaps.Max():F1}ms, cached={diff.ViewerRows.CachedCount}");
            _performanceOutput.WriteLine($"Publish stages: {diff.MaxSinglePublishTiming}");
            Assert.InRange(maxPublish, 0, 100);
        }
        finally { pulse.Stop(); window.Close(); }
    });
}
